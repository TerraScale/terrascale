using System.Security.Cryptography;

namespace TerraScale.DotNet;

public sealed class TerraScaleIdGenerator
{
    private const int RandomnessBytes = 10;

    private readonly Lock gate = new();
    private readonly byte[] randomness = new byte[RandomnessBytes];
    private readonly TimeProvider timeProvider;
    private long? lastMilliseconds;

    public TerraScaleIdGenerator() : this(TimeProvider.System) { }

    internal TerraScaleIdGenerator(TimeProvider timeProvider) => this.timeProvider = timeProvider;

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
