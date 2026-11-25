using System.Net;
using System.Net.Sockets;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Platform.Http;

public sealed class EntryRateLimits(TimeProvider time)
{
    private readonly ClientBuckets signIn = new(time, 120, 60);
    private readonly ClientBuckets registration = new(time, 30, 10);
    private readonly ClientBuckets mcp = new(time, 600, 300);
    private readonly ClientBuckets mcpRejections = new(time, 60, 30);

    public bool AdmitSignIn(HttpContext context) => signIn.Take(ClientKey(context)) is not null;

    public bool AdmitRegistration(HttpContext context) => registration.Take(ClientKey(context)) is not null;

    public McpAdmission? AdmitMcp(HttpContext context)
    {
        var client = ClientKey(context);
        if (mcpRejections.Take(client) is not { } rejection)
        {
            return null;
        }

        if (mcp.Take(client) is null)
        {
            mcpRejections.Refund(rejection);
            return null;
        }

        return new McpAdmission(() => mcpRejections.Refund(rejection));
    }

    public static IResult Refuse(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.RetryAfter = "60";
        return RequestError.Response(context, StatusCodes.Status429TooManyRequests, "Too many requests. Try again in a minute.");
    }

    private static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress switch
    {
        null => "unknown",
        { IsIPv4MappedToIPv6: true } mapped => mapped.MapToIPv4().ToString(),
        { AddressFamily: AddressFamily.InterNetworkV6 } address => Prefix64(address),
        var address => address.ToString(),
    };

    private static string Prefix64(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        return address.TryWriteBytes(bytes, out _) ? Convert.ToHexStringLower(bytes[..8]) + "::/64" : address.ToString();
    }

    private sealed class ClientBuckets(TimeProvider time, int burst, int perMinute)
    {
        private const int Capacity = 50_000;
        private readonly Lock gate = new();
        private readonly Dictionary<string, Bucket> buckets = new(StringComparer.Ordinal);
        private readonly Bucket overflow = new(burst * 8.0, time.GetUtcNow());
        private DateTimeOffset swept;

        public Bucket? Take(string client)
        {
            lock (gate)
            {
                var now = time.GetUtcNow();
                var bucket = Find(client, now);
                Refill(bucket, now, Limit(bucket), Rate(bucket));
                if (bucket.Tokens < 1)
                {
                    return null;
                }

                bucket.Tokens -= 1;
                return bucket;
            }
        }

        public void Refund(Bucket bucket)
        {
            lock (gate)
            {
                Refill(bucket, time.GetUtcNow(), Limit(bucket), Rate(bucket));
                bucket.Tokens = Math.Min(Limit(bucket), bucket.Tokens + 1);
            }
        }

        private double Limit(Bucket bucket) => bucket == overflow ? burst * 8.0 : burst;

        private double Rate(Bucket bucket) => bucket == overflow ? perMinute * 8.0 : perMinute;

        private Bucket Find(string client, DateTimeOffset now)
        {
            if (buckets.TryGetValue(client, out var known))
            {
                return known;
            }

            if (buckets.Count >= Capacity && now - swept < TimeSpan.FromSeconds(1))
            {
                return overflow;
            }

            if (buckets.Count >= Capacity)
            {
                swept = now;
                foreach (var (key, bucket) in buckets)
                {
                    Refill(bucket, now, burst, perMinute);
                    if (bucket.Tokens >= burst)
                    {
                        buckets.Remove(key);
                    }
                }

                if (buckets.Count >= Capacity)
                {
                    return overflow;
                }
            }

            var created = new Bucket(burst, now);
            buckets[client] = created;
            return created;
        }

        private static void Refill(Bucket bucket, DateTimeOffset now, double limit, double perMinute)
        {
            var elapsed = now - bucket.At;
            if (elapsed > TimeSpan.Zero)
            {
                bucket.Tokens = Math.Min(limit, bucket.Tokens + elapsed.TotalMinutes * perMinute);
                bucket.At = now;
            }
        }

    }

    private sealed class Bucket(double tokens, DateTimeOffset at)
    {
        public double Tokens { get; set; } = tokens;

        public DateTimeOffset At { get; set; } = at;
    }
}

public sealed class McpAdmission(Action refund)
{
    private int refunded;

    public void Refund()
    {
        if (Interlocked.Exchange(ref refunded, 1) == 0)
        {
            refund();
        }
    }
}
