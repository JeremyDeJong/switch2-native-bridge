using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Switch2Bridge;

/// <summary>Shows what the bridge receives from one player, live, with a tick on each button
/// that has been pressed since the window opened.</summary>
public partial class InputTestWindow : Window
{
    const double StickArea = 64, DotSize = 12, BarMax = 64;

    readonly Func<PadStatus?> _source;
    readonly List<Chip> _chips = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    sealed record Chip(uint Mask, string Name, Border Box, TextBlock Text)
    {
        public bool Tested { get; set; }
        public bool? ShownDown { get; set; }
    }

    // What a sideways Joy-Con can send, after its buttons are moved to their new places.
    static readonly string[] SidewaysButtons = ["A", "B", "X", "Y", "L", "R", "ZL", "ZR", "Minus", "Plus", "L stick click"];

    internal InputTestWindow(Func<PadStatus?> source, int player, bool joyCons, bool sideways = false, bool rightJoyCon = false)
    {
        _source = source;
        InitializeComponent();
        Heading.Text = $"Test inputs: player {player}";

        // SL/SR aren't passed to Steam, and a Joy-Con pair has no GL or GR.
        foreach (var (mask, name) in InputDecoder.Buttons)
        {
            if (name.StartsWith("S") && name.Contains('(') || name == "Headset") continue;
            if (joyCons && name is "GL" or "GR") continue;
            if (sideways && !SidewaysButtons.Contains(name) && !(rightJoyCon && name == "Home")) continue;
            var text = new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center };
            var box = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 6, 6), MinWidth = 52, Child = text };
            box.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
            Chips.Children.Add(box);
            _chips.Add(new Chip(mask, name, box, text));
        }

        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    void Refresh()
    {
        var status = _source();
        bool live = status?.Sides.Any(s => s.Live) == true;
        foreach (var chip in _chips)
        {
            bool down = live && (status!.Buttons & chip.Mask) != 0;
            bool newlyTested = down && !chip.Tested;
            if (newlyTested) chip.Tested = true;
            if (chip.ShownDown == down && !newlyTested) continue;
            chip.ShownDown = down;
            chip.Text.Text = chip.Tested ? "✓ " + chip.Name : chip.Name;
            chip.Box.SetResourceReference(BackgroundProperty, down ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush");
            chip.Text.SetResourceReference(TextBlock.ForegroundProperty, down ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        }

        int tested = _chips.Count(c => c.Tested);
        Progress.Text = live ? $"Press each button. {tested} of {_chips.Count} seen." : "This controller isn't connected.";
        if (status == null) return;
        Move(LeftDot, status.LX, status.LY);
        Move(RightDot, status.RX, status.RY);
        GyroX.Height = 3 + status.Gyro[0] * (BarMax - 3);
        GyroY.Height = 3 + status.Gyro[1] * (BarMax - 3);
        GyroZ.Height = 3 + status.Gyro[2] * (BarMax - 3);
    }

    static void Move(FrameworkElement dot, double x, double y)
    {
        Canvas.SetLeft(dot, x * (StickArea - DotSize));
        Canvas.SetTop(dot, y * (StickArea - DotSize));
    }

    void OnReset(object sender, RoutedEventArgs e)
    {
        foreach (var chip in _chips)
        {
            chip.Tested = false;
            chip.ShownDown = null;
        }
        Refresh();
    }
}
