using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace Switch2Bridge;

sealed record SideStatus(string Name, bool Live, int? BatteryMv, bool Charging, int ReportsPerSecond)
{
    /// <summary>Nintendo's own voltage-to-level curve isn't public; these are the thresholds
    /// switch2controllerpc's authors settled on from real controllers.</summary>
    public string? BatteryLevel => BatteryMv switch
    {
        null => null,
        > 3250 => "high",
        > 3125 => "medium",
        _ => "low",
    };
}

sealed record PadStatus(int Number, string Name, IReadOnlyList<SideStatus> Sides,
    double LX, double LY, double RX, double RY, double[] Gyro, bool Rumbling);

/// <summary>The status window: one card per player, refreshed while it is open.</summary>
public partial class StatusWindow : Window
{
    readonly App _app;
    readonly Func<PadStatus[]>? _preview;
    readonly ObservableCollection<PadView> _pads = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(66) };

    /// <param name="preview">Sample players to show instead of the bridge's (for --render-preview).</param>
    internal StatusWindow(App app, Func<PadStatus[]>? preview = null)
    {
        _app = app;
        _preview = preview;
        InitializeComponent();
        Pads.ItemsSource = _pads;
        if (TryFindResource("AccentButtonStyle") is Style accent) SteamButton.Style = accent;
        AutostartBox.IsChecked = _app.AutostartEnabled;
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
        Closed += (_, _) => _timer.Stop();
        Refresh();
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
    string _title = "", _detail = "";
    double _cardOpacity = 1, _lx, _ly, _rx, _ry, _gx, _gy, _gz;
    Visibility _liveVisibility;

    public int Number => _number;
    public string Title => _title;
    public string Detail => _detail;
    public double CardOpacity => _cardOpacity;
    public Visibility LiveVisibility => _liveVisibility;
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
        Set(ref _title, $"Player {s.Number} · {s.Name}", nameof(Title));
        Set(ref _detail, Describe(s, live), nameof(Detail));
        Set(ref _cardOpacity, live ? 1.0 : 0.6, nameof(CardOpacity));
        Set(ref _liveVisibility, live ? Visibility.Visible : Visibility.Hidden, nameof(LiveVisibility));
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
        if (!live) return "Waiting. Press a button on it to reconnect.";
        var parts = new List<string>();
        foreach (var side in s.Sides)
        {
            var prefix = s.Sides.Count > 1 ? side.Name + " " : "Battery ";
            if (!side.Live) parts.Add($"{side.Name} not connected");
            else if (side.BatteryMv is int mv)
                parts.Add($"{prefix}{side.BatteryLevel} ({mv / 1000.0:0.00} V{(side.Charging ? ", charging" : "")})");
        }
        int rate = s.Sides.Where(x => x.Live).Select(x => x.ReportsPerSecond).DefaultIfEmpty(0).Max();
        if (rate > 0) parts.Add($"{rate} reports/s");
        if (s.Rumbling) parts.Add("Rumble");
        return string.Join(" · ", parts);
    }

    void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
