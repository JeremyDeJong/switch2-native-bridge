using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Concentus;
using Concentus.Enums;

namespace Switch2Bridge;

/// <summary>
/// An experiment to work out the Pro Controller's headphone audio. On firmware 2.1.4, a quiet
/// Opus tone sent to the "Output Report (Headset Audio)" channel as report ID, state byte,
/// length, then frame was audible from the controller's body, in repeated runs, with normal
/// input running. Every odd state byte up to 0x0F played and every even one didn't, so bit 0
/// turns audio on. This test plays a melody, the same tone at rising levels in case the
/// headphones were just too quiet, then the higher state bits with audio on, to look for what
/// sends it to the headphones. It only writes to that
/// documented audio channel, never the neighbouring "Unknown + Command" channel, where stray
/// bytes could be taken as real controller commands.
/// </summary>
sealed class AudioLab(SwitchController controller)
{
    internal enum Heard { Nothing, Controller, Headphones }

    /// <summary>One thing to play. Melody steps change note each second.</summary>
    internal readonly record struct Step(byte State, bool Melody, int Seconds, short Level = ToneLevel)
    {
        public override string ToString() => Melody ? "rising melody" : Level != ToneLevel ? $"state 0x{State:X2} at level {Level}" : $"state 0x{State:X2}";
    }

    internal const int AudioBlock = 0x32;
    const byte ReportId = 0x00;
    const byte KnownAudibleState = 0x0D;
    const int FrameMs = 20, SampleRate = 48000, FrameSamples = SampleRate * FrameMs / 1000;
    const short ToneLevel = 800;      // about -32 dB of full scale
    internal const short LoudestLevel = 3200; // about -20 dB; encoding at this bitrate can peak about twice that
    const double ToneHz = 660;
    static readonly double[] MelodyHz = [523.25, 587.33, 659.25, 698.46, 783.99];
    static readonly TimeSpan Gap = TimeSpan.FromSeconds(1);

    internal static readonly Step[] Steps = BuildSteps();

    static Step[] BuildSteps()
    {
        var steps = new List<Step> { new(KnownAudibleState, true, MelodyHz.Length) };
        // Louder, in case the headphones were working and just too quiet to notice.
        foreach (short level in new short[] { ToneLevel, 1600, LoudestLevel }) steps.Add(new(KnownAudibleState, false, 2, level));
        // Each mix of the high bits, with audio on: alone, and with the bits the controller
        // itself reports when headphones are in.
        for (int high = 1; high <= 0x0F; high++)
        {
            steps.Add(new((byte)(high << 4 | 0x01), false, 2));
            steps.Add(new((byte)(high << 4 | KnownAudibleState), false, 2));
        }
        return [.. steps];
    }

    public static TimeSpan TotalTime => Steps.Aggregate(TimeSpan.Zero, (t, s) => t + TimeSpan.FromSeconds(s.Seconds) + Gap);

    /// <summary>One packet for the output channel: report ID, state, block length, then the
    /// Opus frame padded to the 50-byte block, mirroring the input report's audio block.</summary>
    internal static byte[] BuildPacket(byte state, ReadOnlySpan<byte> opus)
    {
        if (opus.Length > AudioBlock) throw new ArgumentException("Opus frame too long for the block.", nameof(opus));
        var packet = new byte[3 + AudioBlock];
        packet[0] = ReportId;
        packet[1] = state;
        packet[2] = AudioBlock;
        opus.CopyTo(packet.AsSpan(3));
        return packet;
    }

    /// <summary>The note a melody step plays at a given moment.</summary>
    internal static double MelodyNote(TimeSpan elapsed) =>
        MelodyHz[Math.Clamp((int)elapsed.TotalSeconds, 0, MelodyHz.Length - 1)];

    /// <summary>The low-delay Opus mode, which is what produces the F8 (mono) first byte seen on
    /// the controller's own frames: music mode, full band, 20 ms.</summary>
    internal static IOpusEncoder NewEncoder(bool stereo)
    {
        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, stereo ? 2 : 1, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        encoder.Bitrate = stereo ? 18000 : 16000;
        encoder.UseVBR = false;
        // Left to itself at this low bitrate, Opus narrows the band, which changes the first byte.
        encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_FULLBAND;
        encoder.MaxBandwidth = OpusBandwidth.OPUS_BANDWIDTH_FULLBAND;
        // Both channels carry the same tone, which it would otherwise send as mono.
        if (stereo) encoder.ForceChannels = 2;
        return encoder;
    }

    /// <summary>The next 20 ms of a quiet tone as one Opus frame.</summary>
    internal static byte[] EncodeToneFrame(IOpusEncoder encoder, bool stereo, double hz, ref double phase, short level = ToneLevel)
    {
        int channels = stereo ? 2 : 1;
        var pcm = new short[FrameSamples * channels];
        double step = 2 * Math.PI * hz / SampleRate;
        for (int i = 0; i < FrameSamples; i++)
        {
            short sample = (short)(Math.Sin(phase) * level);
            phase += step;
            for (int c = 0; c < channels; c++) pcm[i * channels + c] = sample;
        }
        phase %= 2 * Math.PI;
        var output = new byte[AudioBlock];
        int length = encoder.Encode(pcm, FrameSamples, output, output.Length);
        return output[..length];
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    sealed class Tally
    {
        public int Sent, Failed;
        public string? FirstError;
        public Heard Heard;
    }

    readonly Tally[] _tallies = Steps.Select(_ => new Tally()).ToArray();
    volatile int _current;

    /// <summary>Marks the sound playing now, or in the pause just after it. Headphones win over
    /// the controller if both are marked, since that's the one being looked for.</summary>
    public void MarkHeard(Heard where)
    {
        int index = Math.Clamp(_current, 0, Steps.Length - 1);
        lock (_tallies)
            if (where > _tallies[index].Heard) _tallies[index].Heard = where;
        Log.Info($"Audio test sounds: heard from the {where} during {Steps[index]}.");
    }

    /// <summary>Plays every step in turn, returning a short result. Cancelling stops early and
    /// keeps the results. Normal input keeps running, because the audio input channel stays off.</summary>
    public async Task<string> RunAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!controller.HasHeadsetOutput) return "This controller has no headset audio output channel, so there's nothing to test.";
        timeBeginPeriod(1);
        var total = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < Steps.Length; i++)
            {
                _current = i;
                progress.Report(Steps[i].Melody
                    ? "Playing 5 rising notes. Is it a clear melody, or just a buzz?"
                    : $"Sound {i} of {Steps.Length - 1}: say where you hear it, if anywhere.");
                await PlayAsync(Steps[i], _tallies[i], ct);
                await Task.Delay(Gap, ct);
            }
        }
        catch (OperationCanceledException) { progress.Report("Stopping..."); }
        finally
        {
            timeEndPeriod(1);
            try { await controller.WriteHeadsetOutputAsync(BuildPacket(KnownAudibleState, AudioProbe.OpusSilence)); }
            catch (Exception e) { Log.Warn($"Audio test sounds: final silence failed: {e.Message}"); }
        }
        return Finish(total.Elapsed);
    }

    async Task PlayAsync(Step step, Tally tally, CancellationToken ct)
    {
        var encoder = NewEncoder(stereo: false);
        double phase = 0;
        var play = TimeSpan.FromSeconds(step.Seconds);
        var clock = Stopwatch.StartNew();
        long next = 0;
        while (clock.Elapsed < play)
        {
            ct.ThrowIfCancellationRequested();
            double hz = step.Melody ? MelodyNote(clock.Elapsed) : ToneHz;
            var packet = BuildPacket(step.State, EncodeToneFrame(encoder, false, hz, ref phase, step.Level));
            try
            {
                var status = await controller.WriteHeadsetOutputAsync(packet);
                if (status == Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success) tally.Sent++;
                else { tally.Failed++; tally.FirstError ??= status.ToString(); }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                tally.Failed++;
                tally.FirstError ??= e.Message;
            }
            next += FrameMs;
            while (clock.ElapsedMilliseconds < next) await Task.Delay(1, ct);
        }
    }

    string Finish(TimeSpan elapsed)
    {
        var report = new StringBuilder();
        report.AppendLine($"Audio test sounds, {elapsed.TotalSeconds:0} s.");
        var controllerSteps = new List<string>();
        var headphoneSteps = new List<string>();
        lock (_tallies)
            for (int i = 0; i < Steps.Length; i++)
            {
                var t = _tallies[i];
                if (t.Sent + t.Failed == 0) continue;
                if (t.Heard == Heard.Controller) controllerSteps.Add(Steps[i].ToString());
                if (t.Heard == Heard.Headphones) headphoneSteps.Add(Steps[i].ToString());
                report.AppendLine($"{i,2}. {Steps[i]}: heard from {(t.Heard == Heard.Nothing ? "nowhere" : t.Heard.ToString().ToLowerInvariant())}, {t.Sent} sent, {t.Failed} failed{(t.FirstError != null ? $" ({t.FirstError})" : "")}");
            }
        var verdict = headphoneSteps.Count > 0
            ? $"Heard in the headphones with: {string.Join(", ", headphoneSteps)}."
            : controllerSteps.Count > 0
                ? $"Heard from the controller only, with: {string.Join(", ", controllerSteps)}. None reached the headphones."
                : "Nothing heard.";
        report.AppendLine(verdict);

        Log.Info("Audio test sounds:" + Environment.NewLine + report);
        try
        {
            Directory.CreateDirectory(AudioProbe.Directory);
            File.WriteAllText(Path.Combine(AudioProbe.Directory, $"sounds-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), report.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Audio test sounds: couldn't save results: {e.Message}");
        }
        return verdict;
    }
}
