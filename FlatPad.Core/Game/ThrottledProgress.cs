using System.Diagnostics;

namespace FlatPad.Core.Game;

/// <summary>
/// Rate-limits progress reports so a fast inner loop cannot swamp its consumer.
/// </summary>
/// <remarks>
/// ⚠️ This exists because of a bug that only appears at scale. Reporting once per file is fine for
/// a 650-file package and fatal for a 119,443-file one: <see cref="Progress{T}"/> posts every report
/// to the UI thread, so the message pump backs up, the window stops responding, and **Cancel stops
/// working** — the one control that matters during a long operation. A 650-file test showed none of
/// it.
///
/// Throttling lives here rather than in each consumer so the callback rate is bounded for all of
/// them. <see cref="ReportFinal"/> always gets through, so a run never ends showing 99 %.
/// </remarks>
public sealed class ThrottledProgress<T>(
    IProgress<T>? inner,
    TimeSpan interval,
    Func<TimeSpan>? clock = null)
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private TimeSpan? _last;

    private TimeSpan Now => clock?.Invoke() ?? _stopwatch.Elapsed;

    /// <summary>Report unless one went out too recently. The first call always goes out.</summary>
    public void Report(T value)
    {
        if (inner is null)
            return;
        TimeSpan now = Now;
        if (_last is not null && now - _last.Value < interval)
            return;
        _last = now;
        inner.Report(value);
    }

    /// <summary>Report unconditionally — for the last update, which must never be dropped.</summary>
    public void ReportFinal(T value)
    {
        if (inner is null)
            return;
        _last = Now;
        inner.Report(value);
    }
}
