namespace LocalBack.Core.Watching;

/// <summary>
/// Collects paths and hands them over once no new event has arrived for any of them for <see cref="Quiet"/>.
/// One save that fires five events becomes one version. Uses a one-shot timer, not polling.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _due = new(Util.PathUtil.Comparer);
    private readonly Action<IReadOnlyList<string>> _flush;
    private readonly Timer _timer;
    private readonly Func<DateTime> _clock;
    private bool _disposed;

    public TimeSpan Quiet { get; set; }

    /// <summary>Upper bound so a file that is written continuously is still backed up now and then.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, DateTime> _firstSeen = new(Util.PathUtil.Comparer);

    public Debouncer(TimeSpan quiet, Action<IReadOnlyList<string>> flush, Func<DateTime>? clock = null)
    {
        Quiet = quiet;
        _flush = flush;
        _clock = clock ?? (() => DateTime.UtcNow);
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int Count
    {
        get { lock (_gate) return _due.Count; }
    }

    public void Add(string path)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = _clock();
            if (!_firstSeen.ContainsKey(path)) _firstSeen[path] = now;
            var due = now + Quiet;
            var cap = _firstSeen[path] + MaxDelay;
            _due[path] = due < cap ? due : cap;
            Arm();
        }
    }

    private void Arm()
    {
        if (_due.Count == 0) return;
        var next = _due.Values.Min();
        var wait = next - _clock();
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        _timer.Change(wait + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Hands over everything that is due now. Called by the timer; public for tests.</summary>
    public void Tick()
    {
        List<string> ready;
        lock (_gate)
        {
            if (_disposed) return;
            var now = _clock();
            ready = _due.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
            foreach (var p in ready)
            {
                _due.Remove(p);
                _firstSeen.Remove(p);
            }
            Arm();
        }
        if (ready.Count > 0) _flush(ready);
    }

    /// <summary>Hands over everything immediately (e.g. "Back up now").</summary>
    public IReadOnlyList<string> Drain()
    {
        lock (_gate)
        {
            var all = _due.Keys.ToList();
            _due.Clear();
            _firstSeen.Clear();
            return all;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
