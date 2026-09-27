namespace Rtfc.Daemon;

/// <summary>
/// Who is keeping the daemon alive (spec §3.1). Every <c>rtfc mcp</c> holds one open IPC
/// request as a lease. When none remain for the grace period the daemon exits, which is
/// what makes "home" mean "Claude Code is open on this machine".
/// </summary>
public sealed class Leases(TimeProvider clock)
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private int _count;
    private DateTimeOffset _lastReleased = clock.GetUtcNow();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public IDisposable Acquire()
    {
        lock (_lock)
        {
            _count++;
        }

        return new Release(this);
    }

    /// <summary>True once no lease has been held for the grace period, measured from start-up or the last release.</summary>
    public bool IsIdle()
    {
        lock (_lock)
        {
            return _count == 0 && clock.GetUtcNow() - _lastReleased >= Grace;
        }
    }

    private void ReleaseOne()
    {
        lock (_lock)
        {
            _count--;
            _lastReleased = clock.GetUtcNow();
        }
    }

    private sealed class Release(Leases leases) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                leases.ReleaseOne();
            }
        }
    }
}
