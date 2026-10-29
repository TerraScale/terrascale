namespace TerraBase.Fast.Management;

// A query deadline clock that reads 0 until armed; then each reading returns the current time and moves it 6 seconds
// on, past the engine's 5-second query deadline.
internal sealed class SteppingClock : TimeProvider
{
    private static readonly long Step = 6 * TimeSpan.TicksPerSecond;
    private long _now;
    private volatile bool _armed;

    // Set by a test or hook, read by the engine on other threads.
    public bool Armed
    {
        get => _armed;
        set => _armed = value;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Armed ? Interlocked.Add(ref _now, Step) - Step : Interlocked.Read(ref _now);
}
