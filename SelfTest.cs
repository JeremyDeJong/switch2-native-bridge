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
    static byte[] Replay(byte[] ble, (int State, int Report)[] bits)
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
