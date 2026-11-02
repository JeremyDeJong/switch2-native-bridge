using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading.Channels;

namespace Switch2Bridge;

/// <summary>
/// Turns a controller's raw Bluetooth reports into readable events: buttons going down and
/// up, sticks moving, and gaps between reports. Times are when reports reached the bridge,
/// not when a button was physically pressed.
/// </summary>
sealed class InputDecoder
{
    const double GapMs = 50;
    const int StickStep = 200;      // about 5% of the 12-bit range
    const double StickEveryMs = 100;

    internal static readonly (uint Mask, string Name)[] Buttons =
    [
        (0x00000001, "Y"), (0x00000002, "X"), (0x00000004, "B"), (0x00000008, "A"),
        (0x00000010, "SR (right)"), (0x00000020, "SL (right)"), (0x00000040, "R"), (0x00000080, "ZR"),
        (0x00000100, "Minus"), (0x00000200, "Plus"), (0x00000400, "R stick click"), (0x00000800, "L stick click"),
        (0x00001000, "Home"), (0x00002000, "Capture"), (0x00004000, "C"),
        (0x00010000, "D-pad down"), (0x00020000, "D-pad up"), (0x00040000, "D-pad right"), (0x00080000, "D-pad left"),
        (0x00100000, "SR (left)"), (0x00200000, "SL (left)"), (0x00400000, "L"), (0x00800000, "ZL"),
        (0x01000000, "GR"), (0x02000000, "GL"), (0x10000000, "Headset"),
    ];

    double _lastMs = -1;
    uint _buttons;
    readonly double[] _downAt = new double[32];
    readonly int[] _loggedStick = new int[4];
    readonly double[] _stickLoggedAt = [double.MinValue, double.MinValue];
    bool _sticksKnown;

    /// <summary>The events one report causes; a short report causes none.</summary>
    public List<string> Feed(double ms, ReadOnlySpan<byte> report)
    {
        var events = new List<string>();
        if (report.Length < 16) return events;

        if (_lastMs >= 0 && ms - _lastMs >= GapMs) events.Add($"Bluetooth gap {ms - _lastMs:0} ms");
        _lastMs = ms;

        uint buttons = BitConverter.ToUInt32(report[4..]);
        uint changed = buttons ^ _buttons;
        while (changed != 0)
        {
            int bit = BitOperations.TrailingZeroCount(changed);
            changed &= changed - 1;
            uint mask = 1u << bit;
            string name = "unknown bit " + bit;
            foreach (var (m, n) in Buttons) if (m == mask) name = n;
            if ((buttons & mask) != 0)
            {
                _downAt[bit] = ms;
                events.Add(name + " down");
            }
            else events.Add($"{name} up (held {ms - _downAt[bit]:0} ms)");
        }
        _buttons = buttons;

        Stick("L stick", 0, ms, report.Slice(10, 3), events);
        Stick("R stick", 1, ms, report.Slice(13, 3), events);
        _sticksKnown = true;
        return events;
    }

    void Stick(string name, int index, double ms, ReadOnlySpan<byte> packed, List<string> events)
    {
        int value = packed[0] | packed[1] << 8 | packed[2] << 16;
        int x = value & 0xFFF, y = value >> 12;
        ref int lastX = ref _loggedStick[index * 2];
        ref int lastY = ref _loggedStick[index * 2 + 1];
        if (!_sticksKnown) { lastX = x; lastY = y; return; }
        if (Math.Abs(x - lastX) < StickStep && Math.Abs(y - lastY) < StickStep) return;
        if (ms - _stickLoggedAt[index] < StickEveryMs) return;
        lastX = x; lastY = y;
        _stickLoggedAt[index] = ms;
        // Uncalibrated: 0 to 4095, about 2048 at rest.
        events.Add(string.Create(CultureInfo.InvariantCulture, $"{name} raw x={x} y={y}"));
    }
}

/// <summary>
/// A recording of raw controller reports (.s2rec). It holds report bytes and timing but
/// no Bluetooth address. Layout: "S2RC", u16 version, i64 start time (UTC ticks), then
/// records of u32 milliseconds, u8 controller kind, u16 length, report bytes.
/// </summary>
static class InputRecording
{
    static readonly byte[] Magic = "S2RC"u8.ToArray();
    const ushort FormatVersion = 1;

    internal readonly record struct Entry(uint Ms, ControllerKind Kind, byte[] Report);

    internal static void WriteHeader(BinaryWriter writer, DateTime startUtc)
    {
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(startUtc.Ticks);
    }

    internal static void WriteEntry(BinaryWriter writer, uint ms, ControllerKind kind, byte[] report)
    {
        writer.Write(ms);
        writer.Write((byte)kind);
        writer.Write((ushort)report.Length);
        writer.Write(report);
    }

    /// <summary>Reads a recording. A last record cut short, as a crash leaves it, is dropped
    /// and reported through <paramref name="truncated"/>.</summary>
    internal static List<Entry> Read(Stream stream, out bool truncated)
    {
        truncated = false;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a controller recording.");
        var version = reader.ReadUInt16();
        if (version != FormatVersion) throw new InvalidDataException($"Unsupported recording version {version}.");
        reader.ReadInt64();

        var entries = new List<Entry>();
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 7) { truncated = true; break; }
            uint ms = reader.ReadUInt32();
            byte kind = reader.ReadByte();
            ushort length = reader.ReadUInt16();
            if (kind > (byte)ControllerKind.JoyConRight) throw new InvalidDataException($"Unknown controller kind {kind}.");
            if (stream.Length - stream.Position < length) { truncated = true; break; }
            entries.Add(new Entry(ms, (ControllerKind)kind, reader.ReadBytes(length)));
        }
        return entries;
    }
}

/// <summary>
/// Writes the continuous input log and any recording on a background task, so disk writes
/// never delay input reaching Steam. If the disk can't keep up, the oldest queued reports
/// are dropped from the log; the game still gets them.
/// </summary>
static class InputCapture
{
    public static readonly string LogDirectory = Path.Combine(Log.Directory, "input-log");
    public static readonly string RecordingDirectory = Path.Combine(Log.Directory, "recordings");
    const int KeepLogDays = 7;
    const long MaxRecordingBytes = 100L * 1024 * 1024;

    readonly record struct Item(int Player, ControllerKind Kind, long Stamp, byte[]? Report, string? Note);

    sealed class ActiveRecording(int player, string path, BinaryWriter writer, long startStamp)
    {
        public int Player => player;
        public string Path => path;
        public BinaryWriter Writer => writer;
        public long StartStamp => startStamp;
    }

    static readonly Channel<Item> Queue = Channel.CreateBounded<Item>(
        new BoundedChannelOptions(20_000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    static readonly object Gate = new();
    static readonly Dictionary<(int, ControllerKind), InputDecoder> Decoders = [];
    static readonly long BaseStamp = Stopwatch.GetTimestamp();
    static readonly DateTime BaseTime = DateTime.Now;
    static Task? _loop;
    static Timer? _flush;
    static StreamWriter? _log;
    static DateTime _logDay;
    static ActiveRecording? _recording;
    static volatile int _recordingPlayer;

    /// <summary>The player being recorded, or 0. Readable on every report without taking
    /// the lock that disk writes hold, so it can't delay input.</summary>
    public static int RecordingPlayer => _recordingPlayer;

    public static void Start()
    {
        _loop = Task.Run(Loop);
        _flush = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _ = Task.Run(DeleteOldLogs);
    }

    public static void Post(int player, ControllerKind kind, long stamp, byte[] report) =>
        Queue.Writer.TryWrite(new Item(player, kind, stamp, report, null));

    public static void Note(int player, ControllerKind kind, string note) =>
        Queue.Writer.TryWrite(new Item(player, kind, Stopwatch.GetTimestamp(), null, note));

    public static bool IsRecording(int player)
    {
        lock (Gate) return _recording?.Player == player;
    }

    /// <summary>Starts recording one player, replacing any recording in progress.</summary>
    public static string StartRecording(int player)
    {
        Directory.CreateDirectory(RecordingDirectory);
        var path = Path.Combine(RecordingDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-player{player}.s2rec");
        var writer = new BinaryWriter(File.Create(path));
        InputRecording.WriteHeader(writer, DateTime.UtcNow);
        lock (Gate)
        {
            CloseRecording();
            _recording = new ActiveRecording(player, path, writer, Stopwatch.GetTimestamp());
            _recordingPlayer = player;
        }
        Log.Info($"Recording player {player} to {path}.");
        return path;
    }

    public static void StopRecording()
    {
        lock (Gate) CloseRecording();
    }

    static void CloseRecording()
    {
        if (_recording is not { } r) return;
        _recording = null;
        _recordingPlayer = 0;
        try { r.Writer.Dispose(); Log.Info($"Recording saved: {r.Path}."); }
        catch (IOException e) { Log.Warn($"Recording may be incomplete: {e.Message}"); }
    }

    public static void Shutdown()
    {
        Queue.Writer.TryComplete();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _flush?.Dispose();
        lock (Gate)
        {
            CloseRecording();
            try { _log?.Dispose(); } catch (IOException) { }
            _log = null;
        }
    }

    static async Task Loop()
    {
        await foreach (var item in Queue.Reader.ReadAllAsync())
        {
            try
            {
                lock (Gate) Handle(item);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Input capture write failed: {e.Message}");
            }
        }
    }

    static void Handle(Item item)
    {
        double sinceBase = (item.Stamp - BaseStamp) * 1000.0 / Stopwatch.Frequency;
        var time = BaseTime.AddMilliseconds(sinceBase);
        if (item.Report is not { } report)
        {
            Decoders.Remove((item.Player, item.Kind));
            WriteLine(time, item.Player, item.Kind, item.Note ?? "");
            return;
        }

        if (_recording is { } r && r.Player == item.Player)
        {
            double ms = (item.Stamp - r.StartStamp) * 1000.0 / Stopwatch.Frequency;
            if (ms >= 0) InputRecording.WriteEntry(r.Writer, (uint)ms, item.Kind, report);
            if (r.Writer.BaseStream.Length > MaxRecordingBytes)
            {
                Log.Warn("Recording reached its 100 MB limit and stopped.");
                CloseRecording();
            }
        }

        if (!Settings.Current.LogInputs) return;
        if (!Decoders.TryGetValue((item.Player, item.Kind), out var decoder))
            Decoders[(item.Player, item.Kind)] = decoder = new InputDecoder();
        foreach (var line in decoder.Feed(sinceBase, report)) WriteLine(time, item.Player, item.Kind, line);
    }

    static void WriteLine(DateTime time, int player, ControllerKind kind, string text)
    {
        if (!Settings.Current.LogInputs) return;
        if (_log == null || _logDay != time.Date)
        {
            _log?.Dispose();
            Directory.CreateDirectory(LogDirectory);
            _log = new StreamWriter(new FileStream(Path.Combine(LogDirectory, $"inputs-{time:yyyy-MM-dd}.log"),
                FileMode.Append, FileAccess.Write, FileShare.Read));
            _logDay = time.Date;
        }
        var who = kind switch { ControllerKind.JoyConLeft => "L", ControllerKind.JoyConRight => "R", _ => "Pro" };
        _log.WriteLine($"{time:HH:mm:ss.fff} P{player} {who,-3} {text}");
    }

    static void Flush()
    {
        lock (Gate)
        {
            try
            {
                _log?.Flush();
                _recording?.Writer.Flush();
            }
            catch (IOException) { }
        }
    }

    static void DeleteOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            foreach (var file in Directory.GetFiles(LogDirectory, "inputs-*.log"))
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-KeepLogDays)) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not tidy old input logs: {e.Message}");
        }
    }
}
