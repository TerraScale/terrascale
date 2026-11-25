using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Platform.Http;

public sealed class EntryRateLimits : IDisposable
{
    private readonly PartitionedRateLimiter<string> signIn = PerClient(120, 60);
    private readonly PartitionedRateLimiter<string> registration = PerClient(30, 10);
    private readonly PartitionedRateLimiter<string> mcp = PerClient(600, 300);
    private readonly PartitionedRateLimiter<string> mcpRejections = PerClient(60, 30);

    public bool AdmitSignIn(HttpContext context) => Admit(signIn, context);

    public bool AdmitRegistration(HttpContext context) => Admit(registration, context);

    public bool AdmitMcp(HttpContext context) => Admit(mcp, context) && mcpRejections.GetStatistics(ClientKey(context)) is not { CurrentAvailablePermits: <= 0 };

    public void RecordMcpRejection(HttpContext context)
    {
        using var lease = mcpRejections.AttemptAcquire(ClientKey(context));
    }

    public static IResult Refuse(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.RetryAfter = "60";
        return RequestError.Response(context, StatusCodes.Status429TooManyRequests, "Too many requests. Try again in a minute.");
    }

    public void Dispose()
    {
        signIn.Dispose();
        registration.Dispose();
        mcp.Dispose();
        mcpRejections.Dispose();
    }

    private static bool Admit(PartitionedRateLimiter<string> limiter, HttpContext context)
    {
        using var lease = limiter.AttemptAcquire(ClientKey(context));
        return lease.IsAcquired;
    }

    private static PartitionedRateLimiter<string> PerClient(int burst, int perMinute) =>
        PartitionedRateLimiter.Create<string, string>(client => RateLimitPartition.GetTokenBucketLimiter(client, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1) / perMinute,
            QueueLimit = 0,
            AutoReplenishment = true,
        }), StringComparer.Ordinal);

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
}
