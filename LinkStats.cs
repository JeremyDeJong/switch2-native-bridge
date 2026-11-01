using System.Diagnostics;

namespace Switch2Bridge;

/// <summary>
/// How evenly a controller's Bluetooth reports arrive: the average and worst gap between
/// reports, and stalls (gaps of 50 ms or more, long enough to feel as a hitch). This is
/// the Bluetooth side only; it can't see the delay from a press to the screen.
/// </summary>
sealed class LinkStats
{
    public const double StallMs = 50;
    const double LogStallMs = 250;
    static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    readonly string _name;
    long _last;
    // The current one-second window, and the last full one.
    double _windowSum, _windowMax;
    int _windowCount;
    long _windowStart;
    (double Avg, double Worst) _lastWindow;
    readonly Queue<double> _worstPerSecond = new(); // last 10 windows' worst gap
    readonly Queue<long> _stalls = new();            // stall times in the last minute
    long _lastStallLog;

    // The whole connection, for the summary logged when it ends.
    long _sessionReports;
    double _sessionSum, _sessionWorst;
    int _sessionStalls;

    public LinkStats(string name) => _name = name;

    /// <summary>Call on every report, with Stopwatch.GetTimestamp().</summary>
    public void Report(long now)
    {
        if (_last == 0) { _last = _windowStart = now; return; }
        double gap = (now - _last) * TicksToMs;
        _last = now;

        _windowSum += gap; _windowCount++;
        if (gap > _windowMax) _windowMax = gap;
        _sessionReports++; _sessionSum += gap;
        if (gap > _sessionWorst) _sessionWorst = gap;

        if (gap >= StallMs)
        {
            _stalls.Enqueue(now);
            _sessionStalls++;
            if (gap >= LogStallMs && (now - _lastStallLog) * TicksToMs > 10_000)
            {
                _lastStallLog = now;
                Log.Warn($"{_name}: Bluetooth stalled for {gap:0} ms (interference, or the adapter is busy).");
            }
        }

        if ((now - _windowStart) * TicksToMs >= 1000)
        {
            _lastWindow = (_windowCount > 0 ? _windowSum / _windowCount : 0, _windowMax);
            _worstPerSecond.Enqueue(_windowMax);
            while (_worstPerSecond.Count > 10) _worstPerSecond.Dequeue();
            _windowSum = _windowMax = 0; _windowCount = 0; _windowStart = now;
        }
    }

    /// <summary>Average gap over the last second, worst gap over the last 10 seconds,
    /// and stalls in the last minute.</summary>
    public (double AvgMs, double WorstMs, int StallsPerMinute) Read(long now)
    {
        while (_stalls.Count > 0 && (now - _stalls.Peek()) * TicksToMs > 60_000) _stalls.Dequeue();
        double worst = _worstPerSecond.Count > 0 ? _worstPerSecond.Max() : 0;
        return (_lastWindow.Avg, worst, _stalls.Count);
    }

    public string Summary() => _sessionReports == 0 ? $"{_name}: no reports."
        : $"{_name}: {_sessionReports} reports, average gap {_sessionSum / _sessionReports:0.0} ms, " +
          $"worst {_sessionWorst:0} ms, {_sessionStalls} stall(s) of {StallMs:0} ms or more.";
}
