using System.Text.Json.Nodes;
using HIDMaestro;

namespace Switch2Bridge;

/// <summary>
/// Offline checks that need no controller, driver or elevation: replays the persona's
/// own report-0x05 program (read from the profile JSON) over the state block the bridge
/// builds, and checks the wired report that comes out carries the Bluetooth report's
/// bytes unchanged. Also checks Joy-Con merging and the composed flash image.
/// </summary>
static class SelfTest
{
    public static int Run(string? profileOutDir)
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            if (ok) Log.Ok(what);
            else { Log.Error(what); failures++; }
        }

        var (factory, user) = VirtualPad.ComposeFlash(new FlashDump(new byte[0x140], new byte[0x40]), null, null);
        var json = VirtualPad.ProfileJson("switch2-pro-bridge-selftest", factory, user);
        var program = json["inputReports"]!.AsArray()
            .Single(r => (string?)r!["reportId"] == "0x05")!["program"]!.AsArray();
        var bits = program.Select(op => op!["bits"]).OfType<JsonArray>().Single()
            .Select(p => (State: (int)p![0]!, Report: (int)p![1]!)).ToArray();

        // Every button on its own: the persona must put it on the wired report bit that
        // sits on the same Bluetooth report bit, one byte later (after the report ID).
        int passing = 0;
        for (int bleBit = 0; bleBit < 32; bleBit++)
        {
            var ble = new byte[63];
            BitConverter.TryWriteBytes(ble.AsSpan(4), 1u << bleBit);
            uint wiredButtons = BitConverter.ToUInt32(Replay(ble, bits), 5);
            // Wired buttons start at report byte 5 (bit 40), Bluetooth buttons at byte 4.
            bool known = bits.Any(b => b.Report - 40 == bleBit);
            if (known && wiredButtons == 1u << bleBit) passing++;
            else if (known) Check(false, $"button bit {bleBit} lands on 0x{wiredButtons:X8}");
            else if (wiredButtons != 0) Check(false, $"unmapped bit {bleBit} leaks onto 0x{wiredButtons:X8}");
        }
        Check(passing == 21, $"{passing}/21 buttons pass through on their own bit");

        // Random reports: buttons, both sticks and the six IMU words must arrive unchanged.
        uint mask = bits.Aggregate(0u, (m, b) => m | 1u << (b.Report - 40));
        var rng = new Random(1234);
        bool randomOk = true;
        for (int i = 0; i < 2000 && randomOk; i++)
        {
            var ble = new byte[63];
            rng.NextBytes(ble);
            BitConverter.TryWriteBytes(ble.AsSpan(4), BitConverter.ToUInt32(ble, 4) & mask);
            var wired = Replay(ble, bits);
            randomOk = wired.AsSpan(5, 4).SequenceEqual(ble.AsSpan(4, 4))
                && wired.AsSpan(11, 6).SequenceEqual(ble.AsSpan(10, 6))
                && wired.AsSpan(49, 12).SequenceEqual(ble.AsSpan(48, 12))
                && wired.AsSpan(32, 4).SequenceEqual(ble.AsSpan(31, 4));
            if (!randomOk) Check(false, $"random report {i} differs: ble {Convert.ToHexString(ble)} wired {Convert.ToHexString(wired)}");
        }
        if (randomOk) Check(true, "2000 random reports: buttons, sticks, IMU and battery arrive byte-for-byte");
        Check(program.Any(op => (string?)op!["copy"] == "24-27" && (int?)op["to"] == 32),
            "report 0x05 copies the battery bytes over the persona's fixed 3.8 V");

        // Joy-Con pair: each side keeps its own buttons and stick; motion follows the right Joy-Con.
        Check((VirtualPad.LeftJoyConButtons & VirtualPad.RightJoyConButtons) == 0, "Joy-Con button sets do not overlap");
        Check(((VirtualPad.LeftJoyConButtons | VirtualPad.RightJoyConButtons) & ~mask) == 0, "every Joy-Con button is a mapped button");
        bool mergeOk = true;
        for (int i = 0; i < 500 && mergeOk; i++)
        {
            var left = new byte[63]; var right = new byte[63];
            rng.NextBytes(left); rng.NextBytes(right);
            var merged = VirtualPad.NeutralReport();
            VirtualPad.Merge(merged, ControllerKind.JoyConLeft, left, rightIsLive: true);
            VirtualPad.Merge(merged, ControllerKind.JoyConRight, right, rightIsLive: true);
            uint expect = (BitConverter.ToUInt32(left, 4) & VirtualPad.LeftJoyConButtons)
                | (BitConverter.ToUInt32(right, 4) & VirtualPad.RightJoyConButtons);
            mergeOk = BitConverter.ToUInt32(merged, 4) == expect
                && merged.AsSpan(10, 3).SequenceEqual(left.AsSpan(10, 3))
                && merged.AsSpan(13, 3).SequenceEqual(right.AsSpan(13, 3))
                && merged.AsSpan(48, 12).SequenceEqual(right.AsSpan(48, 12));
        }
        Check(mergeOk, "Joy-Con pair merges into one Pro-shaped report");

        var leftOnly = VirtualPad.NeutralReport();
        var lone = new byte[63];
        rng.NextBytes(lone);
        VirtualPad.Merge(leftOnly, ControllerKind.JoyConLeft, lone, rightIsLive: false);
        Check(leftOnly.AsSpan(48, 12).SequenceEqual(lone.AsSpan(48, 12)), "a lone left Joy-Con supplies motion");
        Check(BitConverter.ToUInt16(VirtualPad.NeutralReport(), 31) == 3800, "a pad with no controller yet reports 3.8 V, not an empty battery");

        // Composed flash for a Joy-Con pair: left in stick slot 1, right in slot 2, Pro Controller 2 ID.
        var leftFlash = new FlashDump(Fill(0x140, 0x11), Fill(0x40, 0x22));
        var rightFlash = new FlashDump(Fill(0x140, 0x33), Fill(0x40, 0x44));
        var (jf, ju) = VirtualPad.ComposeFlash(null, leftFlash, rightFlash);
        Check(jf[0xA8] == 0x11 && jf[0xE8] == 0x33 && jf[0x100] == 0x33, "Joy-Con stick calibrations land in slots 1 and 2");
        Check(BitConverter.ToUInt16(jf, 0x12) == 0x057E && BitConverter.ToUInt16(jf, 0x14) == 0x2069, "composed flash reports a Pro Controller 2");
        Check(ju[0x02] == 0x22 && ju[0x22] == 0x44, "Joy-Con user calibrations land in slots 1 and 2");
        var (rf, _) = VirtualPad.ComposeFlash(null, null, rightFlash);
        Check(rf[0xE8] == 0x33 && rf[0xA8] != 0x33, "a lone right Joy-Con fills slot 2 and leaves slot 1 at defaults");

        // Rumble strength scales both amplitudes and leaves frequencies and headers alone.
        var thump = Rumble.Packet(Rumble.Thump, 3);
        var half = (byte[])thump.Clone();
        Rumble.Scale(half, 0.5);
        Check(half.AsSpan().SequenceEqual(Rumble.Packet(Rumble.Entry(0x060, 0x1A8, 0x0C0, 0x128), 3)),
            "rumble at 50% halves both amplitudes, keeps frequencies");
        var full = (byte[])thump.Clone();
        Rumble.Scale(full, 1.0);
        Check(full.AsSpan().SequenceEqual(thump), "rumble at 100% passes through unchanged");
        var none = (byte[])thump.Clone();
        Rumble.Scale(none, 0);
        Check(none.AsSpan().SequenceEqual(Rumble.Packet(Rumble.Entry(0x060, 0, 0x0C0, 0), 3)), "rumble at 0% is silent");

        // Battery percentage only once a real range has been seen.
        Check(BatteryLog.PercentFrom(3000, 3400, 200, 3200) == 50, "learned battery range maps mid-voltage to 50%");
        Check(BatteryLog.PercentFrom(3000, 3400, 10, 3200) == null, "no percentage from too few samples");
        Check(BatteryLog.PercentFrom(3100, 3150, 500, 3120) == null, "no percentage from too narrow a range");

        // Updates: tags parse, and settings from 0.2-0.5 still read.
        Check(Updater.ParseVersion("v0.6.0") == new Version(0, 6, 0) && Updater.ParseVersion("v1.2") == new Version(1, 2, 0),
            "release tags parse as versions");
        Check(Settings.FromJson("{\"autostartConfigured\":true}") is { AutostartConfigured: true, RumbleStrength: 1.0, IdleOffMinutes: 15 },
            "the 0.5 settings file still reads, with new defaults");

        // Link stats: steady 15 ms reports with one 80 ms gap is one stall, worst ~80 ms.
        var stats = new LinkStats("selftest");
        long tick = System.Diagnostics.Stopwatch.Frequency / 1000, t = 1;
        for (int i = 0; i < 200; i++) { t += (i == 100 ? 80 : 15) * tick; stats.Report(t); }
        var (avgGap, worstGap, stallCount) = stats.Read(t);
        Check(stallCount == 1 && Math.Abs(worstGap - 80) < 1 && avgGap is > 14 and < 20, $"link stats: 1 stall, worst {worstGap:0} ms, avg {avgGap:0.0} ms");

        // Input events: presses, releases, gaps, sticks past a threshold, unknown bits.
        byte[] InputReport(uint pressed, int lx = 2048)
        {
            var r = VirtualPad.NeutralReport();
            BitConverter.TryWriteBytes(r.AsSpan(4), pressed);
            int packed = lx | 2048 << 12;
            r[10] = (byte)packed; r[11] = (byte)(packed >> 8); r[12] = (byte)(packed >> 16);
            return r;
        }
        var decoder = new InputDecoder();
        Check(decoder.Feed(0, InputReport(0)).Count == 0, "the first report at rest causes no events");
        Check(decoder.Feed(15, InputReport(0x08)).SequenceEqual(["A down"]), "a press is logged");
        Check(decoder.Feed(30, InputReport(0x08)).Count == 0, "a held button logs nothing more");
        Check(decoder.Feed(115, InputReport(0)).SequenceEqual(["Bluetooth gap 85 ms", "A up (held 100 ms)"]), "a release gives its hold time, and a long gap is logged");
        Check(decoder.Feed(130, InputReport(0, 2100)).Count == 0, "a small stick move logs nothing");
        Check(decoder.Feed(145, InputReport(0, 3000)).SequenceEqual(["L stick raw x=3000 y=2048"]), "a large stick move is logged");
        Check(decoder.Feed(160, InputReport(1u << 31)).SequenceEqual(["unknown bit 31 down"]), "an unknown button bit is logged, not hidden");
        Check(decoder.Feed(175, new byte[5]).Count == 0, "a too-short report causes no events");

        // Sideways Joy-Con: buttons move to where they now sit, the stick turns with the
        // controller and lands on the left stick, and the rest is untouched.
        Check(SidewaysJoyCon.MapButtons(ControllerKind.JoyConLeft, 0x20000) == 0x01 && SidewaysJoyCon.MapButtons(ControllerKind.JoyConLeft, 0x10000) == 0x08
            && SidewaysJoyCon.MapButtons(ControllerKind.JoyConLeft, 0x80000) == 0x04 && SidewaysJoyCon.MapButtons(ControllerKind.JoyConLeft, 0x40000) == 0x02,
            "sideways left Joy-Con: arrows become Y, A, B, X by position");
        Check(SidewaysJoyCon.MapButtons(ControllerKind.JoyConRight, 0x02) == 0x08 && SidewaysJoyCon.MapButtons(ControllerKind.JoyConRight, 0x08) == 0x04
            && SidewaysJoyCon.MapButtons(ControllerKind.JoyConRight, 0x04) == 0x01 && SidewaysJoyCon.MapButtons(ControllerKind.JoyConRight, 0x01) == 0x02,
            "sideways right Joy-Con: X, A, B, Y move a quarter turn");
        Check(SidewaysJoyCon.MapButtons(ControllerKind.JoyConLeft, 0x200000) == 0x400000 && SidewaysJoyCon.MapButtons(ControllerKind.JoyConLeft, 0x100000) == 0x40
            && SidewaysJoyCon.MapButtons(ControllerKind.JoyConRight, 0x20) == 0x400000 && SidewaysJoyCon.MapButtons(ControllerKind.JoyConRight, 0x10) == 0x40,
            "sideways: SL is L and SR is R on both Joy-Cons");
        foreach (var kind in new[] { ControllerKind.JoyConLeft, ControllerKind.JoyConRight })
        {
            var allPressed = SidewaysJoyCon.MapButtons(kind, 0xFFFFFFFF);
            Check((allPressed & ~mask) == 0, $"sideways {kind}: every result is a button Steam receives");
        }
        byte[] JoyConReport(int stickAt, int x, int y)
        {
            var r = new byte[63];
            int packed = x | y << 12;
            r[stickAt] = (byte)packed; r[stickAt + 1] = (byte)(packed >> 8); r[stickAt + 2] = (byte)(packed >> 16);
            BitConverter.TryWriteBytes(r.AsSpan(31), (ushort)3333);
            short[] motion = [1, 2, 3, 4, 5, 6];
            for (int i = 0; i < 6; i++) BitConverter.TryWriteBytes(r.AsSpan(48 + i * 2), motion[i]);
            return r;
        }
        (int X, int Y) StickAt(byte[] r, int at)
        {
            int v = r[at] | r[at + 1] << 8 | r[at + 2] << 16;
            return (v & 0xFFF, v >> 12);
        }
        short[] Motion(byte[] r) => Enumerable.Range(0, 6).Select(i => BitConverter.ToInt16(r, 48 + i * 2)).ToArray();
        var sideLeft = SidewaysJoyCon.Apply(ControllerKind.JoyConLeft, JoyConReport(10, 1000, 3000));
        var sideRight = SidewaysJoyCon.Apply(ControllerKind.JoyConRight, JoyConReport(13, 1000, 3000));
        Check(StickAt(sideLeft, 10) == (4095 - 3000, 1000) && StickAt(sideLeft, 13) == (2048, 2048), "sideways left: stick turns, right stick stays centred");
        Check(StickAt(sideRight, 10) == (3000, 4095 - 1000) && StickAt(sideRight, 13) == (2048, 2048), "sideways right: its stick becomes the left stick");
        Check(Motion(sideLeft).SequenceEqual(new short[] { -2, 1, 3, -5, -4, 6 }) && Motion(sideRight).SequenceEqual(new short[] { 2, -1, 3, 5, 4, 6 }), "sideways: motion axes turn with the controller");
        Check(BitConverter.ToUInt16(sideRight, 31) == 3333, "sideways: battery passes through");
        Check(BitConverter.ToInt16(SidewaysJoyCon.Apply(ControllerKind.JoyConLeft, MinMotion()), 48) == short.MaxValue, "sideways: the lowest motion value can't overflow when flipped");
        static byte[] MinMotion()
        {
            var r = new byte[63];
            BitConverter.TryWriteBytes(r.AsSpan(50), short.MinValue);
            return r;
        }

        // Install location: copies in once, never downgrades, leaves debug runs alone.
        var v = new Version(0, 7, 0);
        const string folder = @"C:\Apps\Switch2Bridge";
        Check(Installer.Decide(@"C:\Downloads\Switch2Bridge-v0.7.0.exe", folder, v, []) is (Installer.Choice.Install, @"C:\Apps\Switch2Bridge\Switch2Bridge-v0.7.0.exe"), "a downloaded copy installs itself");
        Check(Installer.Decide(@"C:\Apps\Switch2Bridge\Switch2Bridge-v0.7.0.exe", folder, v, []) is (Installer.Choice.RunHere, null), "the installed copy runs in place");
        Check(Installer.Decide(@"C:\Downloads\Switch2Bridge-v0.7.0.exe", folder, v, [@"C:\Apps\Switch2Bridge\Switch2Bridge-v0.8.0.exe"]) is (Installer.Choice.UseInstalled, @"C:\Apps\Switch2Bridge\Switch2Bridge-v0.8.0.exe"), "an older download starts the newer installed copy");
        Check(Installer.Decide(@"C:\Program Files\dotnet\dotnet.exe", folder, v, []) is (Installer.Choice.RunHere, null), "a run through the dotnet host stays put");

        // Fixed players: a controller keeps its number; others avoid numbers that are spoken for.
        Check(Bridge.ChoosePlayer(3, new HashSet<int> { 1 }, new HashSet<int>()) == 3, "a controller gets its remembered player");
        Check(Bridge.ChoosePlayer(null, new HashSet<int>(), new HashSet<int> { 1 }) == 2, "a new controller avoids another's remembered player");
        Check(Bridge.ChoosePlayer(1, new HashSet<int> { 1 }, new HashSet<int> { 2 }) == 3, "a taken player falls through to the next unclaimed one");
        Check(Bridge.ChoosePlayer(null, new HashSet<int> { 1 }, new HashSet<int>(Enumerable.Range(1, 8))) == 2, "if all are claimed, it takes the lowest free one");

        // Setup checks say what's wrong, and stay quiet when nothing is.
        Check(SetupChecks.Evaluate(new(true, true, [], [], true)).Count == 0, "setup checks: nothing to report");
        Check(SetupChecks.Evaluate(new(null, true, [], [], false)).Count == 0, "setup checks: unknown Bluetooth state isn't called a problem");
        var allWrong = SetupChecks.Evaluate(new(false, false, ["DS4Windows"], ["Pro Controller"], false));
        Check(allWrong.Count == 4 && allWrong[0].Contains("turned off") && allWrong[1].Contains("Pro Controller") && allWrong[2].Contains("Steam") && allWrong[3].Contains("DS4Windows"), "setup checks: each problem gets a message");
        Check(SetupChecks.Evaluate(new(true, true, [], ["Pro Controller"], true)).Count == 0, "setup checks: a paired device isn't flagged once a controller is connected");

        // Diagnostics: private details don't reach the report.
        var redacted = Diagnostics.Redact(@"02:00:00:00:00:01 020000000001 C:\Users\Jo Bloggs\AppData Jo Bloggs ok", @"C:\Users\Jo Bloggs", "Jo Bloggs");
        Check(redacted == @"XX:XX:XX:XX:XX:XX XXXXXXXXXXXX %USERPROFILE%\AppData [user] ok", "diagnostics remove addresses and the user name");
        Check(!Settings.ForDiagnostics().Contains("controllers"), "diagnostics settings leave out per-controller data");

        // Isolation: with everything pressed and moving, Steam's state block shows nothing
        // except the real battery.
        var busy = new byte[63];
        Array.Fill(busy, (byte)0xFF);
        BitConverter.TryWriteBytes(busy.AsSpan(31), (ushort)3102);
        Span<byte> isolatedState = stackalloc byte[VirtualPad.StateBlockSize];
        Span<byte> neutralState = stackalloc byte[VirtualPad.StateBlockSize];
        VirtualPad.ToStateBlock(VirtualPad.IsolatedReport(busy), isolatedState);
        VirtualPad.ToStateBlock(VirtualPad.NeutralReport(), neutralState);
        Check(isolatedState[..3].IndexOfAnyExcept((byte)0) < 0, "isolation: no button reaches Steam, including Home");
        Check(isolatedState[4..24].SequenceEqual(neutralState[4..24]), "isolation: sticks centred, no motion");
        Check(BitConverter.ToUInt16(isolatedState[24..]) == 3102, "isolation: the real battery still reaches Steam");

        // Recordings: write, read back, replay, and survive being cut short.
        using (var recording = new MemoryStream())
        {
            using (var writer = new BinaryWriter(recording, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                InputRecording.WriteHeader(writer, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
                InputRecording.WriteEntry(writer, 0, ControllerKind.ProController, InputReport(0));
                InputRecording.WriteEntry(writer, 15, ControllerKind.ProController, InputReport(0x02000000));
                InputRecording.WriteEntry(writer, 40, ControllerKind.ProController, InputReport(0));
            }
            recording.Position = 0;
            var replayed = RecordingCheck.Run(recording);
            Check(replayed is { Reports: 3, Mismatches: 0, Truncated: false } && replayed.Events.Count == 2
                && replayed.Events[0].EndsWith("GL down") && replayed.Events[1].EndsWith("GL up (held 25 ms)"),
                "a recording replays with reports unchanged and the right events");
            recording.SetLength(recording.Length - 10);
            recording.Position = 0;
            Check(RecordingCheck.Run(recording) is { Reports: 2, Truncated: true }, "a recording cut short keeps its complete reports");
        }
        bool rejected = false;
        try { InputRecording.Read(new MemoryStream(new byte[32]), out _); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "a file that isn't a recording is rejected");
        var emptyFolder = Path.Combine(Path.GetTempPath(), $"s2b-empty-{Guid.NewGuid():N}");
        Check(RecordingCheck.CheckFolder(emptyFolder, update: false) == 0, "no recordings means nothing to fail");

        // Firmware record: the built-in list parses, nothing claims tested without an entry,
        // and each combination is recorded once.
        var testedList = FirmwareSupport.ParseTested(JsonNode.Parse("{\"tested\":[{\"controller\":\"Pro\",\"firmware\":\"1.2.3\"}]}")!);
        Check(testedList.Contains(("Pro", new Version(1, 2, 3))) && testedList.Count == 1, "tested firmware list parses");
        Check(!FirmwareSupport.IsTested(ControllerKind.ProController, new Version(99, 0, 0)), "an unlisted firmware counts as untested");
        var fw2 = new FirmwareInfo(new Version(2, 0, 0), 2, new Version(12, 0, 0), null);
        var history = Path.Combine(Path.GetTempPath(), $"s2b-firmware-{Guid.NewGuid():N}.csv");
        try
        {
            var day = new DateTime(2026, 11, 2);
            bool added = FirmwareSupport.Record(history, ControllerKind.ProController, fw2, true, false, day);
            bool repeat = FirmwareSupport.Record(history, ControllerKind.ProController, fw2, true, false, day.AddDays(1));
            bool upgrade = FirmwareSupport.Record(history, ControllerKind.ProController, fw2 with { Firmware = new Version(2, 1, 0) }, true, false, day.AddDays(2));
            Check(added && !repeat && upgrade && File.ReadAllLines(history).Length == 3, "firmware history adds each version once");
        }
        finally { File.Delete(history); }
        string FirmwareLine(params SideStatus[] sides) => PadView.DescribeFirmware(new PadStatus(1, sides.Length > 1 ? "Joy-Con pair" : "Pro Controller 2", sides, 0, 0, 0, 0, [0, 0, 0], false));
        Check(FirmwareLine(new SideStatus("Pro", true, null, false, 0, Firmware: fw2, HeadsetChannel: true)) == "Firmware 2.0.0 (not yet tested) · Headset audio channel: yes",
            "firmware line for a Pro Controller");
        Check(FirmwareLine(new SideStatus("L", true, null, false, 0, Firmware: fw2, FirmwareTested: true), new SideStatus("R", true, null, false, 0)) == "L firmware 2.0.0 (tested) · R firmware unknown",
            "firmware line for a Joy-Con pair");

        // Battery level: bit 0 external power, bit 1 charging, bits 2-5 level 0-9.
        Check(PowerInfo.Parse([7, (6 << 2) | 0b10, 0]) is { Level: 6, ExternalPower: false, Charging: true }, "power byte gives level and charging");
        Check(PowerInfo.Parse([7, (9 << 2) | 0b01]) is { Level: 9, ExternalPower: true, Charging: false }, "power byte gives level 9 and external power");
        Check(PowerInfo.Parse([1, 0]) is { Level: 0 }, "a real report at level 0 counts");
        Check(PowerInfo.Parse(new byte[64]) == null && PowerInfo.Parse([7]) == null, "empty or too-short reports give no level");
        Check(PowerInfo.Parse([7, 10 << 2]) == null, "a level above 9 is rejected");

        // Audio probe: the documented firmware reply and report layout, and honest verdicts.
        var fw = FirmwareInfo.Parse(Convert.FromHexString("01000E010C000000FFFFFFFF"));
        Check(fw is { Type: 1, Dsp: null, ListedWithHeadsetAudio: false } && fw.Firmware == new Version(1, 0, 14) && fw.Bluetooth == new Version(12, 0, 0),
            "firmware reply parses as the notes' example");
        var audioReport = new byte[0x70];
        audioReport[0] = 7; audioReport[1] = (6 << 2) | 1; audioReport[0xD] = 0x07; audioReport[0xE] = AudioProbe.DocumentedLength;
        audioReport[0xF] = 0xAB; audioReport[0xF + AudioProbe.DocumentedLength - 1] = 0xCD; audioReport[0xF + AudioProbe.DocumentedLength] = 0xEE;
        Check(AudioProbe.Parse(audioReport) is { Counter: 7, State: 0x07, BatteryLevel: 6, ExternalPower: true, Charging: false, Data: { Length: AudioProbe.DocumentedLength } data }
            && data[0] == 0xAB && data[^1] == 0xCD, "headset report parses per the documented layout");
        Check(AudioProbe.Parse(new byte[5]) == null, "a too-short headset report is rejected");

        byte[] AudioFrame(byte counter, byte state, byte fill)
        {
            var r = new byte[0x70];
            r[0] = counter; r[0xD] = state; r[0xE] = AudioProbe.DocumentedLength;
            Array.Fill(r, fill, 0xF, AudioProbe.DocumentedLength);
            return r;
        }
        var skipped = AudioProbe.Analyze([AudioFrame(255, 7, 1), AudioFrame(0, 7, 2), AudioFrame(3, 7, 2)]);
        Check(skipped is { Reports: 3, LostCounters: 2, Distinct: 2, NonZeroPercent: 100, ChangedPercent: 50 }, "audio stats count skips across counter wrap, and changes");
        AudioProbe.PhaseStats Stats(params byte[][] r) => AudioProbe.Analyze(r);
        var silent = Stats(AudioFrame(0, 7, 0), AudioFrame(1, 7, 0));
        var constant = Stats(AudioFrame(0, 7, 5), AudioFrame(1, 7, 5));
        var moving = Stats(AudioFrame(0, 7, 1), AudioFrame(1, 7, 2));
        Check(AudioProbe.Verdict(Stats(), Stats()).StartsWith("No reports"), "probe verdict: nothing arrived");
        Check(AudioProbe.Verdict(Stats(AudioFrame(0, 0, 0)), Stats(AudioFrame(1, 0, 0))).Contains("no headset"), "probe verdict: no headset");
        Check(AudioProbe.Verdict(silent, silent).Contains("every audio byte is zero"), "probe verdict: mic silent");
        Check(AudioProbe.Verdict(constant, moving).StartsWith("Promising"), "probe verdict: reacts to sound");
        Check(AudioProbe.Verdict(moving, moving).Contains("can't tell"), "probe verdict: no difference claims nothing");

        if (profileOutDir != null)
        {
            Directory.CreateDirectory(profileOutDir);
            File.WriteAllText(Path.Combine(profileOutDir, "switch2-pro-bridge-selftest.json"), json.ToJsonString());
            using var ctx = new HMContext();
            ctx.LoadProfilesFromDirectory(profileOutDir);
            var profile = ctx.GetProfile("switch2-pro-bridge-selftest");
            Check(profile != null && profile.BuildsItsOwnReports, "HIDMaestro loads the bridge profile as a self-reporting device");
        }

        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    static byte[] Fill(int length, byte value)
    {
        var a = new byte[length];
        Array.Fill(a, value);
        return a;
    }

    /// <summary>Builds the wired report 0x05 from the bridge's state block the way the
    /// persona's program does: the bits table, pack12 for the sticks, and the IMU copy.</summary>
    internal static byte[] Replay(byte[] ble, (int State, int Report)[] bits)
    {
        Span<byte> state = stackalloc byte[VirtualPad.StateBlockSize];
        VirtualPad.ToStateBlock(ble, state);
        var report = new byte[64];
        report[0] = 0x05;
        foreach (var (s, r) in bits)
            if ((state[s / 8] >> (s % 8) & 1) != 0) report[r / 8] |= (byte)(1 << (r % 8));
        Pack12(state, 4, report, 11);
        Pack12(state, 8, report, 14);
        state[12..24].CopyTo(report.AsSpan(49));
        state[24..28].CopyTo(report.AsSpan(32));
        return report;
    }

    static void Pack12(ReadOnlySpan<byte> state, int at, byte[] report, int to)
    {
        int x = BitConverter.ToInt16(state[at..]) & 0xFFF, y = BitConverter.ToInt16(state[(at + 2)..]) & 0xFFF;
        int v = x | y << 12;
        report[to] = (byte)v;
        report[to + 1] = (byte)(v >> 8);
        report[to + 2] = (byte)(v >> 16);
    }
}
