using System.Diagnostics.Metrics;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

/// <summary>
/// The member's hybrid logical clock (docs/design/anna-core.md, Identities and dots): it stamps every local write, after
/// every local mint and the stored state it writes. Receiving a replicated stamp ahead of the physical clock by more
/// than <see cref="MaxSkew"/> advances the clock only that far and is counted. Local mints and durable seeds are uncapped.
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

    /// <summary>A local stamp after every previous mint and the state this write replaces, without a skew cap.</summary>
    public Hlc Tick(Hlc floor = default)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_lock)
        {
            return _last = new Hlc(Math.Max(_last.Value, floor.Value)).Tick(now);
        }
    }

    /// <summary>Restores the uncapped floor from durable stamps before a store is published.</summary>
    public void Seed(Hlc stamp)
    {
        lock (_lock)
        {
            _last = new Hlc(Math.Max(_last.Value, stamp.Value));
        }
    }

    /// <summary>Observes a received stamp with the skew cap, without lowering the local mint floor.</summary>
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
