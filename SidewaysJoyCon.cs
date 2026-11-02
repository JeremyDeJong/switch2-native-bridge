namespace Switch2Bridge;

/// <summary>
/// Turns a lone Joy-Con held sideways into a Pro-shaped report: stick on the left, face
/// buttons in the positions they now sit in, SL/SR as L/R. The left Joy-Con is turned a
/// quarter anticlockwise and the right one clockwise. Button positions, stick rotation and
/// motion axes follow switch2controllerpc's horizontal mode.
/// </summary>
static class SidewaysJoyCon
{
    const uint Y = 0x01, X = 0x02, B = 0x04, A = 0x08, SrRight = 0x10, SlRight = 0x20, R = 0x40, Zr = 0x80;
    const uint Minus = 0x100, Plus = 0x200, RStick = 0x400, LStick = 0x800, Home = 0x1000, Capture = 0x2000, C = 0x4000;
    const uint Down = 0x10000, Up = 0x20000, Right = 0x40000, Left = 0x80000, SrLeft = 0x100000, SlLeft = 0x200000, L = 0x400000, Zl = 0x800000;

    static readonly (uint From, uint To)[] LeftMap =
    [
        (Up, Y), (Down, A), (Left, B), (Right, X),
        (SlLeft, L), (SrLeft, R), (L, Zl), (Zl, Zr),
        (Minus, Plus), (Capture, Minus), (LStick, LStick),
    ];

    static readonly (uint From, uint To)[] RightMap =
    [
        (X, A), (A, B), (B, Y), (Y, X),
        (SlRight, L), (SrRight, R), (R, Zl), (Zr, Zr),
        (Plus, Plus), (C, Minus), (Home, Home), (RStick, LStick),
    ];

    internal static uint MapButtons(ControllerKind kind, uint buttons)
    {
        uint mapped = 0;
        foreach (var (from, to) in kind == ControllerKind.JoyConLeft ? LeftMap : RightMap)
            if ((buttons & from) != 0) mapped |= to;
        return mapped;
    }

    static short Negate(short v) => v == short.MinValue ? short.MaxValue : (short)-v;

    /// <summary>The Pro-shaped report for this Joy-Con's report: buttons, stick and motion
    /// turned, right stick centred, battery unchanged. The report must be at least 60 bytes.</summary>
    internal static byte[] Apply(ControllerKind kind, ReadOnlySpan<byte> report)
    {
        bool left = kind == ControllerKind.JoyConLeft;
        var result = VirtualPad.NeutralReport();
        BitConverter.TryWriteBytes(result.AsSpan(4), MapButtons(kind, BitConverter.ToUInt32(report[4..])));

        var packed = report.Slice(left ? 10 : 13, 3);
        int value = packed[0] | packed[1] << 8 | packed[2] << 16;
        int x = value & 0xFFF, y = value >> 12;
        int turnedX = left ? 4095 - y : y;
        int turnedY = left ? x : 4095 - x;
        int stick = turnedX | turnedY << 12;
        result[10] = (byte)stick; result[11] = (byte)(stick >> 8); result[12] = (byte)(stick >> 16);

        report.Slice(31, 4).CopyTo(result.AsSpan(31));

        short ax = BitConverter.ToInt16(report[48..]), ay = BitConverter.ToInt16(report[50..]), az = BitConverter.ToInt16(report[52..]);
        short gx = BitConverter.ToInt16(report[54..]), gy = BitConverter.ToInt16(report[56..]), gz = BitConverter.ToInt16(report[58..]);
        short[] motion = left
            ? [Negate(ay), ax, az, Negate(gy), Negate(gx), gz]
            : [ay, Negate(ax), az, gy, gx, gz];
        for (int i = 0; i < motion.Length; i++) BitConverter.TryWriteBytes(result.AsSpan(48 + i * 2), motion[i]);
        return result;
    }
}
