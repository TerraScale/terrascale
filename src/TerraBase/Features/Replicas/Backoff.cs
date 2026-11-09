namespace TerraBase.Features.Replicas;

internal struct Backoff
{
    private readonly TimeSpan _first;
    private readonly TimeSpan _max;
    private TimeSpan _current;

    public Backoff(TimeSpan first, TimeSpan max)
    {
        _first = first;
        _max = max;
        _current = first;
    }

    public TimeSpan Next()
    {
        var current = _current;
        _current = TimeSpan.FromTicks(Math.Min(_current.Ticks * 2, _max.Ticks));
        return current;
    }

    public void Reset() => _current = _first;
}
