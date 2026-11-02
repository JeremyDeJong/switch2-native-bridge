using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Switch2Bridge;

sealed record SideStatus(string Name, bool Live, int? BatteryMv, bool Charging, int ReportsPerSecond,
    int? BatteryPercent = null, double AvgGapMs = 0, double WorstGapMs = 0, int StallsPerMinute = 0, int? PowerLevel = null,
    FirmwareInfo? Firmware = null, bool HeadsetChannel = false, bool FirmwareTested = false)
{
    /// <summary>Nintendo's own voltage-to-level curve isn't public; these are the thresholds
    /// switch2controllerpc's authors settled on from real controllers.</summary>
    public string? BatteryLevel => BatteryMv switch
    {
        null => null,
        > 3250 => "high",
        > BatteryLog.LowMv => "medium",
        _ => "low",
    };
}

sealed record PadStatus(int Number, string Name, IReadOnlyList<SideStatus> Sides,
    double LX, double LY, double RX, double RY, double[] Gyro, bool Rumbling, uint Buttons = 0, bool Isolated = false, string? CustomName = null,
    bool CanHoldSideways = false, bool Sideways = false)
{
    public string DisplayName => CustomName ?? Name;
}

/// <summary>The status window: one card per player, an update banner, and settings.</summary>
public partial class StatusWindow : Window
{
    static readonly (string Label, int Minutes)[] IdleChoices =
        [("Never", 0), ("5 minutes", 5), ("10 minutes", 10), ("15 minutes", 15), ("30 minutes", 30), ("1 hour", 60)];

    readonly App _app;
    readonly Func<PadStatus[]>? _preview;
    readonly ObservableCollection<PadView> _pads = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    bool _loadingSettings;
    IReadOnlyList<string>? _shownWarnings;

    /// <param name="preview">Sample players to show instead of the bridge's (for --render-preview).</param>
    internal StatusWindow(App app, Func<PadStatus[]>? preview = null)
    {
        _app = app;
        _preview = preview;
        InitializeComponent();
        Pads.ItemsSource = _pads;
        if (TryFindResource("AccentButtonStyle") is Style accent)
        {
            SteamButton.Style = accent;
            UpdateButton.Style = accent;
        }
        AutostartBox.IsChecked = _app.AutostartEnabled;
        LoadSettings();
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
        _app.UpdateChanged += OnUpdateChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _app.UpdateChanged -= OnUpdateChanged;
        };
        OnUpdateChanged();
        Refresh();
    }

    void LoadSettings()
    {
        _loadingSettings = true;
        var s = Settings.Current;
        RumbleSlider.Value = Math.Round(s.RumbleStrength * 100);
        RumbleValue.Text = $"{RumbleSlider.Value:0}%";
        foreach (var (label, _) in IdleChoices) IdleBox.Items.Add(label);
        int idle = Array.FindIndex(IdleChoices, c => c.Minutes == s.IdleOffMinutes);
        IdleBox.SelectedIndex = idle >= 0 ? idle : Array.FindIndex(IdleChoices, c => c.Minutes == 15);
        SleepBox.IsChecked = s.OffOnSleep;
        QuitBox.IsChecked = s.OffOnQuit;
        UpdatesBox.IsChecked = s.CheckForUpdates;
        LogInputsBox.IsChecked = s.LogInputs;
        VersionText.Text = $"Version {Updater.Current.ToString(3)}";
        _loadingSettings = false;
    }

    void Refresh()
    {
        var bridge = _app.Bridge;
        var snapshot = _preview?.Invoke() ?? bridge?.Snapshot() ?? [];
        while (_pads.Count > snapshot.Length) _pads.RemoveAt(_pads.Count - 1);
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (i == _pads.Count) _pads.Add(new PadView());
            _pads[i].Update(snapshot[i]);
        }

        int live = snapshot.Count(p => p.Sides.Any(s => s.Live));
        Summary.Text = bridge == null && _preview == null ? _app.StartupState
            : live == 0 ? "Listening for controllers."
            : $"{live} connected. Steam sees each one as a wired Switch 2 Pro Controller.";
        EmptyHint.Visibility = snapshot.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_preview == null && !ReferenceEquals(_shownWarnings, _app.SetupWarnings))
        {
            _shownWarnings = _app.SetupWarnings;
            Checks.ItemsSource = _shownWarnings;
            Checks.Visibility = _shownWarnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void OnUpdateChanged()
    {
        var update = _app.AvailableUpdate;
        bool show = update != null && Settings.Current.SkippedVersion != update.Version.ToString(3);
        UpdateBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (update != null) UpdateTitle.Text = $"Version {update.Version.ToString(3)} is available";
    }

    internal void ShowUpdateBannerForPreview(string version)
    {
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateTitle.Text = $"Version {version} is available";
    }

    async void OnUpdate(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = SkipButton.IsEnabled = false;
        var progress = new Progress<string>(text =>
        {
            UpdateStatus.Text = text.Length > 0 ? text : "Your controllers reconnect after it restarts.";
            if (text.Length == 0) UpdateButton.IsEnabled = SkipButton.IsEnabled = true;
        });
        await _app.InstallUpdateAsync(progress);
    }

    void OnSkipUpdate(object sender, RoutedEventArgs e) => _app.SkipUpdate();

    void OnReleaseNotes(object sender, RoutedEventArgs e)
    {
        if (_app.AvailableUpdate is { PageUrl.Length: > 0 } u)
            Process.Start(new ProcessStartInfo(u.PageUrl) { UseShellExecute = true });
    }

    async void OnTestRumble(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int player } button || _app.Bridge == null) return;
        button.IsEnabled = false;
        try { await _app.Bridge.TestRumbleAsync(player); }
        finally { button.IsEnabled = true; }
    }

    void OnSidewaysClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: int player } box) _app.Bridge?.SetSideways(player, box.IsChecked == true);
    }

    void OnRename(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int player } || _app.Bridge is not { } bridge) return;
        var current = bridge.Snapshot().FirstOrDefault(p => p.Number == player);
        var dialog = new RenameWindow(current?.CustomName ?? "") { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null) bridge.RenamePlayer(player, dialog.Result);
    }

    async void OnCreateDiagnostics(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var adapter = await SetupChecks.DescribeAdapterAsync();
            var pads = _app.Bridge?.Snapshot() ?? [];
            var warnings = _app.SetupWarnings;
            bool autostart = _app.AutostartEnabled;
            var path = await Task.Run(() => Diagnostics.Create(pads, warnings, adapter, autostart));
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not create the diagnostics report: {ex.Message}");
            MessageBox.Show($"Couldn't create the report:\n\n{ex.Message}", "Switch 2 Native Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = true; }
    }

    internal void ShowWarningsForPreview(IReadOnlyList<string> warnings)
    {
        _shownWarnings = warnings;
        Checks.ItemsSource = warnings;
        Checks.Visibility = Visibility.Visible;
    }

    void OnTestInputs(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int player } || _app.Bridge is not { } bridge) return;
        var status = bridge.Snapshot().FirstOrDefault(p => p.Number == player);
        bool joyCons = status?.Name.StartsWith("Joy-Con") == true;
        var hold = bridge.HoldIsolation(player);
        var window = new InputTestWindow(() => bridge.Snapshot().FirstOrDefault(p => p.Number == player), player, joyCons,
            status?.Sideways == true, status?.Name == "Joy-Con (R)") { Owner = this };
        window.Closed += (_, _) => hold?.Dispose();
        window.Show();
    }

    void OnRecord(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int player }) return;
        try
        {
            if (InputCapture.IsRecording(player)) InputCapture.StopRecording();
            else InputCapture.StartRecording(player);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not start recording: {ex.Message}");
            MessageBox.Show($"Couldn't start recording:\n\n{ex.Message}", "Switch 2 Native Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnLogInputsClick(object sender, RoutedEventArgs e)
    {
        Settings.Current.LogInputs = LogInputsBox.IsChecked == true;
        Settings.Save();
    }

    void OnOpenInputLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(InputCapture.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", InputCapture.LogDirectory) { UseShellExecute = true });
    }

    void OnOpenRecordings(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(InputCapture.RecordingDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", InputCapture.RecordingDirectory) { UseShellExecute = true });
    }

    void OnRumbleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RumbleValue == null) return;
        RumbleValue.Text = $"{e.NewValue:0}%";
        if (_loadingSettings) return;
        Settings.Current.RumbleStrength = e.NewValue / 100.0;
        Settings.Save();
    }

    void OnIdleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || IdleBox.SelectedIndex < 0) return;
        Settings.Current.IdleOffMinutes = IdleChoices[IdleBox.SelectedIndex].Minutes;
        Settings.Save();
    }

    void OnPowerOptionClick(object sender, RoutedEventArgs e)
    {
        Settings.Current.OffOnSleep = SleepBox.IsChecked == true;
        Settings.Current.OffOnQuit = QuitBox.IsChecked == true;
        Settings.Save();
    }

    void OnUpdatesOptionClick(object sender, RoutedEventArgs e)
    {
        Settings.Current.CheckForUpdates = UpdatesBox.IsChecked == true;
        Settings.Save();
    }

    async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) b.IsEnabled = false;
        await _app.CheckForUpdatesAsync(manual: true);
        if (sender is Button b2) b2.IsEnabled = true;
        if (_app.AvailableUpdate == null) VersionText.Text = $"Version {Updater.Current.ToString(3)}, up to date";
    }

    async void OnRunProbe(object sender, RoutedEventArgs e)
    {
        if (_app.Bridge is not { } bridge)
        {
            ProbeStatus.Text = "The bridge is still starting.";
            return;
        }
        ProbeButton.IsEnabled = SoundsButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(text => ProbeStatus.Text = text);
            var verdict = await bridge.RunAudioProbeAsync(progress);
            ProbeStatus.Text = verdict ?? "Connect a Pro Controller first. Joy-Cons don't have a headphone jack.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Audio probe failed: {ex.Message}");
            ProbeStatus.Text = $"The test didn't finish: {ex.Message}";
        }
        finally
        {
            ProbeButton.IsEnabled = SoundsButton.IsEnabled = true;
        }
    }

    AudioLab? _lab;
    CancellationTokenSource? _soundsStop;

    async void OnPlaySounds(object sender, RoutedEventArgs e)
    {
        if (_app.Bridge is not { } bridge)
        {
            SoundsStatus.Text = "The bridge is still starting.";
            return;
        }
        var answer = MessageBox.Show(
            "This sends sound to the controller's undocumented audio channel: a short melody, a tone at rising levels, then each setting that might route it to the headphones.\n\n" +
            "Don't wear your headphones. An untested setting could make a loud noise. Plug them into the controller and hold them near your ear, not on it, with the controller's volume low.\n\n" +
            "Listen closely to both, and click where you hear each sound straight away. Steam gets no input from this controller for under 2 minutes.\n\n" +
            "No one has documented this channel, so there's a small unknown risk to the controller. Carry on?",
            "Play test sounds", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        SoundsButton.IsEnabled = ProbeButton.IsEnabled = false;
        StopSoundsButton.IsEnabled = true;
        _soundsStop = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(text => SoundsStatus.Text = text);
            var verdict = await bridge.RunAudioLabAsync(lab =>
            {
                _lab = lab;
                Dispatcher.BeginInvoke(() => HeardControllerButton.IsEnabled = HeardHeadphonesButton.IsEnabled = true);
            }, progress, _soundsStop.Token);
            SoundsStatus.Text = verdict ?? "Connect a Pro Controller first. Joy-Cons don't have a headphone jack.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Audio test sounds failed: {ex.Message}");
            SoundsStatus.Text = $"The test didn't finish: {ex.Message}";
        }
        finally
        {
            _lab = null;
            _soundsStop.Dispose();
            _soundsStop = null;
            SoundsButton.IsEnabled = ProbeButton.IsEnabled = true;
            HeardControllerButton.IsEnabled = HeardHeadphonesButton.IsEnabled = false;
            StopSoundsButton.IsEnabled = false;
        }
    }

    void OnHeardController(object sender, RoutedEventArgs e) => _lab?.MarkHeard(AudioLab.Heard.Controller);

    void OnHeardHeadphones(object sender, RoutedEventArgs e) => _lab?.MarkHeard(AudioLab.Heard.Headphones);

    void OnStopSounds(object sender, RoutedEventArgs e)
    {
        StopSoundsButton.IsEnabled = false;
        _soundsStop?.Cancel();
    }

    void OnOpenProbeResults(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AudioProbe.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", AudioProbe.Directory) { UseShellExecute = true });
    }

    void OnOpenFirmwareRecord(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", Log.Directory) { UseShellExecute = true });
    }

    void OnOpenBatteryLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(BatteryLog.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", BatteryLog.Directory) { UseShellExecute = true });
    }

    async void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        AutostartBox.IsEnabled = false;
        await _app.SetAutostartAsync(AutostartBox.IsChecked == true);
        AutostartBox.IsChecked = _app.AutostartEnabled;
        AutostartBox.IsEnabled = true;
    }

    void OnOpenLog(object sender, RoutedEventArgs e) => App.OpenLog();

    void OnOpenSteam(object sender, RoutedEventArgs e)
    {
        try { App.OpenSteam(); }
        catch (Exception ex) { Log.Warn($"Could not open Steam: {ex.Message}"); }
    }
}

/// <summary>One player card's bindable values.</summary>
sealed class PadView : INotifyPropertyChanged
{
    const double Pad = 34, Dot = 8, BarMax = 34;

    public event PropertyChangedEventHandler? PropertyChanged;

    int _number;
    string _title = "", _detail = "", _linkDetail = "", _firmwareDetail = "", _recordLabel = "Record";
    double _cardOpacity = 1, _lx, _ly, _rx, _ry, _gx, _gy, _gz;
    Visibility _liveVisibility, _actionsVisibility, _sidewaysVisibility = Visibility.Collapsed;
    bool _sideways;

    public int Number => _number;
    public string Title => _title;
    public string Detail => _detail;
    public string LinkDetail => _linkDetail;
    public string FirmwareDetail => _firmwareDetail;
    public string RecordLabel => _recordLabel;
    public double CardOpacity => _cardOpacity;
    public Visibility LiveVisibility => _liveVisibility;
    public Visibility ActionsVisibility => _actionsVisibility;
    public Visibility SidewaysVisibility => _sidewaysVisibility;
    public bool IsSideways => _sideways;
    public double LX => _lx;
    public double LY => _ly;
    public double RX => _rx;
    public double RY => _ry;
    public double GX => _gx;
    public double GY => _gy;
    public double GZ => _gz;

    public void Update(PadStatus s)
    {
        bool live = s.Sides.Any(x => x.Live);
        Set(ref _number, s.Number, nameof(Number));
        Set(ref _title, $"Player {s.Number} · {s.DisplayName}", nameof(Title));
        Set(ref _detail, Describe(s, live), nameof(Detail));
        Set(ref _linkDetail, DescribeLink(s), nameof(LinkDetail));
        Set(ref _firmwareDetail, DescribeFirmware(s), nameof(FirmwareDetail));
        Set(ref _recordLabel, InputCapture.IsRecording(s.Number) ? "Stop recording" : "Record", nameof(RecordLabel));
        Set(ref _sidewaysVisibility, s.CanHoldSideways && live ? Visibility.Visible : Visibility.Collapsed, nameof(SidewaysVisibility));
        Set(ref _sideways, s.Sideways, nameof(IsSideways));
        Set(ref _cardOpacity, live ? 1.0 : 0.6, nameof(CardOpacity));
        Set(ref _liveVisibility, live ? Visibility.Visible : Visibility.Hidden, nameof(LiveVisibility));
        Set(ref _actionsVisibility, live ? Visibility.Visible : Visibility.Collapsed, nameof(ActionsVisibility));
        Set(ref _lx, s.LX * (Pad - Dot), nameof(LX));
        Set(ref _ly, s.LY * (Pad - Dot), nameof(LY));
        Set(ref _rx, s.RX * (Pad - Dot), nameof(RX));
        Set(ref _ry, s.RY * (Pad - Dot), nameof(RY));
        Set(ref _gx, 2 + s.Gyro[0] * (BarMax - 2), nameof(GX));
        Set(ref _gy, 2 + s.Gyro[1] * (BarMax - 2), nameof(GY));
        Set(ref _gz, 2 + s.Gyro[2] * (BarMax - 2), nameof(GZ));
    }

    static string Describe(PadStatus s, bool live)
    {
        if (!live) return "Off or out of range. Press a button on it to reconnect.";
        var parts = new List<string>();
        if (s.Isolated) parts.Add("Steam input paused");
        foreach (var side in s.Sides)
        {
            var prefix = s.Sides.Count > 1 ? side.Name + " " : "Battery ";
            if (!side.Live) parts.Add($"{side.Name} not connected");
            else if (side.BatteryMv is int mv)
            {
                var level = side.PowerLevel is int bars ? $"level {bars} of {PowerInfo.MaxLevel}"
                    : side.BatteryPercent is int pct ? $"~{pct}%" : side.BatteryLevel;
                parts.Add($"{prefix}{level} ({mv / 1000.0:0.00} V{(side.Charging ? ", charging" : "")})");
            }
        }
        if (s.Rumbling) parts.Add("Rumble");
        return string.Join(" · ", parts);
    }

    /// <summary>The firmware line: version and whether it's on the tested list, plus for a
    /// Pro Controller whether it exposes the headset audio channel.</summary>
    internal static string DescribeFirmware(PadStatus s)
    {
        var sides = s.Sides.Where(x => x.Live).ToList();
        if (sides.Count == 0) return "";
        var parts = sides.Select(side =>
        {
            var prefix = s.Sides.Count > 1 ? $"{side.Name} firmware" : "Firmware";
            return side.Firmware is { } f
                ? $"{prefix} {f.Firmware} ({(side.FirmwareTested ? "tested" : "not yet tested")})"
                : $"{prefix} unknown";
        }).ToList();
        if (s.Sides.Count == 1 && sides[0].Firmware != null && s.Name == "Pro Controller 2")
            parts.Add($"Headset audio channel: {(sides[0].HeadsetChannel ? "yes" : "no")}");
        return string.Join(" · ", parts);
    }

    static string DescribeLink(PadStatus s)
    {
        var liveSides = s.Sides.Where(x => x.Live).ToList();
        if (liveSides.Count == 0) return "";
        int rate = liveSides.Max(x => x.ReportsPerSecond);
        double avg = liveSides.Max(x => x.AvgGapMs), worst = liveSides.Max(x => x.WorstGapMs);
        int stalls = liveSides.Sum(x => x.StallsPerMinute);
        if (rate == 0) return "Bluetooth: measuring...";
        return $"Bluetooth: {rate} reports/s · avg gap {avg:0.0} ms · worst {worst:0} ms · {stalls} stall{(stalls == 1 ? "" : "s")}/min";
    }

    void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
