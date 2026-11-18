namespace Dsm.Engine;

/// <summary>
/// Token-bucket speed limiter shared by any number of connections. A rate of 0 means unlimited.
/// Callers may overdraw the bucket; the debt is repaid by waiting, so together they never
/// exceed the rate.
/// </summary>
public sealed class Throttle
{
    private readonly object _gate = new();
    private double _rate;
    private double _tokens;
    private long _last = Environment.TickCount64;

    public Throttle(double bytesPerSecond = 0) => Rate = bytesPerSecond;

    public double Rate
    {
        get { lock (_gate) return _rate; }
        set
        {
            lock (_gate)
            {
                _rate = Math.Max(0, value);
                _tokens = 0; // drop old burst/debt so a new limit applies immediately
                _last = Environment.TickCount64;
            }
        }
    }

    public Task TakeAsync(int bytes, CancellationToken cancellationToken)
    {
        double waitMs;
        lock (_gate)
        {
            if (_rate <= 0) return Task.CompletedTask;
            var now = Environment.TickCount64;
            var burst = Math.Max(_rate / 4, 16 * 1024);
            _tokens = Math.Min(burst, _tokens + (now - _last) / 1000.0 * _rate);
            _last = now;
            _tokens -= bytes;
            if (_tokens >= 0) return Task.CompletedTask;
            waitMs = -_tokens / _rate * 1000;
        }
        return Task.Delay(TimeSpan.FromMilliseconds(waitMs), cancellationToken);
    }
}
