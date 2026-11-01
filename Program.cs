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

        // Started by the previous version's updater: let it exit and remove its exe first.
        int updatedFrom = 0;
        if (args.Length == 3 && args[0] == "--after-update" && int.TryParse(args[1], out updatedFrom))
            Updater.FinishUpdate(updatedFrom, args[2]);

        using var instance = new Mutex(true, @"Global\Switch2NativeBridge", out bool first);
        if (!first)
        {
            MessageBox.Show("Switch 2 Native Bridge is already running. Look for it in the system tray.",
                "Switch 2 Native Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        // Versions before 0.2 took no mutex, and released exes carry their version in the
        // name (Switch2Bridge-v0.6.0.exe), so look for any process named Switch2Bridge*.
        int self = Environment.ProcessId;
        if (Process.GetProcesses().Any(p => p.Id != self && p.Id != updatedFrom
                && p.ProcessName.StartsWith("Switch2Bridge", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Another copy of Switch 2 Native Bridge is running (an older version may still be open in a console window). Close it, then start the bridge again.",
                "Switch 2 Native Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }

        if (Process.GetProcessesByName("switch2pc").Length > 0)
        {
            MessageBox.Show("switch2pc.exe is running and would grab the controllers too. Close it, then start the bridge again.",
                "Switch 2 Native Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }

        Settings.Load();
        return new App(justUpdated: updatedFrom != 0).Run();
    }
}
