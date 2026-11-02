using System.Text;

namespace Switch2Bridge;

/// <summary>Controller firmware versions, from command 10/01 (switch2_controller_research, commands.md).</summary>
sealed record FirmwareInfo(Version Firmware, int Type, Version Bluetooth, Version? Dsp)
{
    /// <summary>The notes list the headset audio channel from firmware 2.0.0.</summary>
    public bool ListedWithHeadsetAudio => Firmware >= new Version(2, 0, 0);

    internal static FirmwareInfo? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7) return null;
        // 0xFF bytes mean no DSP firmware, on controllers still on factory firmware.
        Version? dsp = data.Length >= 11 && data[8] != 0xFF ? new Version(data[8], data[9], data[10]) : null;
        return new FirmwareInfo(new Version(data[0], data[1], data[2]), data[3], new Version(data[4], data[5], data[6]), dsp);
    }

    public override string ToString() =>
        $"firmware {Firmware}, Bluetooth patch {Bluetooth}, DSP {(Dsp?.ToString() ?? "none")}";
}

/// <summary>
/// A read-only experiment for headset audio over Bluetooth. Reads the firmware version,
/// subscribes to the Pro Controller's documented "Input Report (Headset Audio)" channel for
/// a short quiet phase and a talking phase, and records what arrives. It writes nothing to
/// the audio output channel. The format of these bytes is undocumented, so this only shows
/// whether data arrives, how fast, and whether it reacts to sound. It can't decode it.
/// </summary>
static class AudioProbe
{
    public static readonly string Directory = Path.Combine(Log.Directory, "audio-probe");

    // Layout from hid_reports/bluetooth_interface.md, attribute 0x002E.
    const int CounterAt = 0x0, PowerAt = 0x1, StateAt = 0xD, LengthAt = 0xE, DataAt = 0xF;
    internal const int DocumentedLength = 0x32;

    static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Phase = TimeSpan.FromSeconds(10);
    const long InputStallMs = 3000;

    internal readonly record struct Frame(byte Counter, byte Power, byte State, byte[] Data)
    {
        public int BatteryLevel => (Power >> 2) & 0xF;
        public bool ExternalPower => (Power & 1) != 0;
        public bool Charging => (Power & 2) != 0;
    }

    internal static Frame? Parse(ReadOnlySpan<byte> report)
    {
        if (report.Length <= LengthAt) return null;
        int length = Math.Min(report[LengthAt], report.Length - DataAt);
        return new Frame(report[CounterAt], report[PowerAt], report[StateAt], report.Slice(DataAt, Math.Max(0, length)).ToArray());
    }

    internal sealed record PhaseStats(int Reports, int Malformed, int LostCounters, int Distinct, double NonZeroPercent, double ChangedPercent, byte[] States);

    /// <summary>Summarises one phase: how many reports, how many were skipped by the 8-bit
    /// counter, and how much the audio bytes vary from report to report.</summary>
    internal static PhaseStats Analyze(IReadOnlyList<byte[]> reports)
    {
        int malformed = 0, lost = 0;
        long bytes = 0, nonZero = 0, compared = 0, changed = 0;
        var distinct = new HashSet<string>();
        var states = new SortedSet<byte>();
        Frame? previous = null;
        foreach (var report in reports)
        {
            if (Parse(report) is not Frame frame) { malformed++; continue; }
            states.Add(frame.State);
            distinct.Add(Convert.ToHexString(frame.Data));
            bytes += frame.Data.Length;
            nonZero += frame.Data.Count(b => b != 0);
            if (previous is Frame p)
            {
                lost += (byte)(frame.Counter - p.Counter - 1);
                int shared = Math.Min(p.Data.Length, frame.Data.Length);
                compared += shared;
                for (int i = 0; i < shared; i++) if (p.Data[i] != frame.Data[i]) changed++;
            }
            previous = frame;
        }
        return new PhaseStats(reports.Count, malformed, lost, distinct.Count,
            bytes == 0 ? 0 : nonZero * 100.0 / bytes,
            compared == 0 ? 0 : changed * 100.0 / compared,
            [.. states]);
    }

    /// <summary>What the two phases can and can't show, without claiming more than that.</summary>
    internal static string Verdict(PhaseStats quiet, PhaseStats talking)
    {
        if (quiet.Reports + talking.Reports == 0)
            return "No reports arrived. The channel exists, but the controller sent nothing on it here.";
        if (quiet.Reports + talking.Reports == quiet.Malformed + talking.Malformed)
            return "Reports arrived but were too short to hold the documented layout. The raw capture may show why.";
        if (quiet.States.All(s => s == 0) && talking.States.All(s => s == 0))
            return "The controller reports no headset attached (state 0). Plug in a headset with a mic and run it again.";
        if (quiet.NonZeroPercent == 0 && talking.NonZeroPercent == 0)
            return "A headset is detected, but every audio byte is zero. The microphone may need turning on by a command we don't know yet.";
        if (Math.Abs(talking.ChangedPercent - quiet.ChangedPercent) >= 10 || Math.Abs(talking.NonZeroPercent - quiet.NonZeroPercent) >= 10)
            return "Promising: the audio bytes change noticeably when you talk, so this looks like live microphone data. That doesn't reveal the codec.";
        return "Data is arriving, but it looks about the same quiet or talking. It could be constant-rate compressed audio, or something else; this test can't tell.";
    }

    static string Describe(string name, PhaseStats s, TimeSpan duration, int inputReports) =>
        $"{name}: {s.Reports} audio reports ({s.Reports / duration.TotalSeconds:0.0}/s), {s.LostCounters} skipped by the counter, " +
        $"{s.Malformed} malformed, {s.Distinct} distinct, {s.NonZeroPercent:0.0}% non-zero bytes, " +
        $"{s.ChangedPercent:0.0}% bytes changed per report, states [{string.Join(", ", s.States.Select(b => $"0x{b:X2}"))}], " +
        $"{inputReports / duration.TotalSeconds:0.0} normal input reports/s";

    /// <summary>Runs the experiment on a connected Pro Controller and returns the verdict.
    /// Always unsubscribes, and stops early if normal input stalls.</summary>
    public static async Task<string> RunAsync(SwitchController controller, IProgress<string> progress, CancellationToken ct)
    {
        var report = new StringBuilder();
        void Note(string line) { Log.Info("Audio probe: " + line); report.AppendLine(line); }

        var firmware = await controller.ReadFirmwareAsync(ct);
        Note(firmware != null ? $"{controller.Name}: {firmware}." : "Couldn't read the firmware version.");
        if (!controller.HasHeadsetChannel)
        {
            var missing = firmware is { ListedWithHeadsetAudio: false }
                ? "This controller has no headset audio channel. The notes list it from firmware 2.0.0, so updating the controller on a Switch 2 may add it."
                : "This controller doesn't expose the headset audio channel.";
            Note(missing);
            Save(report, null);
            return missing;
        }

        var gate = new object();
        var captured = new List<(long Ms, int Phase, byte[] Report)>();
        int phase = 0; // 0 = settling, 1 = quiet, 2 = talking
        var inputCounts = new int[3];
        long lastInput = Environment.TickCount64;
        long start = Environment.TickCount64;
        void OnAudio(byte[] data) { lock (gate) captured.Add((Environment.TickCount64 - start, phase, data)); }
        void OnInput(byte[] _) { lock (gate) { inputCounts[phase]++; lastInput = Environment.TickCount64; } }

        controller.InputReport += OnInput;
        string? stopped = null;
        try
        {
            await controller.StartHeadsetCaptureAsync(OnAudio, ct);
            async Task<bool> Wait(TimeSpan duration)
            {
                var end = Environment.TickCount64 + (long)duration.TotalMilliseconds;
                while (Environment.TickCount64 < end)
                {
                    await Task.Delay(250, ct);
                    long sinceInput;
                    lock (gate) sinceInput = Environment.TickCount64 - lastInput;
                    if (sinceInput > InputStallMs) return false;
                }
                return true;
            }

            progress.Report("Starting...");
            bool ok = await Wait(Settle);
            if (ok)
            {
                progress.Report($"Stay quiet for {Phase.TotalSeconds:0} seconds, and don't touch the mic.");
                lock (gate) phase = 1;
                ok = await Wait(Phase);
            }
            if (ok)
            {
                progress.Report($"Now talk into the mic for {Phase.TotalSeconds:0} seconds.");
                lock (gate) phase = 2;
                ok = await Wait(Phase);
            }
            if (!ok) stopped = "Stopped early: normal controller input stalled while listening, so listening to this channel may interfere with it.";
        }
        finally
        {
            await controller.StopHeadsetCaptureAsync(OnAudio);
            controller.InputReport -= OnInput;
        }

        List<(long Ms, int Phase, byte[] Report)> all;
        int[] inputs;
        lock (gate) { all = [.. captured]; inputs = [.. inputCounts]; }
        var quiet = Analyze(all.Where(c => c.Phase == 1).Select(c => c.Report).ToList());
        var talking = Analyze(all.Where(c => c.Phase == 2).Select(c => c.Report).ToList());
        Note(Describe("Quiet", quiet, Phase, inputs[1]));
        Note(Describe("Talking", talking, Phase, inputs[2]));
        if (all.Select(c => Parse(c.Report)).OfType<Frame>().Cast<Frame?>().LastOrDefault() is Frame last)
            Note($"Power byte: battery level {last.BatteryLevel} of 9, external power {last.ExternalPower}, charging {last.Charging}.");

        var verdict = stopped ?? Verdict(quiet, talking);
        Note(verdict);
        Save(report, all);
        return verdict;
    }

    /// <summary>Saves the summary and the raw reports. The raw file can contain whatever the
    /// mic picked up, in an undecoded form, and stays on this PC.</summary>
    static void Save(StringBuilder report, List<(long Ms, int Phase, byte[] Report)>? captured)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var stem = Path.Combine(Directory, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            File.WriteAllText(stem + ".txt", report.ToString());
            if (captured == null) return;
            // Each record: milliseconds (u32), phase (u8: 0 settling, 1 quiet, 2 talking), length (u16), report bytes.
            using var writer = new BinaryWriter(File.Create(stem + ".bin"));
            foreach (var (ms, phase, data) in captured)
            {
                writer.Write((uint)ms);
                writer.Write((byte)phase);
                writer.Write((ushort)data.Length);
                writer.Write(data);
            }
            Log.Info($"Audio probe: saved {stem}.txt and .bin");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Audio probe: could not save results: {e.Message}");
        }
    }
}
