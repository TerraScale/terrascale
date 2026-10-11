using System.Diagnostics.Metrics;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

/// <summary>
/// The member's hybrid logical clock (docs/design/anna-core.md, Identities and dots): it stamps every local write, after
/// every stamp it saw. A replicated stamp ahead of the physical clock by more than <see cref="MaxSkew"/> is accepted, but
/// moves the clock at most that far ahead and is counted: a fast peer clock never stalls a stream, and only wins the
/// last-writer-wins choice between concurrent writes.
/// </summary>
internal sealed class MemberClock
{
    /// <summary>How far a replicated stamp may move the clock ahead of the physical clock; a code constant, not a setting.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly long _maxSkewMilliseconds = (long)MaxSkew.TotalMilliseconds;
    private readonly Counter<long> _capped;
    private Hlc _last;

    public MemberClock(TimeProvider time, Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _time = time;
        _capped = meter.CreateCounter<long>(
            "terrabase.clock.skew_capped", "{stamp}", "Replicated stamps further ahead of this member's physical clock than the 1 s skew bound.");
    }

    /// <summary>The stamp of a local write, after every stamp issued or observed.</summary>
    public Hlc Tick()
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_lock)
        {
            return _last = _last.Tick(now);
        }
    }

    /// <summary>Moves the clock past <paramref name="stamp"/>, a stamp this member read or received.</summary>
    public void Observe(Hlc stamp)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var limit = now + _maxSkewMilliseconds;
        if (stamp.Milliseconds > limit)
        {
            _capped.Add(1);
            stamp = new Hlc((ulong)limit << 16);
        }

        lock (_lock)
        {
            _last = _last.Observe(stamp, now);
        }
    }
}
