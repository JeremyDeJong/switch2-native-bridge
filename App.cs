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
    static readonly string SettingsPath = Path.Combine(Log.Directory, "settings.json");

    readonly CancellationTokenSource _cts = new();
    Forms.NotifyIcon? _tray;
    System.Drawing.Icon? _idleIcon, _liveIcon;
    StatusWindow? _window;
    HMContext? _ctx;
    Bridge? _bridge;
    bool _autostart;

    public Bridge? Bridge => _bridge;
    public bool AutostartEnabled => _autostart;
    public string StartupState { get; private set; } = "Starting...";

    public App()
    {
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

            bool firstRun = !File.Exists(SettingsPath);
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

            if (firstRun) ShowStatus();
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
            Directory.CreateDirectory(Log.Directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { autostartConfigured = true }));
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
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => Quit();
        menu.Items.Add(open);
        menu.Items.Add(autostart);
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
        return pads.Select(p => $"Player {p.Number}: {p.Name}, {(p.Sides.Any(s => s.Live) ? "connected" : "waiting")}");
    }

    void UpdateTrayIcon()
    {
        if (_tray == null) return;
        bool live = _bridge?.AnyLive == true;
        _tray.Icon = live ? _liveIcon : _idleIcon;
        _tray.Text = live ? "Switch 2 Native Bridge: connected" : "Switch 2 Native Bridge: waiting for controller";
    }

    public void Quit()
    {
        Log.Info("Shutting down.");
        _cts.Cancel();
        _window?.Close();
        if (_tray != null) _tray.Visible = false;
        _bridge?.Dispose();
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
