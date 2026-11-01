namespace Switch2Bridge;

/// <summary>
/// The Switch 2 rumble packet: two 16-byte motor blocks (left, right), each a header byte
/// (0x50 | sequence) then three 5-byte vibration entries. An entry is 40 bits, little-endian:
/// low-band frequency (9 bits), tone flag, low-band amplitude (10 bits at bit 10), high-band
/// frequency (9 bits at 20), tone flag, high-band amplitude (10 bits at 30).
/// (Layout as used by switch2controllerpc.)
/// </summary>
static class Rumble
{
    const int BlockSize = 16, EntrySize = 5, Entries = 3;
    const ulong AmpMask = 0x3FF;

    /// <summary>Scales every amplitude in a 32-byte left+right packet, in place.</summary>
    public static void Scale(Span<byte> packet, double strength)
    {
        if (strength >= 0.999) return;
        for (int block = 0; block + BlockSize <= packet.Length && block < 2 * BlockSize; block += BlockSize)
            for (int e = 0; e < Entries; e++)
            {
                var entry = packet.Slice(block + 1 + e * EntrySize, EntrySize);
                ulong v = Read40(entry);
                ulong lf = (v >> 10) & AmpMask, hf = (v >> 30) & AmpMask;
                lf = (ulong)Math.Round(lf * strength);
                hf = (ulong)Math.Round(hf * strength);
                v = (v & ~(AmpMask << 10) & ~(AmpMask << 30)) | (lf << 10) | (hf << 30);
                Write40(entry, v);
            }
    }

    /// <summary>One vibration entry: frequencies are the switch2controllerpc defaults when 0.</summary>
    public static ulong Entry(int lfFreq, int lfAmp, int hfFreq, int hfAmp) =>
        (ulong)(uint)(lfFreq & 0x1FF)
        | (ulong)(uint)(lfAmp & 0x3FF) << 10
        | (ulong)(uint)(hfFreq & 0x1FF) << 20
        | (ulong)(uint)(hfAmp & 0x3FF) << 30;

    /// <summary>A 32-byte packet playing <paramref name="entry"/> on both motors.</summary>
    public static byte[] Packet(ulong entry, int sequence)
    {
        var packet = new byte[2 * BlockSize];
        ulong silent = Entry(0x0E1, 0, 0x1E1, 0);
        for (int block = 0; block < 2 * BlockSize; block += BlockSize)
        {
            packet[block] = (byte)(0x50 | (sequence & 0x0F));
            Write40(packet.AsSpan(block + 1), entry);
            Write40(packet.AsSpan(block + 1 + EntrySize), silent);
            Write40(packet.AsSpan(block + 1 + 2 * EntrySize), silent);
        }
        return packet;
    }

    /// <summary>The thump-and-click switch2controllerpc plays on connect.</summary>
    public static readonly ulong Thump = Entry(0x060, 0x350, 0x0C0, 0x250);
    public static readonly ulong Click = Entry(0x0E1, 0x030, 0x1E2, 0x300);
    public static readonly ulong Stop = Entry(0x0E1, 0, 0x1E1, 0);

    static ulong Read40(ReadOnlySpan<byte> b) =>
        b[0] | (ulong)b[1] << 8 | (ulong)b[2] << 16 | (ulong)b[3] << 24 | (ulong)b[4] << 32;

    static void Write40(Span<byte> b, ulong v)
    {
        for (int i = 0; i < EntrySize; i++) b[i] = (byte)(v >> (8 * i));
    }
}
