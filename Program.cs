using System.Diagnostics;
using System.Windows;

namespace Switch2Bridge;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run(args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault());
        if (args.Length == 2 && args[0] == "--render-preview") return Preview.Run(args[1]);

        using var instance = new Mutex(true, @"Global\Switch2NativeBridge", out bool first);
        if (!first)
        {
            MessageBox.Show("Switch 2 Native Bridge is already running. Look for it in the system tray.",
                "Switch 2 Native Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        if (Process.GetProcessesByName("switch2pc").Length > 0)
        {
            MessageBox.Show("switch2pc.exe is running and would grab the controllers too. Close it, then start the bridge again.",
                "Switch 2 Native Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }

        return new App().Run();
    }
}
