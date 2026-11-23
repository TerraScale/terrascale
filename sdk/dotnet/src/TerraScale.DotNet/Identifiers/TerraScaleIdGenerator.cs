using System.Security.Cryptography;

namespace TerraScale.DotNet;

/// <summary>
/// Generates plain uppercase entity ULIDs using cryptographic randomness and a monotonic random component.
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
    private const int RandomnessBytes = 10;

    private readonly Lock gate = new();
    private readonly byte[] randomness = new byte[RandomnessBytes];
    private readonly TimeProvider timeProvider;
    private long? lastMilliseconds;

    /// <summary>Creates an instance-owned generator using the system UTC clock.</summary>
    public TerraScaleIdGenerator() : this(TimeProvider.System) { }

    internal TerraScaleIdGenerator(TimeProvider timeProvider) => this.timeProvider = timeProvider;

    /// <summary>Creates a new canonical uppercase 26-character ULID, without an entity prefix.</summary>
    /// <exception cref="OverflowException">The 80-bit monotonic random component is exhausted before time advances.</exception>
    public string NewId()
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (lastMilliseconds is { } previous && now <= previous)
            {
                now = previous;
                Increment(randomness);
            }
            else
            {
                RandomNumberGenerator.Fill(randomness);
            }

            lastMilliseconds = now;
            return Ulid.NewUlid(DateTimeOffset.FromUnixTimeMilliseconds(now), randomness).ToString();
        }
    }

    private static void Increment(Span<byte> value)
    {
        if (!value.ContainsAnyExcept(byte.MaxValue))
        {
            throw new OverflowException("The ULID random component is exhausted within this millisecond.");
        }

        var index = value.Length - 1;
        while (++value[index] == 0)
        {
            index--;
        }
    }
}
