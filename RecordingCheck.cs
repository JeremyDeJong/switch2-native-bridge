using System.Text;
using System.Text.Json.Nodes;

namespace Switch2Bridge;

/// <summary>
/// Replays recordings through the bridge's conversion code, with no controller, Bluetooth
/// or driver. Each report must come out of the persona's report program with its buttons,
/// sticks, motion and battery unchanged, and the decoded events must match the saved
/// .events.txt next to the recording, so a later change can't quietly alter behaviour.
/// </summary>
static class RecordingCheck
{
    const string EventsSuffix = ".events.txt";

    internal sealed record Result(int Reports, int Mismatches, bool Truncated, List<string> Events);

    static (int State, int Report)[] Bits()
    {
        var json = VirtualPad.ProfileJson("recording-check", new byte[FlashDump.FactoryLength], new byte[FlashDump.UserLength]);
        var program = json["inputReports"]!.AsArray().Single(r => (string?)r!["reportId"] == "0x05")!["program"]!.AsArray();
        return program.Select(op => op!["bits"]).OfType<JsonArray>().Single()
            .Select(p => ((int)p![0]!, (int)p![1]!)).ToArray();
    }

    internal static Result Run(Stream recording)
    {
        var entries = InputRecording.Read(recording, out bool truncated);
        var bits = Bits();
        uint mapped = bits.Aggregate(0u, (m, b) => m | 1u << (b.Report - 40));
        var merged = VirtualPad.NeutralReport();
        var decoders = new Dictionary<ControllerKind, InputDecoder>();
        var events = new List<string>();
        int mismatches = 0;

        foreach (var (ms, kind, report) in entries)
        {
            if (!decoders.TryGetValue(kind, out var decoder)) decoders[kind] = decoder = new InputDecoder();
            foreach (var line in decoder.Feed(ms, report)) events.Add($"{ms,8} {kind,-13} {line}");
            if (report.Length < 60) continue;

            if (kind == ControllerKind.ProController) report.AsSpan(0, Math.Min(report.Length, merged.Length)).CopyTo(merged);
            else VirtualPad.Merge(merged, kind, report, decoders.ContainsKey(ControllerKind.JoyConRight));

            var wired = SelfTest.Replay(merged, bits);
            bool same = (BitConverter.ToUInt32(wired, 5) & mapped) == (BitConverter.ToUInt32(merged, 4) & mapped)
                && wired.AsSpan(11, 6).SequenceEqual(merged.AsSpan(10, 6))
                && wired.AsSpan(49, 12).SequenceEqual(merged.AsSpan(48, 12))
                && wired.AsSpan(32, 4).SequenceEqual(merged.AsSpan(31, 4));
            if (!same) mismatches++;
        }
        return new Result(entries.Count, mismatches, truncated, events);
    }

    /// <summary>Checks every recording in a folder. With <paramref name="update"/>, saves each
    /// one's current events as the expected ones; review that diff before committing it.</summary>
    public static int CheckFolder(string folder, bool update)
    {
        var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.s2rec") : [];
        if (files.Length == 0)
        {
            Console.WriteLine($"No recordings in {folder}, nothing to replay.");
            return 0;
        }

        int failures = 0;
        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            try
            {
                using var stream = File.OpenRead(file);
                var result = Run(stream);
                var actual = string.Join("\n", result.Events) + "\n";
                var expectedFile = Path.ChangeExtension(file, null) + EventsSuffix;
                string problem = result.Mismatches > 0 ? $"{result.Mismatches} report(s) changed in conversion"
                    : result.Reports == 0 ? "no reports"
                    : "";
                if (problem.Length == 0)
                {
                    if (update) File.WriteAllText(expectedFile, actual, new UTF8Encoding(false));
                    else if (!File.Exists(expectedFile)) problem = "no saved events; review it, then run with --update";
                    else if (File.ReadAllText(expectedFile).ReplaceLineEndings("\n") != actual) problem = "events differ from the saved ones";
                }
                Console.WriteLine(problem.Length == 0
                    ? $"PASS {name}: {result.Reports} reports, {result.Events.Count} events{(result.Truncated ? " (cut short at the end)" : "")}"
                    : $"FAIL {name}: {problem}");
                if (problem.Length > 0) failures++;
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
            {
                Console.WriteLine($"FAIL {name}: {e.Message}");
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }
}
