using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Switch2Bridge;

/// <summary>
/// Keeps the running copy in %LOCALAPPDATA%\Programs\Switch2Bridge, so Start with Windows
/// and updates don't depend on where the download was saved. The downloaded exe is copied
/// there, the copy is started, and the download is left alone for the user to delete.
/// </summary>
static class Installer
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Switch2Bridge");
    static readonly Regex VersionedName = new(@"^Switch2Bridge-v(\d+)\.(\d+)\.(\d+)\.exe$", RegexOptions.IgnoreCase);

    internal enum Choice { RunHere, Install, UseInstalled }

    internal static Version? VersionOf(string path) =>
        VersionedName.Match(Path.GetFileName(path)) is { Success: true } m
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;

    /// <summary>Decides where to run. Debug runs through the dotnet host always run in place.</summary>
    internal static (Choice Choice, string? Path) Decide(string? current, string folder, Version version, IEnumerable<string> installed)
    {
        if (current == null || !Path.GetFileName(current).StartsWith("Switch2Bridge", StringComparison.OrdinalIgnoreCase))
            return (Choice.RunHere, null);
        if (string.Equals(Path.GetDirectoryName(current), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return (Choice.RunHere, null);
        var newest = installed.Where(f => VersionOf(f) != null).OrderBy(VersionOf).LastOrDefault();
        return newest != null && VersionOf(newest) > version
            ? (Choice.UseInstalled, newest)
            : (Choice.Install, Path.Combine(folder, $"Switch2Bridge-v{version}.exe"));
    }

    static Version Current => new(Updater.Current.Major, Updater.Current.Minor, Updater.Current.Build);

    /// <summary>Hands over to the installed copy if that's where the bridge should run. Returns
    /// true if it did and this process should exit. Any failure just runs from here instead.</summary>
    public static bool HandOff()
    {
        try
        {
            var installed = System.IO.Directory.Exists(Folder) ? System.IO.Directory.GetFiles(Folder, "Switch2Bridge-v*.exe") : [];
            var (choice, path) = Decide(Environment.ProcessPath, Folder, Current, installed);
            if (choice == Choice.RunHere) return false;
            if (choice == Choice.Install)
            {
                System.IO.Directory.CreateDirectory(Folder);
                File.Copy(Environment.ProcessPath!, path!, overwrite: true);
                Log.Info($"Installed to {path}.");
            }
            else Log.Info($"A newer version is installed; starting {path}.");
            var start = new ProcessStartInfo(path!) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
            start.ArgumentList.Add("--after-install");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(start);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"Couldn't install to {Folder}, running from here: {e.Message}");
            return false;
        }
    }

    /// <summary>Removes older versions from the install folder, once this copy runs from it.</summary>
    public static void RemoveOlderVersions()
    {
        var me = Environment.ProcessPath;
        if (me == null || !string.Equals(Path.GetDirectoryName(me), Folder, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var file in System.IO.Directory.GetFiles(Folder, "Switch2Bridge-v*.exe"))
        {
            if (VersionOf(file) is not { } version || version >= Current) continue;
            try { File.Delete(file); Log.Info($"Removed {Path.GetFileName(file)}."); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Couldn't remove {file}: {e.Message}"); }
        }
    }

    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Switch2NativeBridge";
    static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Switch 2 Native Bridge.lnk");

    /// <summary>Adds the Start Menu entry and the Windows "Installed apps" entry, pointed at
    /// this exe. Only for the installed copy; failures are logged and otherwise harmless.</summary>
    public static void RegisterWithWindows()
    {
        var me = Environment.ProcessPath;
        if (me == null || !string.Equals(Path.GetDirectoryName(me), Folder, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "Switch 2 Native Bridge");
            key.SetValue("DisplayVersion", Current.ToString());
            key.SetValue("Publisher", "Jeremy De Jong");
            key.SetValue("InstallLocation", Folder);
            key.SetValue("DisplayIcon", me);
            key.SetValue("UninstallString", $"\"{me}\" --uninstall");
            key.SetValue("URLInfoAbout", $"https://github.com/{Updater.Repository}");
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = me;
            shortcut.WorkingDirectory = Folder;
            shortcut.Description = "Switch 2 controllers over Bluetooth for Steam";
            shortcut.Save();
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't add the Start Menu or Installed apps entry: {e.Message}");
        }
    }

    /// <summary>Run from Windows' Installed apps. Asks first, then removes the startup task, Windows
    /// entries and app folder, and on request the virtual controller drivers and the user's data.</summary>
    public static int Uninstall()
    {
        const string title = "Uninstall Switch 2 Native Bridge";
        if (System.Windows.MessageBox.Show("Remove Switch 2 Native Bridge from this PC?", title,
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return 0;
        bool drivers = System.Windows.MessageBox.Show(
            "Also remove the HIDMaestro virtual controller drivers and certificate?\n\nChoose No if another app on this PC uses HIDMaestro.",
            title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
        bool data = System.Windows.MessageBox.Show(
            "Also delete settings, logs, battery history and recordings?", title,
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

        var problems = new List<string>();
        void Step(string name, Action action)
        {
            try { action(); }
            catch (Exception e) { problems.Add($"{name}: {e.Message}"); }
        }
        Step("Startup task", Autostart.Disable);
        if (drivers)
            Step("Drivers", () =>
            {
                var report = HIDMaestro.HMUninstall.Run();
                Log.Info($"HIDMaestro removal: {report.Status}.");
                if (report.Status == HIDMaestro.HMUninstallStatus.Failed) problems.Add("Drivers: HIDMaestro reported a failure. Its uninstaller may finish the job.");
            });
        Step("Start Menu entry", () => File.Delete(ShortcutPath));
        Step("Installed apps entry", () => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false));

        // This exe is in the folder, so a separate command deletes it after this process exits.
        var targets = data ? $"rmdir /s /q \"{Folder}\" & rmdir /s /q \"{Log.Directory}\"" : $"rmdir /s /q \"{Folder}\"";
        Step("App folder", () => Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & {targets}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        }));

        System.Windows.MessageBox.Show(problems.Count == 0
                ? "Switch 2 Native Bridge has been removed."
                : "Finished, but some steps didn't complete:\n\n" + string.Join("\n", problems),
            title, System.Windows.MessageBoxButton.OK, problems.Count == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        return problems.Count == 0 ? 0 : 1;
    }

    public static void WaitForExit(int pid)
    {
        try { using var process = Process.GetProcessById(pid); process.WaitForExit(20_000); }
        catch (ArgumentException) { } // already gone
    }
}
