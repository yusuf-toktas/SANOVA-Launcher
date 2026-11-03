using System.Diagnostics;

namespace Sanova.Core.Downloads;

/// <summary>
/// Token-bucket limiter shared by all concurrent downloads, so the configured speed limit
/// applies to the launcher as a whole. A limit of 0 disables throttling.
/// </summary>
public sealed class BandwidthThrottle
{
    private readonly Lock _gate = new();
    private long _bytesPerSecond;
    private double _tokens;
    private long _lastTimestamp = Stopwatch.GetTimestamp();

    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set => Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, value));
    }

    public ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        var limit = BytesPerSecond;
        if (limit <= 0 || bytes <= 0) return ValueTask.CompletedTask;

        TimeSpan delay;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            _lastTimestamp = now;
            // Allow at most one second of burst.
            _tokens = Math.Min(limit, _tokens + elapsed * limit);
            _tokens -= bytes;
            if (_tokens >= 0) return ValueTask.CompletedTask;
            delay = TimeSpan.FromSeconds(-_tokens / limit);
        }
        return new ValueTask(Task.Delay(delay, cancellationToken));
    }
}
