using System.Text.Json.Nodes;
using HIDMaestro;

namespace Switch2Bridge;

/// <summary>A controller's flash: device info and factory calibration from 0x13000,
/// user stick calibration from 0x1FC040.</summary>
sealed record FlashDump(byte[] Factory, byte[] User)
{
    public const uint FactoryAddress = 0x13000;
    public const int FactoryLength = 0x140;
    public const uint UserAddress = 0x1FC040;
    public const int UserLength = 0x40;
}

/// <summary>
/// One virtual wired Pro Controller 2 (HIDMaestro's switch2-pro-controller-composite
/// persona), fed by either a Pro Controller 2 or a left + right Joy-Con pair. Steam
/// keeps seeing the same device across Bluetooth reconnects, because the pad
/// outlives each Bluetooth session.
/// </summary>
sealed class VirtualPad : IDisposable
{
    // The stock persona's 24-byte state block, plus 4 bytes this bridge adds for battery
    // voltage and current (the persona otherwise sends a fixed 3.8 V).
    internal const int StateBlockSize = 28;
    const int ReportLength = 63;
    const int BatteryAt = 31; // Bluetooth report: voltage (mV, u16) then current (u16)
    const ushort DefaultBatteryMv = 3800;

    // Bluetooth input report button bit -> bit in HIDMaestro's state block for the
    // switch2-pro-controller-composite profile (its stateBlock and report 0x05 "bits" table).
    static readonly (uint Ble, int State)[] ButtonMap =
    [
        (0x00000004, 0),  // B
        (0x00000008, 1),  // A
        (0x00000001, 2),  // Y
        (0x00000002, 3),  // X
        (0x00000040, 4),  // R
        (0x00000080, 5),  // ZR
        (0x00000200, 6),  // Plus
        (0x00000400, 7),  // Right stick click
        (0x00010000, 8),  // D-pad down
        (0x00040000, 9),  // D-pad right
        (0x00080000, 10), // D-pad left
        (0x00020000, 11), // D-pad up
        (0x00400000, 12), // L
        (0x00800000, 13), // ZL
        (0x00000100, 14), // Minus
        (0x00000800, 15), // Left stick click
        (0x00001000, 16), // Home
        (0x00002000, 17), // Capture
        (0x01000000, 18), // GR
        (0x02000000, 19), // GL
        (0x00004000, 20), // C
    ];

    // Which Bluetooth button bits each Joy-Con owns when two are merged into one pad.
    internal const uint LeftJoyConButtons = 0x00010000 | 0x00020000 | 0x00040000 | 0x00080000
        | 0x00400000 | 0x00800000 | 0x00000100 | 0x00000800 | 0x00002000;
    internal const uint RightJoyConButtons = 0x00000001 | 0x00000002 | 0x00000004 | 0x00000008
        | 0x00000040 | 0x00000080 | 0x00000200 | 0x00000400 | 0x00001000 | 0x00004000;

    static readonly byte[] LedPatterns = [0x01, 0x03, 0x07, 0x0F, 0x09, 0x05, 0x0D, 0x06];

    readonly HMController _pad;
    readonly object _gate = new();
    readonly byte[] _merged = NeutralReport();
    SwitchController? _pro, _left, _right;

    // Status-window figures per controller: reports seen, battery millivolts, and the
    // report rate measured over the last full second.
    readonly Dictionary<ControllerKind, (long Reports, int BatteryMv, byte ChargeState)> _seen = [];
    readonly Dictionary<ControllerKind, (long Reports, long Ticks, int Rate)> _rateWindow = [];
    long _lastRumbleTicks;
    readonly HashSet<ControllerKind> _batteryLogged = [];
    readonly Dictionary<ControllerKind, LinkStats> _link = [];

    // Battery: a sample a minute per controller, and one low-battery warning per connection.
    const long BatterySampleMs = 60_000;
    readonly Dictionary<ControllerKind, long> _lastBatterySample = [];
    readonly Dictionary<ControllerKind, int> _lowSamples = [];
    readonly HashSet<ControllerKind> _lowWarned = [];
    readonly Action<string> _notify;
    readonly Dictionary<ControllerKind, PowerInfo> _power = [];
    readonly Dictionary<ControllerKind, (FirmwareInfo? Info, bool HeadsetChannel)> _firmware = [];
    // The low-battery warning's level. The notes give the 0-9 scale but no meaning for
    // each step, so "1 of 9" is this bridge's choice.
    const int LowLevel = 1;

    // Idle: the last time a button or stick moved (motion alone doesn't count, so a
    // controller left on a desk still goes idle).
    const int StickActivity = 60;
    long _lastActivity = Environment.TickCount64;
    uint _activityButtons;
    int _activityL, _activityR;
    int _rumbleSequence;

    // Rumble: newest packet wins, written by one loop so a slow link never queues up stale rumble.
    byte[]? _pendingRumble;
    readonly SemaphoreSlim _rumbleSignal = new(0, 1);
    readonly CancellationTokenSource _rumbleCts = new();
    readonly Task _rumbleLoop;

    public int Number { get; }
    public bool IsJoyConPad { get; }
    public ulong? ProAddress { get; private set; }
    public ulong? LeftAddress { get; private set; }
    public ulong? RightAddress { get; private set; }
    public byte LedPattern => LedPatterns[(Number - 1) % LedPatterns.Length];

    VirtualPad(int number, bool isJoyConPad, HMController pad, Action<string> notify)
    {
        Number = number;
        IsJoyConPad = isJoyConPad;
        _pad = pad;
        _notify = notify;
        _pad.OutputReceived += OnOutput;
        Submit();
        _rumbleLoop = Task.Run(RumbleLoop);
    }

    /// <summary>The address this pad's name and player number are remembered under.</summary>
    public ulong IdentityAddress { get; private init; }

    public static VirtualPad Create(HMContext ctx, int number, bool isJoyConPad, FlashDump? pro, FlashDump? left, FlashDump? right,
        string identityKey, Action<string> notify, ulong identityAddress)
    {
        var (factory, user) = ComposeFlash(pro, left, right);
        var id = $"switch2-pro-bridge-{number}";
        var json = ProfileJson(id, factory, user);
        var dir = Path.Combine(Log.Directory, "profiles", $"pad{number}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), json.ToJsonString());
        ctx.LoadProfilesFromDirectory(dir);
        var profile = ctx.GetProfile(id) ?? throw new InvalidOperationException("HIDMaestro did not load the bridge profile.");
        return new VirtualPad(number, isJoyConPad, ctx.CreateController(profile, identityKey), notify)
        {
            IdentityAddress = identityAddress,
            _sideways = isJoyConPad && Settings.ControllerSideways(identityAddress),
        };
    }

    public string Describe()
    {
        lock (_gate)
        {
            if (!IsJoyConPad) return $"Player {Number}: Pro Controller 2 ({(_pro != null ? "connected" : "waiting")})";
            string Side(SwitchController? live, ulong? owner) => live != null ? "on" : owner != null ? "waiting" : "-";
            return $"Player {Number}: Joy-Con pair (L {Side(_left, LeftAddress)}, R {Side(_right, RightAddress)})";
        }
    }

    public bool IsLive { get { lock (_gate) return _pro != null || _left != null || _right != null; } }

    /// <summary>Played as one sideways Joy-Con. Never true once both sides have joined.</summary>
    public bool Sideways { get { lock (_gate) return IsSideways; } }
    bool IsSideways => _sideways && IsJoyConPad && (LeftAddress == null || RightAddress == null);
    volatile bool _sideways;

    public void SetSideways(bool sideways)
    {
        lock (_gate)
        {
            _sideways = sideways;
            // Nothing stays held across the change of layout.
            NeutralReport().CopyTo(_merged, 0);
        }
        Settings.SetControllerSideways(IdentityAddress, sideways);
        Submit();
    }

    // Player lights: the bridge's own until Steam sets some.
    byte _steamLights;
    public byte CurrentLights { get { lock (_gate) return _steamLights != 0 ? _steamLights : LedPattern; } }

    /// <summary>Passes on lights Steam has set on the virtual controller, if they've changed.</summary>
    public async Task SyncPlayerLightsAsync(CancellationToken ct)
    {
        byte lights;
        try { lights = (byte)(_pad.DeviceProtocol?.GetRegister("playerLeds") ?? 0); }
        catch (Exception e) when (e is ArgumentException or ObjectDisposedException) { return; }

        SwitchController?[] live;
        lock (_gate)
        {
            if (lights == 0 || lights == _steamLights) return;
            _steamLights = lights;
            live = [_pro, _left, _right];
        }
        Log.Info($"Player {Number}: Steam set the lights to 0x{lights:X2}.");
        foreach (var c in live.OfType<SwitchController>())
        {
            try { await c.SetPlayerLedsAsync(lights, ct); }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException)
            {
                Log.Warn($"{c.Name}: couldn't set Steam's lights: {e.Message}");
            }
        }
    }

    readonly Dictionary<ControllerKind, long> _lastReport = [];

    /// <summary>Ends sessions that have stopped sending reports, so the controller can
    /// reconnect. After sleep, for example, Windows can still call a dead link connected.</summary>
    public void EndStaleSessions(TimeSpan after)
    {
        long now = Environment.TickCount64;
        var stale = new List<SwitchController>();
        lock (_gate)
            foreach (var c in new[] { _pro, _left, _right }.OfType<SwitchController>())
                if (_lastReport.TryGetValue(c.Kind, out var last) && now - last > after.TotalMilliseconds) stale.Add(c);
        foreach (var c in stale)
        {
            Log.Warn($"{c.Name} (player {Number}): no reports for {after.TotalSeconds:0} s, ending the connection.");
            c.EndSession();
        }
    }

    public SwitchController? LivePro { get { lock (_gate) return _pro; } }

    public void SetFirmware(ControllerKind kind, FirmwareInfo? firmware, bool headsetChannel)
    {
        lock (_gate) _firmware[kind] = (firmware, headsetChannel);
        Log.Info($"Player {Number} {kind}: {firmware?.ToString() ?? "firmware unknown"}, headset audio channel {(headsetChannel ? "present" : "absent")}.");
        if (firmware != null) FirmwareSupport.Record(kind, firmware, headsetChannel);
    }

    public void SetPower(ControllerKind kind, PowerInfo power)
    {
        lock (_gate)
        {
            if (!_power.ContainsKey(kind))
                Log.Info($"Player {Number} {kind}: battery level {power.Level} of {PowerInfo.MaxLevel}, external power {power.ExternalPower}, charging {power.Charging}.");
            _power[kind] = power;
        }
    }

    /// <summary>What the status window shows for this pad, right now.</summary>
    public PadStatus Snapshot()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            var sides = new List<SideStatus>();
            void Side(ControllerKind kind, string name, SwitchController? live, ulong? owner)
            {
                if (live == null && owner == null) return;
                _seen.TryGetValue(kind, out var seen);
                if (!_rateWindow.TryGetValue(kind, out var window)) _rateWindow[kind] = window = (seen.Reports, now, 0);
                else if (now - window.Ticks >= 1000)
                    _rateWindow[kind] = window = (seen.Reports, now,
                        (int)Math.Round((seen.Reports - window.Reports) * 1000.0 / (now - window.Ticks)));
                int rate = live != null ? window.Rate : 0;
                int? mv = live != null && seen.BatteryMv > 0 ? seen.BatteryMv : null;
                var (avg, worst, stalls) = live != null && _link.TryGetValue(kind, out var link) ? link.Read(stamp) : (0, 0, 0);
                var power = live != null ? _power.GetValueOrDefault(kind) : null;
                var firmware = live != null && _firmware.TryGetValue(kind, out var f) ? f : (Info: null, HeadsetChannel: false);
                sides.Add(new SideStatus(name, live != null, mv, power?.Charging ?? (live != null && seen.ChargeState != 0), rate,
                    mv is int v && owner is ulong address ? BatteryLog.Percent(address, v) : null,
                    avg, worst, stalls, power?.Level, firmware.Info, firmware.HeadsetChannel,
                    firmware.Info != null && FirmwareSupport.IsTested(kind, firmware.Info.Firmware)));
            }
            if (IsJoyConPad)
            {
                Side(ControllerKind.JoyConLeft, "L", _left, LeftAddress);
                Side(ControllerKind.JoyConRight, "R", _right, RightAddress);
            }
            else Side(ControllerKind.ProController, "Pro", _pro, ProAddress);

            double Axis(int raw) => Math.Clamp(raw / 4095.0, 0, 1);
            int l = _merged[10] | _merged[11] << 8 | _merged[12] << 16;
            int r = _merged[13] | _merged[14] << 8 | _merged[15] << 16;
            double Gyro(int at) => Math.Min(1, Math.Abs(BitConverter.ToInt16(_merged, at)) / 6000.0);
            return new PadStatus(
                Number,
                !IsJoyConPad ? "Pro Controller 2"
                    : LeftAddress != null && RightAddress != null ? "Joy-Con pair"
                    : LeftAddress != null ? "Joy-Con (L)" : "Joy-Con (R)",
                sides,
                Axis(l & 0xFFF), 1 - Axis(l >> 12), Axis(r & 0xFFF), 1 - Axis(r >> 12),
                [Gyro(54), Gyro(56), Gyro(58)],
                now - Interlocked.Read(ref _lastRumbleTicks) < 300,
                BitConverter.ToUInt32(_merged, 4),
                IsIsolated,
                Settings.ControllerName(IdentityAddress),
                IsJoyConPad && (LeftAddress == null || RightAddress == null),
                IsSideways);
        }
    }

    /// <summary>How long since a button was pressed or a stick moved.</summary>
    public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivity));

    /// <summary>Turns off every controller feeding this pad.</summary>
    public async Task PowerOffAsync(string reason)
    {
        SwitchController?[] live;
        lock (_gate) live = [_pro, _left, _right];
        foreach (var c in live.OfType<SwitchController>())
        {
            Log.Info($"Turning off {c.Name} (player {Number}): {reason}.");
            await c.PowerOffAsync();
        }
    }

    /// <summary>A short thump then a click on every controller feeding this pad, at the
    /// current rumble strength, bypassing Steam.</summary>
    public async Task TestRumbleAsync()
    {
        foreach (var (entry, ms) in new[] { (Rumble.Thump, 200), (Rumble.Stop, 30), (Rumble.Click, 150), (Rumble.Stop, 0) })
        {
            var packet = Rumble.Packet(entry, Interlocked.Increment(ref _rumbleSequence));
            Rumble.Scale(packet, Settings.Current.RumbleStrength);
            SwitchController?[] live;
            lock (_gate) live = [_pro, _left, _right];
            foreach (var c in live.OfType<SwitchController>())
            {
                try { await c.WriteRumbleAsync(packet); }
                catch (Exception e) { Log.Warn($"Test rumble on {c.Name} failed: {e.Message}"); }
            }
            if (ms > 0) await Task.Delay(ms);
        }
    }


    public bool Owns(ulong address) => ProAddress == address || LeftAddress == address || RightAddress == address;

    /// <summary>True when a controller of this kind could join: the right type of pad,
    /// and that side not already live.</summary>
    public bool HasRoomFor(ControllerKind kind, ulong address)
    {
        lock (_gate)
        {
            return kind switch
            {
                ControllerKind.ProController => !IsJoyConPad && _pro == null && (ProAddress == null || ProAddress == address),
                ControllerKind.JoyConLeft => IsJoyConPad && _left == null && (LeftAddress == null || LeftAddress == address),
                _ => IsJoyConPad && _right == null && (RightAddress == null || RightAddress == address),
            };
        }
    }

    public void Attach(SwitchController controller)
    {
        lock (_gate)
        {
            switch (controller.Kind)
            {
                case ControllerKind.ProController: _pro = controller; ProAddress = controller.Address; break;
                case ControllerKind.JoyConLeft: _left = controller; LeftAddress = controller.Address; break;
                default: _right = controller; RightAddress = controller.Address; break;
            }
            _link[controller.Kind] = new LinkStats($"{controller.Name} (player {Number})");
            _lowSamples.Remove(controller.Kind);
            _lowWarned.Remove(controller.Kind);
            _lastBatterySample[controller.Kind] = Environment.TickCount64 - BatterySampleMs + 5000; // first sample after 5 s
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            _lastReport[controller.Kind] = Environment.TickCount64;
        }
        InputCapture.Note(Number, controller.Kind, "connected");
        controller.InputReport += report => OnReport(controller.Kind, report);
    }

    public void Detach(SwitchController controller)
    {
        InputCapture.Note(Number, controller.Kind, "disconnected");
        lock (_gate)
        {
            _batteryLogged.Remove(controller.Kind);
            _power.Remove(controller.Kind);
            _firmware.Remove(controller.Kind);
            _lastReport.Remove(controller.Kind);
            if (_link.Remove(controller.Kind, out var link)) Log.Info(link.Summary());
            if (_pro == controller) _pro = null;
            if (_left == controller) _left = null;
            if (_right == controller) _right = null;

            // Release whatever the lost controller was holding.
            var neutral = NeutralReport();
            switch (controller.Kind)
            {
                case ControllerKind.ProController: neutral.CopyTo(_merged, 0); break;
                case var _ when IsSideways: neutral.CopyTo(_merged, 0); break;
                case ControllerKind.JoyConLeft: Merge(_merged, controller.Kind, neutral, _right != null); break;
                default: Merge(_merged, controller.Kind, neutral, false); break;
            }
        }
        Submit();
    }

    void OnReport(ControllerKind kind, byte[] report)
    {
        if (report.Length < 60) return;
        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        InputCapture.Post(Number, kind, stamp, report);
        (ulong Address, int Mv, bool Charging)? sample = null;
        lock (_gate)
        {
            if (_link.TryGetValue(kind, out var link)) link.Report(stamp);
            _lastReport[kind] = Environment.TickCount64;
            _seen.TryGetValue(kind, out var seen);
            // 0x1F: battery voltage in mV; 0x21: charging state, non-zero on USB power
            // (switch2_controller_research, hid_reports.md, input report 0x05).
            _seen[kind] = (seen.Reports + 1, BitConverter.ToUInt16(report, BatteryAt), report[BatteryAt + 2]);
            if (_batteryLogged.Add(kind))
                Log.Info($"Player {Number} {kind}: battery {BitConverter.ToUInt16(report, BatteryAt)} mV, " +
                    $"charging state 0x{report[BatteryAt + 2]:X2}.");
            if (kind == ControllerKind.ProController) report.AsSpan(0, Math.Min(report.Length, ReportLength)).CopyTo(_merged);
            else if (IsSideways) SidewaysJoyCon.Apply(kind, report).CopyTo(_merged, 0);
            else
            {
                Merge(_merged, kind, report, _right != null);
                // One pad, one battery: report whichever Joy-Con is lower.
                var source = kind;
                var other = kind == ControllerKind.JoyConLeft ? ControllerKind.JoyConRight : ControllerKind.JoyConLeft;
                if ((other == ControllerKind.JoyConLeft ? _left : _right) != null
                    && _seen.TryGetValue(other, out var o) && o.BatteryMv > 0 && o.BatteryMv < _seen[kind].BatteryMv)
                    source = other;
                if (source == kind) report.AsSpan(BatteryAt, 4).CopyTo(_merged.AsSpan(BatteryAt));
            }

            NoteActivity();

            long now = Environment.TickCount64;
            ulong? address = kind switch
            {
                ControllerKind.ProController => ProAddress,
                ControllerKind.JoyConLeft => LeftAddress,
                _ => RightAddress,
            };
            if (address != null && (!_lastBatterySample.TryGetValue(kind, out var last) || now - last >= BatterySampleMs))
            {
                _lastBatterySample[kind] = now;
                sample = (address.Value, BitConverter.ToUInt16(report, BatteryAt), report[BatteryAt + 2] != 0);
            }
        }
        Submit();
        if (sample is { } s) _ = Task.Run(() => SampleBattery(kind, s.Address, s.Mv, s.Charging));
    }

    void SampleBattery(ControllerKind kind, ulong address, int millivolts, bool charging)
    {
        if (millivolts <= 0) return;
        BatteryLog.Record(address, millivolts, charging);
        string? warn = null;
        lock (_gate)
        {
            // The controller's own level when it gives one, otherwise voltage.
            var power = _power.GetValueOrDefault(kind);
            bool low = power != null
                ? power.Level <= LowLevel && !power.Charging && !power.ExternalPower
                : !charging && BatteryLog.IsLow(address, millivolts);
            // Two low samples in a row, so a dip under heavy rumble doesn't trigger it.
            int count = low ? _lowSamples.GetValueOrDefault(kind) + 1 : 0;
            _lowSamples[kind] = count;
            if (count >= 2 && _lowWarned.Add(kind))
            {
                var name = kind switch
                {
                    ControllerKind.JoyConLeft => "Left Joy-Con",
                    ControllerKind.JoyConRight => "Right Joy-Con",
                    _ => "Pro Controller 2",
                };
                var pct = BatteryLog.Percent(address, millivolts);
                var detail = power != null ? $" (level {power.Level} of {PowerInfo.MaxLevel})" : pct is int p ? $" (~{p}%)" : "";
                warn = $"{name} (player {Number}) battery is low{detail}. Charge it soon.";
            }
        }
        if (warn != null)
        {
            Log.Warn(warn);
            _notify(warn);
        }
    }

    /// <summary>Marks the pad active when a button changes or a stick moves noticeably.
    /// Call with _gate held, after _merged is updated.</summary>
    void NoteActivity()
    {
        uint buttons = BitConverter.ToUInt32(_merged, 4);
        int l = _merged[10] | _merged[11] << 8 | _merged[12] << 16;
        int r = _merged[13] | _merged[14] << 8 | _merged[15] << 16;
        static bool Moved(int a, int b) =>
            Math.Abs((a & 0xFFF) - (b & 0xFFF)) > StickActivity || Math.Abs((a >> 12) - (b >> 12)) > StickActivity;
        if (buttons != _activityButtons || Moved(l, _activityL) || Moved(r, _activityR))
        {
            _activityButtons = buttons;
            _activityL = l;
            _activityR = r;
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
        }
    }

    /// <summary>Folds one Joy-Con's report into the merged Pro-shaped report: its own
    /// buttons and stick, and motion from the right Joy-Con (the left one's when it is alone).
    /// In vertical grip both Joy-Cons use the Pro Controller's stick slots and IMU axes.</summary>
    internal static void Merge(byte[] merged, ControllerKind kind, ReadOnlySpan<byte> report, bool rightIsLive)
    {
        uint own = kind == ControllerKind.JoyConLeft ? LeftJoyConButtons : RightJoyConButtons;
        uint buttons = (BitConverter.ToUInt32(merged, 4) & ~own) | (BitConverter.ToUInt32(report[4..]) & own);
        BitConverter.TryWriteBytes(merged.AsSpan(4), buttons);
        if (kind == ControllerKind.JoyConLeft)
        {
            report.Slice(10, 3).CopyTo(merged.AsSpan(10));
            if (!rightIsLive) report.Slice(48, 12).CopyTo(merged.AsSpan(48));
        }
        else
        {
            report.Slice(13, 3).CopyTo(merged.AsSpan(13));
            report.Slice(48, 12).CopyTo(merged.AsSpan(48));
        }
    }

    internal static byte[] NeutralReport()
    {
        var report = new byte[ReportLength];
        // Both sticks centred (2048, 2048), packed 12 bits each.
        report[10] = 0x00; report[11] = 0x08; report[12] = 0x80;
        report[13] = 0x00; report[14] = 0x08; report[15] = 0x80;
        // Until a controller reports, the persona's own default charge.
        BitConverter.TryWriteBytes(report.AsSpan(BatteryAt), DefaultBatteryMv);
        report[BatteryAt + 2] = 0x20;
        return report;
    }

    int _isolationHolds;

    /// <summary>True while Steam and games must not see this pad's input: a Test inputs
    /// window is open, or it's being recorded.</summary>
    public bool IsIsolated => Volatile.Read(ref _isolationHolds) > 0 || InputCapture.RecordingPlayer == Number;

    /// <summary>Keeps input from Steam until the returned object is disposed.</summary>
    public IDisposable HoldIsolation()
    {
        Interlocked.Increment(ref _isolationHolds);
        return new IsolationHold(this);
    }

    sealed class IsolationHold(VirtualPad pad) : IDisposable
    {
        int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Interlocked.Decrement(ref pad._isolationHolds);
        }
    }

    /// <summary>What Steam sees during isolation: nothing pressed, sticks centred, no motion,
    /// and the real battery, so it doesn't look like the charge changed.</summary>
    internal static byte[] IsolatedReport(byte[] merged)
    {
        var report = NeutralReport();
        merged.AsSpan(BatteryAt, 4).CopyTo(report.AsSpan(BatteryAt));
        return report;
    }

    void Submit()
    {
        Span<byte> state = stackalloc byte[StateBlockSize];
        bool isolated = IsIsolated;
        lock (_gate) ToStateBlock(isolated ? IsolatedReport(_merged) : _merged, state);
        try { _pad.SubmitRawReport(state); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Converts a Bluetooth input report into the persona's 24-byte state block.</summary>
    internal static void ToStateBlock(ReadOnlySpan<byte> report, Span<byte> state)
    {
        state.Clear();
        uint buttons = BitConverter.ToUInt32(report[4..]);
        uint mapped = 0;
        foreach (var (ble, bit) in ButtonMap)
            if ((buttons & ble) != 0) mapped |= 1u << bit;
        state[0] = (byte)mapped;
        state[1] = (byte)(mapped >> 8);
        state[2] = (byte)(mapped >> 16);

        // Sticks: raw 12-bit values, repacked by the persona exactly as the pad packs them.
        WriteStick(state[4..], report.Slice(10, 3));
        WriteStick(state[8..], report.Slice(13, 3));

        // Accelerometer and gyro: the persona copies these 12 bytes verbatim into
        // report 0x05, where a wired pad carries the same raw sensor words.
        report.Slice(48, 12).CopyTo(state[12..]);

        // Battery voltage and current, copied to the same place in report 0x05.
        report.Slice(BatteryAt, 4).CopyTo(state[24..]);
    }

    static void WriteStick(Span<byte> dest, ReadOnlySpan<byte> packed)
    {
        int v = packed[0] | (packed[1] << 8) | (packed[2] << 16);
        BitConverter.TryWriteBytes(dest, (short)(v & 0xFFF));
        BitConverter.TryWriteBytes(dest[2..], (short)(v >> 12));
    }

    static JsonObject Template()
    {
        using var stream = typeof(VirtualPad).Assembly.GetManifestResourceStream("switch2-pro-composite.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    }

    /// <summary>The flash image the persona serves Steam. A Pro Controller's own flash
    /// as-is. For Joy-Cons: the left one's info and stick calibration in the first stick
    /// slot, the right one's in the second (0x40 bytes on, as on a Pro Controller), the
    /// right one's IMU calibration, and the Pro Controller 2's product ID.</summary>
    internal static (byte[] Factory, byte[] User) ComposeFlash(FlashDump? pro, FlashDump? left, FlashDump? right)
    {
        if (pro != null) return (pro.Factory, pro.User);

        var factory = new byte[FlashDump.FactoryLength];
        Array.Fill(factory, (byte)0xFF);
        foreach (var segment in Template()["protocol"]!["memory"]![0]!["segments"]!.AsArray())
        {
            if (segment!["hex"] is not JsonValue hex) continue;
            var at = Convert.ToUInt32((string)segment["at"]!, 16) - FlashDump.FactoryAddress;
            var bytes = Convert.FromHexString((string)hex!);
            if (at + bytes.Length <= factory.Length) bytes.CopyTo(factory, at);
        }
        var templateSlot1 = factory[0x80..0xC0];

        var user = new byte[FlashDump.UserLength];
        Array.Fill(user, (byte)0xFF);

        if (left != null)
        {
            left.Factory.CopyTo(factory, 0);
            left.User.AsSpan(0, 0x20).CopyTo(user);
        }
        if (right != null)
        {
            if (left == null)
            {
                right.Factory.CopyTo(factory, 0);
                templateSlot1.CopyTo(factory, 0x80);
            }
            right.Factory.AsSpan(0x80, 0x40).CopyTo(factory.AsSpan(0xC0));
            right.Factory.AsSpan(0x100, 0x40).CopyTo(factory.AsSpan(0x100));
            right.User.AsSpan(0, 0x20).CopyTo(user.AsSpan(0x20));
        }
        // 0x13012: vendor and product ID, little-endian.
        BitConverter.TryWriteBytes(factory.AsSpan(0x12), SwitchController.NintendoVid);
        BitConverter.TryWriteBytes(factory.AsSpan(0x14), SwitchController.ProController2Pid);
        return (factory, user);
    }

    /// <summary>The persona's profile with this pad's id and flash image, and a state block
    /// grown by 4 bytes that report 0x05 copies over its fixed battery bytes.</summary>
    internal static JsonObject ProfileJson(string id, byte[] factory, byte[] user)
    {
        var json = Template();
        json["id"] = id;
        json["name"] = "Nintendo Switch 2 Pro Controller (Bluetooth bridge)";
        json["stateBlock"]!["size"] = StateBlockSize;
        var common = json["inputReports"]!.AsArray().Single(r => (string?)r!["reportId"] == "0x05")!;
        // Report byte = Bluetooth byte + 1 (the report ID), so voltage and current land at 32-35.
        common["program"]!.AsArray().Add(new JsonObject { ["copy"] = "24-27", ["to"] = BatteryAt + 1 });
        var segments = json["protocol"]!["memory"]![0]!["segments"]!.AsArray();
        segments.Clear();
        segments.Add(new JsonObject { ["at"] = $"0x{FlashDump.FactoryAddress:X}", ["hex"] = Convert.ToHexString(factory) });
        segments.Add(new JsonObject { ["at"] = $"0x{FlashDump.UserAddress:X}", ["hex"] = Convert.ToHexString(user) });
        return json;
    }

    void OnOutput(HMController _, HMOutputPacket packet)
    {
        // Report 0x02 is rumble: 16-byte left block then 16-byte right block.
        if (packet.ReportId != 0x02 || packet.Data.Length < 32) return;
        var rumble = packet.Data[..32].ToArray();
        // Bytes after each block's header carry the motor amplitudes; all zero is "stop".
        if (rumble.AsSpan(1, 15).ContainsAnyExcept((byte)0) || rumble.AsSpan(17, 15).ContainsAnyExcept((byte)0))
            Interlocked.Exchange(ref _lastRumbleTicks, Environment.TickCount64);
        Rumble.Scale(rumble, Settings.Current.RumbleStrength);
        Volatile.Write(ref _pendingRumble, rumble);
        try { _rumbleSignal.Release(); } catch (SemaphoreFullException) { }
    }

    async Task RumbleLoop()
    {
        var ct = _rumbleCts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await _rumbleSignal.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            var packet = Interlocked.Exchange(ref _pendingRumble, null);
            if (packet == null) continue;
            SwitchController?[] targets;
            lock (_gate) targets = [_pro, _left, _right];
            foreach (var target in targets)
            {
                if (target == null) continue;
                try { await target.WriteRumbleAsync(packet); }
                catch (Exception e) { Log.Warn($"Rumble write to {target.Name} failed: {e.Message}"); }
            }
        }
    }

    public void Dispose()
    {
        _rumbleCts.Cancel();
        try { _rumbleLoop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _pad.Dispose();
    }
}
