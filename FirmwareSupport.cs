using System.Text.Json.Nodes;

namespace Switch2Bridge;

/// <summary>
/// The running record of controller firmware: firmware-support.json (built in) lists versions
/// confirmed on a real controller, and firmware-history.csv notes each version this PC has seen.
/// The history stays on this PC. A version not on the list is untested, not unsupported.
/// </summary>
static class FirmwareSupport
{
    public static readonly string HistoryFile = Path.Combine(Log.Directory, "firmware-history.csv");
    const string Header = "first seen,controller,firmware,bluetooth patch,dsp,headset channel,tested";

    static readonly Lazy<HashSet<(string Controller, Version Firmware)>> Tested = new(() =>
    {
        using var stream = typeof(FirmwareSupport).Assembly.GetManifestResourceStream("firmware-support.json")!;
        return ParseTested(JsonNode.Parse(stream)!);
    });

    internal static HashSet<(string Controller, Version Firmware)> ParseTested(JsonNode root)
    {
        var tested = new HashSet<(string, Version)>();
        foreach (var entry in root["tested"]!.AsArray())
            tested.Add(((string)entry!["controller"]!, Version.Parse((string)entry["firmware"]!)));
        return tested;
    }

    static string Controller(ControllerKind kind) => kind == ControllerKind.ProController ? "Pro" : kind.ToString();

    public static bool IsTested(ControllerKind kind, Version firmware) => Tested.Value.Contains((Controller(kind), firmware));

    /// <summary>Adds a row the first time this combination is seen; returns whether it did.</summary>
    internal static bool Record(string file, ControllerKind kind, FirmwareInfo info, bool headsetChannel, bool tested, DateTime now)
    {
        var key = $",{Controller(kind)},{info.Firmware},{info.Bluetooth},{info.Dsp?.ToString() ?? "none"},{(headsetChannel ? "yes" : "no")},";
        if (File.Exists(file) && File.ReadLines(file).Any(line => line.Contains(key))) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file)) File.WriteAllText(file, Header + Environment.NewLine);
        File.AppendAllText(file, $"{now:yyyy-MM-dd}{key}{(tested ? "yes" : "no")}{Environment.NewLine}");
        return true;
    }

    public static void Record(ControllerKind kind, FirmwareInfo info, bool headsetChannel)
    {
        try
        {
            if (Record(HistoryFile, kind, info, headsetChannel, IsTested(kind, info.Firmware), DateTime.Now))
                Log.Info($"New firmware for this PC's record: {Controller(kind)} {info.Firmware}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not update the firmware record: {e.Message}");
        }
    }
}
