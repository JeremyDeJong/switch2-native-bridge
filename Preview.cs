using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Switch2Bridge;

/// <summary>
/// --render-preview &lt;folder&gt;: draws the status window with sample players to PNGs
/// in light and dark theme, for checking the look without a controller or the driver.
/// </summary>
static class Preview
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var app = new App();
        int result = 0;
        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                foreach (var (mode, name) in new[] { (ThemeMode.Light, "light"), (ThemeMode.Dark, "dark") })
                {
                    app.ThemeMode = mode;
                    Render(app, Samples, Path.Combine(folder, $"status-{name}.png"));
                    Render(app, () => [], Path.Combine(folder, $"status-empty-{name}.png"));
                    Render(app, () => [Samples()[0]], Path.Combine(folder, $"status-settings-{name}.png"), extras: true);
                    var pressed = Samples()[0] with { Buttons = 0x08 | 0x02000000 };
                    Capture(app, new InputTestWindow(() => pressed, 1, joyCons: false), Path.Combine(folder, $"test-inputs-{name}.png"));
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                result = 1;
            }
            app.Shutdown();
        }, DispatcherPriority.ApplicationIdle);
        app.RunPreview();
        return result;
    }

    static PadStatus[] Samples() =>
    [
        new(1, "Pro Controller 2", [new SideStatus("Pro", true, 3310, false, 68, 72, 14.7, 31, 0, 6, new FirmwareInfo(new Version(2, 0, 0), 2, new Version(12, 0, 0), new Version(1, 0, 0)), true)], 0.5, 0.5, 0.7, 0.35, [0.3, 0.6, 0.15], true, CustomName: "Couch controller"),
        new(2, "Joy-Con pair", [new SideStatus("L", true, 3180, false, 67, null, 14.9, 46, 1), new SideStatus("R", true, 3102, true, 66, null, 15.1, 62, 2)],
            0.4, 0.6, 0.5, 0.5, [0.45, 0.2, 0.5], false),
        new(3, "Joy-Con (R)", [new SideStatus("R", true, 3420, false, 66, null, 15.0, 28, 0, 8, new FirmwareInfo(new Version(1, 2, 0), 1, new Version(12, 0, 0), null))],
            0.6, 0.5, 0.5, 0.5, [0.1, 0.2, 0.1], false, CanHoldSideways: true, Sideways: true),
        new(4, "Pro Controller 2", [new SideStatus("Pro", false, null, false, 0)], 0.5, 0.5, 0.5, 0.5, [0, 0, 0], false),
    ];

    static void Capture(App app, Window window, string file)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000;
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Save(app, window, file);
    }

    static void Render(App app, Func<PadStatus[]> players, string file, bool extras = false)
    {
        var window = new StatusWindow(app, players)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        if (extras)
        {
            // The update banner and the open Settings section, at a height that fits them.
            window.ShowUpdateBannerForPreview("0.7.0");
            window.SettingsExpander.IsExpanded = true;
            window.ShowWarningsForPreview(["Steam isn't running. Open it to use your controllers in games."]);
            window.Height = 1000;
        }
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (extras)
        {
            // Let the Expander's open animation finish before capturing.
            var frame = new DispatcherFrame();
            var wait = new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Normal,
                (s, _) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; }, window.Dispatcher);
            wait.Start();
            Dispatcher.PushFrame(frame);
        }
        Save(app, window, file);
    }

    static void Save(App app, Window window, string file)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        double scale = 1.5;
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // The Mica backdrop is drawn by Windows, not WPF; stand in with the theme's window colour.
            var background = window.TryFindResource("ApplicationBackgroundBrush") as Brush
                ?? (app.ThemeMode == ThemeMode.Dark ? new SolidColorBrush(Color.FromRgb(32, 32, 32)) : Brushes.WhiteSmoke);
            dc.DrawRectangle(background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var stream = File.Create(file)) encoder.Save(stream);
        window.Close();
        Console.WriteLine($"Wrote {file}");
    }
}
