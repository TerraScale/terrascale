using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using IPNetwork = System.Net.IPNetwork;

namespace TerraScale.Platform.Hosting;

public static class ClientAdmission
{
    public const int MaxRequestBodyBytes = 128 * 1024;

    private static readonly IReadOnlyList<IPNetwork> PrivateNetworks =
    [
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10"),
    ];

    private static readonly PlatformQuotaExceeded Throttled = new("too many requests from this address; retry shortly", "rate_limited");

    private static readonly TokenBucketRateLimiterOptions ClientBudget = new()
    {
        TokenLimit = 600,
        TokensPerPeriod = 100,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    public static ForwardedHeadersOptions Forwarding()
    {
        var forwarding = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        forwarding.KnownProxies.Clear();
        forwarding.KnownIPNetworks.Clear();
        foreach (var network in PrivateNetworks)
        {
            forwarding.KnownIPNetworks.Add(network);
        }

        return forwarding;
    }

    public static void Configure(RateLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(static context => Partition(context.Connection.RemoteIpAddress));
        options.OnRejected = static (rejected, _) => new ValueTask(ErrorResults.From(Throttled).ExecuteAsync(rejected.HttpContext));
    }

    private static RateLimitPartition<string> Partition(IPAddress? remote)
    {
        if (remote is null)
        {
            return RateLimitPartition.GetTokenBucketLimiter("unknown", static _ => ClientBudget);
        }

        var address = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        return PrivateNetworks.Any(network => network.Contains(address))
            ? RateLimitPartition.GetNoLimiter("private")
            : RateLimitPartition.GetTokenBucketLimiter(Client(address), static _ => ClientBudget);
    }

    private static string Client(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString();
    }
}
