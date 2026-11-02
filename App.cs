using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HIDMaestro;
using Windows.Devices.Bluetooth;
using Forms = System.Windows.Forms;

namespace Switch2Bridge;

/// <summary>
/// The WPF application: Windows 11 Fluent theme, a tray icon with a native menu, and the
/// status window. Closing the window keeps the bridge running in the tray.
/// </summary>
sealed class App : Application
{
    readonly CancellationTokenSource _cts = new();
    readonly bool _justUpdated;
    Forms.NotifyIcon? _tray;
    System.Drawing.Icon? _idleIcon, _liveIcon;
    StatusWindow? _window;
    HMContext? _ctx;
    Bridge? _bridge;
    bool _autostart;
    bool _quitting;
    DispatcherTimer? _updateTimer;

    public Bridge? Bridge => _bridge;
    public bool AutostartEnabled => _autostart;
    public string StartupState { get; private set; } = "Starting...";

    /// <summary>A newer release, once a check has found one.</summary>
    public UpdateInfo? AvailableUpdate { get; private set; }
    public event Action? UpdateChanged;

    /// <summary>The latest setup check results; replaced, never changed in place.</summary>
    public IReadOnlyList<string> SetupWarnings { get; private set; } = [];
    DispatcherTimer? _checkTimer;

    async Task RunSetupChecksAsync()
    {
        var facts = await SetupChecks.GatherAsync(_bridge?.AnyLive == true);
        var warnings = SetupChecks.Evaluate(facts);
        if (!warnings.SequenceEqual(SetupWarnings))
        {
            foreach (var warning in warnings.Except(SetupWarnings)) Log.Warn($"Setup check: {warning}");
            SetupWarnings = warnings;
        }
    }

    public App(bool justUpdated = false)
    {
        _justUpdated = justUpdated;
        ThemeMode = ThemeMode.System;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    bool _previewOnly;

    /// <summary>Runs the dispatcher with no tray, bridge or driver, for --render-preview.</summary>
    public int RunPreview()
    {
        _previewOnly = true;
        return Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (_previewOnly) return;
        InputCapture.Start();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error($"UI error: {args.Exception.Message}");
            args.Handled = true;
        };

        _idleIcon = TrayIcon.Make(live: false);
        _liveIcon = TrayIcon.Make(live: true);
        _tray = new Forms.NotifyIcon { Icon = _idleIcon, Text = "Switch 2 Native Bridge", Visible = true };
        _tray.MouseUp += (_, me) =>
        {
            if (me.Button == Forms.MouseButtons.Left) ShowStatus();
            else if (me.Button == Forms.MouseButtons.Right) ShowMenu();
        };

        _ = StartBridgeAsync();
    }

    async Task StartBridgeAsync()
    {
        try
        {
            Log.Info("Switch 2 Native Bridge starting.");
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null || !adapter.IsLowEnergySupported)
                throw new InvalidOperationException("No Bluetooth LE adapter found.");

            StartupState = "Installing the virtual controller driver...";
            await Task.Run(() =>
            {
                _ctx = new HMContext();
                _ctx.LoadDefaultProfiles();
                Log.Info("Checking the HIDMaestro driver (the first run installs it)...");
                _ctx.InstallDriver();
            });

            bool firstRun = !Settings.Current.ExistedOnLoad;
            await Task.Run(() =>
            {
                if (firstRun) FirstRunAutostart();
                try { Autostart.RepairIfMoved(); } catch (Exception ex) { Log.Warn(ex.Message); }
                _autostart = SafeIsAutostartEnabled();
            });

            _bridge = new Bridge(_ctx!, adapter.BluetoothAddress, _cts.Token);
            _bridge.Changed += message => Dispatcher.BeginInvoke(() =>
            {
                UpdateTrayIcon();
                _tray?.ShowBalloonTip(2000, "Switch 2 Native Bridge", message, Forms.ToolTipIcon.None);
            });
            _bridge.Start();
            StartupState = "Ready";
            UpdateTrayIcon();

            // Turn controllers off before the PC sleeps, so they don't drain overnight.
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, pe) =>
            {
                if (pe.Mode == Microsoft.Win32.PowerModes.Suspend && Settings.Current.OffOnSleep && _bridge != null)
                    _bridge.PowerOffAllAsync("PC going to sleep").Wait(TimeSpan.FromSeconds(4));
            };

            _checkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _checkTimer.Tick += async (_, _) => await RunSetupChecksAsync();
            _checkTimer.Start();
            await RunSetupChecksAsync();

            if (_justUpdated)
                _tray?.ShowBalloonTip(3000, "Switch 2 Native Bridge", $"Updated to v{Updater.Current.ToString(3)}.", Forms.ToolTipIcon.Info);
            if (firstRun) ShowStatus();

            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
            _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
            _updateTimer.Start();
            await CheckForUpdatesAsync(manual: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            MessageBox.Show($"Switch 2 Native Bridge could not start:\n\n{ex.Message}\n\nLog: {Log.FilePath}",
                "Switch 2 Native Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
            Quit();
        }
    }

    /// <summary>Turns start-with-Windows on the first time the bridge runs.</summary>
    void FirstRunAutostart()
    {
        try
        {
            Autostart.Enable();
            Settings.Current.AutostartConfigured = true;
            Settings.Save();
        }
        catch (Exception e)
        {
            Log.Warn($"Could not set up start with Windows: {e.Message}");
        }
    }

    static bool SafeIsAutostartEnabled()
    {
        try { return Autostart.IsEnabled; }
        catch (Exception e) { Log.Warn(e.Message); return false; }
    }

    public async Task SetAutostartAsync(bool enabled)
    {
        try
        {
            await Task.Run(() => { if (enabled) Autostart.Enable(); else Autostart.Disable(); });
        }
        catch (Exception e)
        {
            Log.Error(e.Message);
            MessageBox.Show(e.Message, "Switch 2 Native Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _autostart = await Task.Run(SafeIsAutostartEnabled);
    }

    /// <summary>Looks for a newer release. Automatic checks respect the setting and a skipped
    /// version and only notify; a manual check always runs and reports "up to date" too.</summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (!manual && !Settings.Current.CheckForUpdates) return;
        try
        {
            var update = await Updater.CheckAsync(_cts.Token);
            AvailableUpdate = update;
            UpdateChanged?.Invoke();
            if (update == null)
            {
                if (manual) _tray?.ShowBalloonTip(2000, "Switch 2 Native Bridge", $"You're on the latest version (v{Updater.Current.ToString(3)}).", Forms.ToolTipIcon.None);
                return;
            }
            Log.Info($"Update available: {update.Tag}.");
            if (manual || Settings.Current.SkippedVersion != update.Version.ToString(3))
                _tray?.ShowBalloonTip(4000, "Switch 2 Native Bridge",
                    $"Version {update.Version.ToString(3)} is available. Open the bridge to update.", Forms.ToolTipIcon.Info);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"Update check failed: {e.Message}");
            if (manual) MessageBox.Show($"Couldn't check for updates:\n\n{e.Message}", "Switch 2 Native Bridge",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void SkipUpdate()
    {
        if (AvailableUpdate == null) return;
        Settings.Current.SkippedVersion = AvailableUpdate.Version.ToString(3);
        Settings.Save();
        UpdateChanged?.Invoke();
    }

    /// <summary>Downloads, verifies and starts the update, then exits. Controllers aren't
    /// turned off, so the new version can reconnect them.</summary>
    public async Task InstallUpdateAsync(IProgress<string> progress)
    {
        if (AvailableUpdate is not { } update) return;
        try
        {
            await Updater.InstallAsync(update, progress, _cts.Token);
            await QuitAsync(powerOff: false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error($"Update failed: {e.Message}");
            progress.Report("");
            MessageBox.Show($"The update didn't install:\n\n{e.Message}", "Switch 2 Native Bridge",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ShowStatus()
    {
        if (_window == null || !_window.IsLoaded)
        {
            _window = new StatusWindow(this);
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public static void OpenLog()
    {
        if (File.Exists(Log.FilePath)) Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        else Process.Start(new ProcessStartInfo("explorer.exe", Log.Directory) { UseShellExecute = true });
    }

    public static void OpenSteam() =>
        Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });

    /// <summary>The tray's right-click menu, as a WPF menu so it takes the Fluent theme.
    /// A hidden window owns it, so it closes when the user clicks elsewhere.</summary>
    void ShowMenu()
    {
        var host = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = null,
            ShowInTaskbar = false, Width = 0, Height = 0, Topmost = true,
        };
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        foreach (var line in StatusLines())
            menu.Items.Add(new MenuItem { Header = line, IsEnabled = false });
        menu.Items.Add(new Separator());

        var open = new MenuItem { Header = "Open status" };
        open.Click += (_, _) => ShowStatus();
        var autostart = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = _autostart };
        autostart.Click += async (_, _) => await SetAutostartAsync(autostart.IsChecked);
        var log = new MenuItem { Header = "Open log" };
        log.Click += (_, _) => OpenLog();
        var update = AvailableUpdate is { } u
            ? new MenuItem { Header = $"Update to v{u.Version.ToString(3)}..." }
            : new MenuItem { Header = "Check for updates" };
        update.Click += async (_, _) =>
        {
            if (AvailableUpdate != null) ShowStatus();
            else await CheckForUpdatesAsync(manual: true);
        };
        var turnOff = new MenuItem { Header = "Turn off controllers", IsEnabled = _bridge?.AnyLive == true };
        turnOff.Click += async (_, _) => { if (_bridge != null) await _bridge.PowerOffAllAsync("turned off from the tray"); };
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += async (_, _) => await QuitAsync(powerOff: Settings.Current.OffOnQuit);
        menu.Items.Add(open);
        menu.Items.Add(turnOff);
        menu.Items.Add(autostart);
        menu.Items.Add(update);
        menu.Items.Add(log);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        menu.Closed += (_, _) => host.Close();
        host.Show();
        host.Activate();
        menu.PlacementTarget = host;
        menu.IsOpen = true;
    }

    IEnumerable<string> StatusLines()
    {
        if (_bridge == null) return [StartupState];
        var pads = _bridge.Snapshot();
        if (pads.Length == 0) return ["No controllers yet"];
        return pads.Select(p => $"Player {p.Number}: {p.DisplayName},{(p.Sides.Any(s => s.Live) ? "connected" : "waiting")}");
    }

    void UpdateTrayIcon()
    {
        if (_tray == null) return;
        bool live = _bridge?.AnyLive == true;
        _tray.Icon = live ? _liveIcon : _idleIcon;
        _tray.Text = live ? "Switch 2 Native Bridge: connected" : "Switch 2 Native Bridge: waiting for controller";
    }

    public void Quit() => _ = QuitAsync(powerOff: false);

    /// <summary>Exits, first turning controllers off when asked (the quit setting), so they
    /// don't stay awake looking for the PC.</summary>
    public async Task QuitAsync(bool powerOff)
    {
        if (_quitting) return;
        _quitting = true;
        Log.Info("Shutting down.");
        _updateTimer?.Stop();
        _checkTimer?.Stop();
        if (powerOff && _bridge != null) await _bridge.PowerOffAllAsync("bridge closing");
        _cts.Cancel();
        _window?.Close();
        if (_tray != null) _tray.Visible = false;
        _bridge?.Dispose();
        InputCapture.Shutdown();
        _ctx?.Dispose();
        _tray?.Dispose();
        Shutdown();
    }
}

/// <summary>The tray icon: the app icon (assets\app.ico) in colour when a controller is
/// live, greyed out while waiting.</summary>
static class TrayIcon
{
    public static System.Drawing.Icon Make(bool live)
    {
        var size = Forms.SystemInformation.SmallIconSize;
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("app.ico")!;
        using var icon = new System.Drawing.Icon(stream, size);
        if (live) return (System.Drawing.Icon)icon.Clone();

        using var bmp = icon.ToBitmap();
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                int l = (int)(c.R * 0.3 + c.G * 0.59 + c.B * 0.11);
                l = 70 + l * 60 / 255; // a mid grey tile that reads on light and dark taskbars
                if (c.R > 230 && c.G > 230 && c.B > 230) l = 255; // keep the letters white
                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(c.A, l, l, l));
            }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
