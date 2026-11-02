using System.Runtime.InteropServices.WindowsRuntime;
using HIDMaestro;
using Windows.Devices.Bluetooth.Advertisement;

namespace Switch2Bridge;

/// <summary>
/// Watches for Switch 2 controllers, runs one Bluetooth session per controller and
/// routes each to its virtual pad: a Pro Controller gets its own, Joy-Cons pair up.
/// </summary>
sealed class Bridge : IDisposable
{
    const ushort NintendoManufacturerId = 0x0553;
    static readonly TimeSpan PartnerWait = TimeSpan.FromSeconds(6);

    readonly HMContext _ctx;
    readonly ulong _hostAddress;
    readonly CancellationToken _ct;
    readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    readonly SemaphoreSlim _connectGate = new(1, 1);
    readonly SemaphoreSlim _padGate = new(1, 1);
    readonly object _gate = new();
    readonly HashSet<ulong> _active = [];
    readonly Dictionary<ulong, FlashDump> _flashCache = [];
    readonly List<VirtualPad> _pads = [];
    readonly List<(SwitchController Controller, FlashDump? Flash, TaskCompletionSource<VirtualPad> Pad)> _waiting = [];

    // After the bridge turns a controller off, ignore its adverts briefly: they are its
    // last reconnect attempts, not the user pressing a button.
    static readonly TimeSpan QuietAfterPowerOff = TimeSpan.FromSeconds(10);
    readonly Dictionary<ulong, long> _quietUntil = [];

    /// <summary>Raised for things worth a notification: connects, disconnects, low battery.</summary>
    public event Action<string>? Changed;

    public Bridge(HMContext ctx, ulong hostAddress, CancellationToken ct)
    {
        _ctx = ctx;
        _hostAddress = hostAddress;
        _ct = ct;
        _watcher.Received += OnAdvertisement;
    }

    public void Start()
    {
        _watcher.Start();
        _ = Task.Run(IdleLoop);
        _ = Task.Run(HealthLoop);
        Log.Info("Press a button on a paired controller, or hold Sync on a new one to pair it.");
    }

    /// <summary>Once a second: restarts the advertisement watcher if Windows stopped it (sleep,
    /// Bluetooth toggled), ends sessions whose reports have stopped so the controller can
    /// reconnect, and passes on player lights Steam has set.</summary>
    async Task HealthLoop()
    {
        var staleAfter = TimeSpan.FromSeconds(5);
        while (!_ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), _ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Stopped or BluetoothLEAdvertisementWatcherStatus.Aborted)
                {
                    _watcher.Start();
                    Log.Info("Restarted listening for controllers.");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Warn($"Couldn't restart listening for controllers: {e.Message}");
            }
            VirtualPad[] pads;
            lock (_gate) pads = [.. _pads];
            foreach (var pad in pads)
            {
                pad.EndStaleSessions(staleAfter);
                await pad.SyncPlayerLightsAsync(_ct);
            }
        }
    }

    /// <summary>Turns off controllers left without input for the configured time.</summary>
    async Task IdleLoop()
    {
        while (!_ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), _ct); }
            catch (OperationCanceledException) { return; }
            int minutes = Settings.Current.IdleOffMinutes;
            if (minutes <= 0) continue;
            VirtualPad[] pads;
            lock (_gate) pads = [.. _pads];
            foreach (var pad in pads.Where(p => p.IsLive && p.IdleFor >= TimeSpan.FromMinutes(minutes)))
            {
                await pad.PowerOffAsync($"no input for {minutes} min");
                Changed?.Invoke($"Player {pad.Number} turned off after {minutes} min idle");
            }
        }
    }

    /// <summary>Turns off every connected controller, waiting at most a few seconds.</summary>
    public async Task PowerOffAllAsync(string reason)
    {
        VirtualPad[] pads;
        lock (_gate) pads = [.. _pads];
        var all = Task.WhenAll(pads.Where(p => p.IsLive).Select(p => p.PowerOffAsync(reason)));
        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(4)));
    }

    /// <summary>Runs the audio probe on the first connected Pro Controller; null if none is connected.</summary>
    public async Task<string?> RunAudioProbeAsync(IProgress<string> progress)
    {
        VirtualPad? pad;
        lock (_gate) pad = _pads.FirstOrDefault(p => p.LivePro != null);
        return pad == null ? null : await pad.RunAudioTestAsync(progress, _ct);
    }

    /// <summary>Keeps a player's input from Steam until disposed; null if there's no such player.</summary>
    public IDisposable? HoldIsolation(int player)
    {
        lock (_gate) return _pads.FirstOrDefault(p => p.Number == player)?.HoldIsolation();
    }

    /// <summary>Plays the audio test sounds on the first connected Pro Controller; null if none is connected.</summary>
    public async Task<string?> RunAudioLabAsync(Action<AudioLab> ready, IProgress<string> progress, CancellationToken stop)
    {
        VirtualPad? pad;
        lock (_gate) pad = _pads.FirstOrDefault(p => p.LivePro != null);
        if (pad == null) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, stop);
        return await pad.RunAudioLabAsync(ready, progress, linked.Token);
    }

    public Task TestRumbleAsync(int player)
    {
        VirtualPad? pad;
        lock (_gate) pad = _pads.FirstOrDefault(p => p.Number == player);
        return pad?.TestRumbleAsync() ?? Task.CompletedTask;
    }

    public string[] Status()
    {
        VirtualPad[] pads;
        lock (_gate) pads = [.. _pads];
        return pads.Length == 0 ? ["No controllers yet"] : pads.Select(p => p.Describe()).ToArray();
    }

    public PadStatus[] Snapshot()
    {
        VirtualPad[] pads;
        lock (_gate) pads = [.. _pads];
        return pads.Select(p => p.Snapshot()).ToArray();
    }

    public bool AnyLive
    {
        get { lock (_gate) return _pads.Any(p => p.IsLive); }
    }

    void OnAdvertisement(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        foreach (var m in e.Advertisement.ManufacturerData)
        {
            if (m.CompanyId != NintendoManufacturerId) continue;
            var data = m.Data.ToArray();
            if (data.Length < 16) continue;
            ushort vid = BitConverter.ToUInt16(data, 3), pid = BitConverter.ToUInt16(data, 5);
            if (vid != SwitchController.NintendoVid || SwitchController.KindOf(pid) == null) continue;
            ulong reconnectTo = 0;
            for (int i = 5; i >= 0; i--) reconnectTo = (reconnectTo << 8) | data[10 + i];
            if (reconnectTo == _hostAddress) Offer(e.BluetoothAddress, needsPairing: false);
            else if (reconnectTo == 0) Offer(e.BluetoothAddress, needsPairing: true);
        }
    }

    void Offer(ulong address, bool needsPairing)
    {
        lock (_gate)
        {
            if (_quietUntil.TryGetValue(address, out var until) && Environment.TickCount64 < until) return;
            if (_ct.IsCancellationRequested || !_active.Add(address)) return;
        }
        _ = Task.Run(async () =>
        {
            try { await RunSession(address, needsPairing); }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { }
            catch (Exception e) { Log.Error($"{SwitchController.Format(address)}: {e.Message}"); }
            finally
            {
                lock (_gate) _active.Remove(address);
            }
        });
    }

    async Task RunSession(ulong address, bool needsPairing)
    {
        SwitchController controller;
        FlashDump? flash;
        var started = System.Diagnostics.Stopwatch.StartNew();

        // One connection set-up at a time: Windows' LE stack copes poorly with
        // several GATT discoveries racing each other.
        await _connectGate.WaitAsync(_ct);
        try
        {
            Log.Info($"Connecting to {SwitchController.Format(address)}...");
            controller = await SwitchController.ConnectAsync(address, _ct);
            try
            {
                if (needsPairing)
                {
                    await controller.PairAsync(_hostAddress, _ct);
                    Log.Ok($"{controller.Name} paired. It will reconnect to this PC when you press a button.");
                }
                flash = await ReadFlash(controller);
            }
            catch
            {
                await controller.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }

        await using var owned = controller;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Disconnected += () => disconnected.TrySetResult();

        var pad = await PadFor(controller, flash);
        pad.Attach(controller);
        try
        {
            await controller.StartInputAsync(_ct);
            await controller.SetPlayerLedsAsync(pad.CurrentLights, _ct);
            Log.Ok($"{controller.Name} connected as player {pad.Number} in {started.ElapsedMilliseconds} ms.");
            pad.SetFirmware(controller.Kind, await controller.ReadFirmwareAsync(_ct), controller.HasHeadsetChannel);
            _ = controller.RequestFastLinkAsync(_ct).ContinueWith(_ => { }, TaskScheduler.Default);
            Changed?.Invoke($"{controller.Name} connected (player {pad.Number})");

            using var session = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            var powerPolling = PollPowerAsync(controller, pad, session.Token);
            try { await disconnected.Task.WaitAsync(_ct); }
            finally
            {
                session.Cancel();
                await powerPolling;
            }
            if (controller.PoweredOff)
            {
                lock (_gate) _quietUntil[address] = Environment.TickCount64 + (long)QuietAfterPowerOff.TotalMilliseconds;
                Log.Info($"{controller.Name} (player {pad.Number}) turned off. Press Home on it to wake it.");
            }
            else
            {
                Log.Warn($"{controller.Name} (player {pad.Number}) disconnected. Press a button on it to reconnect.");
                Changed?.Invoke($"{controller.Name} disconnected");
            }
        }
        finally
        {
            pad.Detach(controller);
        }
    }

    /// <summary>Refreshes a controller's battery level every 30 seconds while it's connected.</summary>
    static async Task PollPowerAsync(SwitchController controller, VirtualPad pad, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            while (true)
            {
                if (await controller.ReadPowerAsync(ct) is { } power) pad.SetPower(controller.Kind, power);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task<FlashDump?> ReadFlash(SwitchController controller)
    {
        lock (_gate)
            if (_flashCache.TryGetValue(controller.Address, out var cached)) return cached;
        try
        {
            var flash = new FlashDump(
                await controller.ReadFlashAsync(FlashDump.FactoryAddress, FlashDump.FactoryLength, _ct),
                await controller.ReadFlashAsync(FlashDump.UserAddress, FlashDump.UserLength, _ct));
            lock (_gate) _flashCache[controller.Address] = flash;
            return flash;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"Could not read {controller.Name} calibration ({e.Message}); using defaults.");
            return null;
        }
    }

    /// <summary>Finds or creates the pad a controller belongs to. A reconnecting controller
    /// returns to its own pad. A new Joy-Con joins a pad missing its side, or waits a few
    /// seconds for its partner so both calibrations go into one pad.</summary>
    async Task<VirtualPad> PadFor(SwitchController controller, FlashDump? flash)
    {
        TaskCompletionSource<VirtualPad> waiting;
        await _padGate.WaitAsync(_ct);
        try
        {
            VirtualPad? pad;
            lock (_gate)
            {
                pad = _pads.FirstOrDefault(p => p.Owns(controller.Address) && p.HasRoomFor(controller.Kind, controller.Address))
                    ?? (controller.Kind == ControllerKind.ProController ? null
                        : _pads.FirstOrDefault(p => !p.Sideways && p.HasRoomFor(controller.Kind, controller.Address) && !p.Owns(controller.Address)
                            && (controller.Kind == ControllerKind.JoyConLeft ? p.LeftAddress == null : p.RightAddress == null)));
            }
            if (pad != null) return pad;

            if (controller.Kind == ControllerKind.ProController)
                return NewPad(isJoyConPad: false, flash, null, null, controller.Address);

            // Set to play sideways: its own controller, with no waiting for a partner. Its stick
            // becomes the left stick, so its calibration goes in the first slot whichever side it is.
            if (Settings.ControllerSideways(controller.Address))
                return NewPad(isJoyConPad: true, null, flash, null, controller.Address);

            var partnerKind = controller.Kind == ControllerKind.JoyConLeft ? ControllerKind.JoyConRight : ControllerKind.JoyConLeft;
            int partnerIndex = _waiting.FindIndex(w => w.Controller.Kind == partnerKind);
            if (partnerIndex >= 0)
            {
                var partner = _waiting[partnerIndex];
                _waiting.RemoveAt(partnerIndex);
                var (left, right) = controller.Kind == ControllerKind.JoyConLeft ? (flash, partner.Flash) : (partner.Flash, flash);
                var leftAddress = controller.Kind == ControllerKind.JoyConLeft ? controller.Address : partner.Controller.Address;
                pad = NewPad(isJoyConPad: true, null, left, right, leftAddress);
                partner.Pad.TrySetResult(pad);
                return pad;
            }

            waiting = new TaskCompletionSource<VirtualPad>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((controller, flash, waiting));
            Log.Info($"{controller.Name} connected; waiting a few seconds for the other Joy-Con...");
        }
        finally
        {
            _padGate.Release();
        }

        try { return await waiting.Task.WaitAsync(PartnerWait, _ct); }
        catch (TimeoutException) { }

        await _padGate.WaitAsync(_ct);
        try
        {
            if (waiting.Task.IsCompletedSuccessfully) return waiting.Task.Result;
            _waiting.RemoveAll(w => w.Pad == waiting);
            Log.Info($"{controller.Name} is on its own; the other Joy-Con can join later.");
            return controller.Kind == ControllerKind.JoyConLeft
                ? NewPad(isJoyConPad: true, null, flash, null, controller.Address)
                : NewPad(isJoyConPad: true, null, null, flash, controller.Address);
        }
        finally
        {
            _padGate.Release();
        }
    }

    VirtualPad NewPad(bool isJoyConPad, FlashDump? pro, FlashDump? left, FlashDump? right, ulong identityAddress)
    {
        int number;
        lock (_gate)
            number = ChoosePlayer(Settings.ControllerPlayer(identityAddress), _pads.Select(p => p.Number).ToHashSet(),
                Settings.PlayersReservedByOthers(identityAddress));
        Settings.RememberPlayer(identityAddress, number);
        Log.Info($"Creating virtual wired Pro Controller 2 for player {number}...");
        var pad = VirtualPad.Create(_ctx, number, isJoyConPad, pro, left, right, $"switch2bridge-{identityAddress:X12}",
            message => Changed?.Invoke(message), identityAddress);
        lock (_gate) _pads.Add(pad);
        return pad;
    }

    /// <summary>A controller's remembered player if free; otherwise the lowest number that's
    /// free and not remembered by another controller; otherwise the lowest free one.</summary>
    internal static int ChoosePlayer(int? remembered, IReadOnlySet<int> inUse, IReadOnlySet<int> reserved)
    {
        if (remembered is int player && !inUse.Contains(player)) return player;
        for (int n = 1; n <= 8; n++)
            if (!inUse.Contains(n) && !reserved.Contains(n)) return n;
        int free = 1;
        while (inUse.Contains(free)) free++;
        return free;
    }

    public void SetSideways(int player, bool sideways)
    {
        VirtualPad? pad;
        lock (_gate) pad = _pads.FirstOrDefault(p => p.Number == player);
        pad?.SetSideways(sideways);
    }

    public void RenamePlayer(int player, string name)
    {
        VirtualPad? pad;
        lock (_gate) pad = _pads.FirstOrDefault(p => p.Number == player);
        if (pad != null) Settings.SetControllerName(pad.IdentityAddress, name);
    }

    public void Dispose()
    {
        _watcher.Stop();
        VirtualPad[] pads;
        lock (_gate)
        {
            pads = [.. _pads];
            _pads.Clear();
        }
        foreach (var pad in pads) pad.Dispose();
    }
}
