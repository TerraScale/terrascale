using NUlid;
using NUlid.Rng;

namespace TerraScale.DotNet;

/// <summary>
/// Generates plain uppercase entity ULIDs using cryptographic randomness and NUlid's monotonic generator.
/// Reuse one instance per caller-owned service. Calls on that instance are thread-safe and strictly increasing,
/// including within a millisecond; a backward clock uses the last observed timestamp until the clock catches up.
/// Independent instances do not share ordering state. IDs are not authentication secrets.
/// </summary>
/// <remarks>
/// The timestamp is a logical ordering value, not a trusted creation time. A forward clock jump followed by
/// correction keeps this instance at the future timestamp until UTC catches up, even if that takes years.
/// After correcting a bad host clock, replace the generator to resume current timestamps; ordering across
/// the old and new instances is not guaranteed. Forward jumps are accepted because a service may legitimately
/// leave its generator idle for any length of time.
/// </remarks>
public sealed class TerraScaleIdGenerator
{
    private readonly object gate = new();
    private readonly MonotonicUlidRng random = new(new CSUlidRng());
    private readonly TimeProvider timeProvider;
    private DateTimeOffset? lastTime;

    /// <summary>Creates an instance-owned generator using the system UTC clock.</summary>
    public TerraScaleIdGenerator() : this(TimeProvider.System) { }

    internal TerraScaleIdGenerator(TimeProvider timeProvider) => this.timeProvider = timeProvider;

    /// <summary>Creates a new canonical uppercase 26-character ULID, without an entity prefix.</summary>
    /// <exception cref="OverflowException">The 80-bit monotonic random component is exhausted before time advances.</exception>
    public string NewId()
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            if (lastTime is { } previous && now < previous)
            {
                now = previous;
            }

            var id = Ulid.NewUlid(now, random).ToString();
            lastTime = now;
            return id;
        }
    }
}
