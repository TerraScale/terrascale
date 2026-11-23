namespace TerraScale.Dashboard.Common.Accounts;

public sealed class TerraScaleIdGenerator(TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private Ulid _last;

    public string NewId()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var next = Ulid.NewUlid(now);
            if (now.ToUnixTimeMilliseconds() <= _last.Time.ToUnixTimeMilliseconds())
            {
                next = Increment(_last);
            }

            _last = next;
            return next.ToString();
        }
    }

    private static Ulid Increment(Ulid previous)
    {
        Span<byte> bytes = stackalloc byte[16];
        previous.TryWriteBytes(bytes);
        for (var index = 15; index >= 6; index--)
        {
            if (++bytes[index] != 0)
            {
                break;
            }
        }

        return new Ulid(bytes);
    }
}
