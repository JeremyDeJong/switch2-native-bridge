using System.Globalization;

namespace Switch2Bridge;

/// <summary>
/// Logs each controller's battery voltage once a minute to
/// %LOCALAPPDATA%\Switch2Bridge\battery\&lt;address&gt;.csv, and learns a percentage from it.
/// Nintendo's voltage-to-charge curve isn't public, so the range comes from what this
/// controller has actually shown: its highest and lowest voltage while not charging.
/// Until it has seen enough of a range, it reports no percentage at all.
/// </summary>
static class BatteryLog
{
    public static readonly string Directory = Path.Combine(Log.Directory, "battery");

    /// <summary>Below this a controller counts as low, until its own range is learned
    /// (switch2controllerpc's measured "low" threshold).</summary>
    public const int LowMv = 3125;
    public const int LowPercent = 15;
    const int MinLearnedRangeMv = 200;
    const int MinLearnedSamples = 120; // two hours of play

    static readonly object Gate = new();
    static readonly Dictionary<ulong, Range> Ranges = [];

    sealed class Range
    {
        public int Min = int.MaxValue, Max = int.MinValue, Samples;
        public void Add(int mv)
        {
            Min = Math.Min(Min, mv);
            Max = Math.Max(Max, mv);
            Samples++;
        }
    }

    static string FileFor(ulong address) => Path.Combine(Directory, $"{address:X12}.csv");

    /// <summary>Appends a sample; charging samples are logged but don't shape the range.</summary>
    public static void Record(ulong address, int millivolts, bool charging)
    {
        lock (Gate)
        {
            var range = Load(address);
            if (!charging && millivolts > 0) range.Add(millivolts);
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var file = FileFor(address);
                if (!File.Exists(file)) File.WriteAllText(file, "time,millivolts,charging" + Environment.NewLine);
                File.AppendAllText(file, string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss},{millivolts},{(charging ? 1 : 0)}{Environment.NewLine}"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A percentage from this controller's learned range, or null while still learning.</summary>
    public static int? Percent(ulong address, int millivolts)
    {
        lock (Gate)
        {
            var range = Load(address);
            return PercentFrom(range.Min, range.Max, range.Samples, millivolts);
        }
    }

    internal static int? PercentFrom(int min, int max, int samples, int millivolts)
    {
        if (samples < MinLearnedSamples || max - min < MinLearnedRangeMv) return null;
        return (int)Math.Round(Math.Clamp((millivolts - min) * 100.0 / (max - min), 0, 100));
    }

    public static bool IsLow(ulong address, int millivolts) =>
        Percent(address, millivolts) is int pct ? pct <= LowPercent : millivolts <= LowMv;

    static Range Load(ulong address)
    {
        if (Ranges.TryGetValue(address, out var range)) return range;
        range = new Range();
        try
        {
            var file = FileFor(address);
            if (File.Exists(file))
                foreach (var line in File.ReadLines(file).Skip(1))
                {
                    var parts = line.Split(',');
                    if (parts.Length == 3 && parts[2] == "0" && int.TryParse(parts[1], out int mv) && mv > 0) range.Add(mv);
                }
        }
        catch (IOException) { }
        Ranges[address] = range;
        return range;
    }
}
