using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

internal sealed class MemberClock(TimeProvider time)
{
    private readonly Lock _lock = new();
    private Hlc _last;

    public DateTimeOffset UtcNow => time.GetUtcNow();

    public Hlc Tick(Hlc floor = default)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_lock)
        {
            return _last = new Hlc(Math.Max(_last.Value, floor.Value)).Tick(now);
        }
    }

    public void Seed(Hlc stamp)
    {
        lock (_lock)
        {
            _last = new Hlc(Math.Max(_last.Value, stamp.Value));
        }
    }
}
