using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Switch2Bridge;

/// <summary>
/// Builds a zip for a bug report: versions, setup, controller status, settings, firmware
/// history and recent logs. Bluetooth addresses and the Windows user name are removed.
/// Battery logs, recordings and audio test captures are left out. Nothing is sent anywhere.
/// </summary>
static class Diagnostics
{
    public static readonly string Directory = Path.Combine(Log.Directory, "diagnostics");
    const int LogLines = 2000;

    static readonly Regex ColonAddress = new(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b");
    static readonly Regex PlainAddress = new(@"\b[0-9A-Fa-f]{12}\b");

    /// <summary>Removes Bluetooth addresses, the profile path and the user name.</summary>
    internal static string Redact(string text, string userProfile, string userName)
    {
        text = ColonAddress.Replace(text, "XX:XX:XX:XX:XX:XX");
        text = PlainAddress.Replace(text, "XXXXXXXXXXXX");
        if (userProfile.Length > 0) text = text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (userName.Length > 0) text = text.Replace(userName, "[user]", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    static string Clean(string text) => Redact(text,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);

    /// <summary>The last lines of a file another part of the app may be writing to.</summary>
    static string Tail(string file)
    {
        if (!File.Exists(file)) return "";
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Enqueue(line);
            if (lines.Count > LogLines) lines.Dequeue();
        }
        return string.Join(Environment.NewLine, lines);
    }

    static string DescribePad(PadStatus pad)
    {
        var text = new StringBuilder($"Player {pad.Number}: {pad.Name}, Steam input {(pad.Isolated ? "paused" : "active")}\n");
        foreach (var s in pad.Sides)
            text.Append($"  {s.Name}: {(s.Live ? "connected" : "not connected")}, {s.Firmware?.ToString() ?? "firmware unknown"}, " +
                $"headset channel {s.HeadsetChannel}, battery level {s.PowerLevel?.ToString() ?? "unknown"}, {s.BatteryMv?.ToString() ?? "?"} mV, " +
                $"charging {s.Charging}, {s.ReportsPerSecond} reports/s, avg gap {s.AvgGapMs:0.0} ms, worst {s.WorstGapMs:0} ms, {s.StallsPerMinute} stalls/min\n");
        return text.ToString();
    }

    public static string Create(IReadOnlyList<PadStatus> pads, IReadOnlyList<string> setupWarnings, string adapter, bool autostart)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"Switch2Bridge-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(Clean(content));
        }

        var summary = new StringBuilder();
        summary.AppendLine($"Switch 2 Native Bridge {Updater.Current.ToString(3)}");
        summary.AppendLine($"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
        summary.AppendLine($"Bluetooth adapter: {adapter}");
        summary.AppendLine($"Start with Windows: {autostart}");
        summary.AppendLine();
        summary.AppendLine("Setup checks:");
        foreach (var warning in setupWarnings) summary.AppendLine("  " + warning);
        if (setupWarnings.Count == 0) summary.AppendLine("  no problems found");
        summary.AppendLine();
        summary.AppendLine("Controllers:");
        foreach (var pad in pads) summary.Append(DescribePad(pad));
        if (pads.Count == 0) summary.AppendLine("  none");
        Add("summary.txt", summary.ToString());

        Add("settings.json", Settings.ForDiagnostics());
        Add("bridge.log", Tail(Log.FilePath));
        Add("firmware-history.csv", Tail(FirmwareSupport.HistoryFile));
        if (System.IO.Directory.Exists(InputCapture.LogDirectory))
        {
            var latest = System.IO.Directory.GetFiles(InputCapture.LogDirectory, "inputs-*.log").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).LastOrDefault();
            if (latest != null) Add("inputs-latest.log", Tail(latest));
        }
        Log.Info($"Diagnostics report created: {path}");
        return path;
    }
}
