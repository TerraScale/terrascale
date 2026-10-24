using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraBase.Features.Webhooks;

/// <summary>Validates public receivers and sends signed, bounded webhook attempts.</summary>
public interface IWebhookTransport
{
    /// <summary>Checks the URL and every current DNS answer without contacting the receiver.</summary>
    ValueTask<bool> ValidateAsync(string url, CancellationToken cancellationToken);

    /// <summary>Sends the exact persisted body with a fresh attempt timestamp.</summary>
    ValueTask<WebhookAttempt> SendAsync(string url, string secret, string deliveryId, ReadOnlyMemory<byte> body,
        DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// An in-process receiver for its own URL scheme (customer functions): such URLs are delivered without the network. A receiver
/// must refuse a body whose project and database are not the ones its URL names.
/// </summary>
public interface IWebhookLocalReceiver
{
    /// <summary>Whether <paramref name="url"/> names this receiver.</summary>
    bool Accepts(string url);

    /// <summary>Delivers the exact persisted body.</summary>
    ValueTask<WebhookAttempt> ReceiveAsync(string url, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

/// <summary>A receiver status or a redacted transport failure; no receiver content is retained.</summary>
public sealed record WebhookAttempt(int? HttpStatus, string? ErrorCode);

/// <summary>Resolves receivers and opens streams only to addresses already approved by the transport.</summary>
public interface IWebhookNetwork
{
    /// <summary>Returns all addresses for the receiver host.</summary>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);

    /// <summary>Connects directly to the supplied IP addresses without another DNS lookup.</summary>
    ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken);
}

/// <summary>Registers the public-only webhook transport and its network boundary.</summary>
public static class WebhookTransportExtensions
{
    /// <summary>Registers secure outbound webhook delivery with no operator settings.</summary>
    public static IServiceCollection AddTerraBaseWebhookTransport(this IServiceCollection services)
    {
        services.TryAddSingleton<IWebhookNetwork, WebhookNetwork>();
        services.AddHttpClient(WebhookTransport.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(provider => WebhookTransport.Handler(provider.GetRequiredService<IWebhookNetwork>()))
            .RemoveAllLoggers();
        return services.AddSingleton<IWebhookTransport, WebhookTransport>();
    }
}

internal sealed class WebhookTransport(IHttpClientFactory clients, IWebhookNetwork network, IEnumerable<IWebhookLocalReceiver> receivers) : IWebhookTransport
{
    internal const string HttpClientName = "TerraBase.Webhooks";
    private static readonly HttpRequestOptionsKey<bool> TargetRejected = new("TerraBase.Webhooks.TargetRejected");

    // Match the customer-backup IANA policy locally: feature boundaries must not depend on another slice's internals.
    private static readonly IPNetwork[] DeniedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("3ffe::/16"), IPNetwork.Parse("3fff::/20"),
    ];
    private static readonly IPNetwork[] GloballyReachableExceptions =
    [
        IPNetwork.Parse("192.0.0.9/32"), IPNetwork.Parse("192.0.0.10/32"),
        IPNetwork.Parse("2001:1::1/128"), IPNetwork.Parse("2001:1::2/128"), IPNetwork.Parse("2001:1::3/128"),
        IPNetwork.Parse("2001:3::/32"), IPNetwork.Parse("2001:4:112::/48"),
        IPNetwork.Parse("2001:20::/28"), IPNetwork.Parse("2001:30::/28"),
    ];
    private static readonly IPNetwork GlobalUnicastV6 = IPNetwork.Parse("2000::/3");

    public async ValueTask<bool> ValidateAsync(string url, CancellationToken cancellationToken)
    {
        if (receivers.Any(receiver => receiver.Accepts(url)))
        {
            return true;
        }

        if (Endpoint(url) is not { } endpoint)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return Approved(await network.ResolveAsync(endpoint.IdnHost, timeout.Token).ConfigureAwait(false));
        }
        catch (Exception error) when (error is SocketException or HttpRequestException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async ValueTask<WebhookAttempt> SendAsync(string url, string secret, string deliveryId, ReadOnlyMemory<byte> body,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (receivers.FirstOrDefault(receiver => receiver.Accepts(url)) is { } local)
        {
            return await local.ReceiveAsync(url, body, cancellationToken).ConfigureAwait(false);
        }

        if (Endpoint(url) is not { } endpoint)
        {
            return new(null, "target_rejected");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        try
        {
            var key = Base64Url.DecodeFromChars(secret);
            try
            {
                if (key.Length != 32)
                {
                    return new(null, "transport_error");
                }

                var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                using var signature = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
                signature.AppendData(Encoding.ASCII.GetBytes(timestamp + "."));
                signature.AppendData(body.Span);
                request.Headers.Add("X-TerraScale-Delivery", deliveryId);
                request.Headers.Add("X-TerraScale-Timestamp", timestamp);
                request.Headers.Add("X-TerraScale-Signature", "v1=" + Convert.ToHexStringLower(signature.GetHashAndReset()));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            request.Content = new ReadOnlyMemoryContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return new((int)response.StatusCode, response.IsSuccessStatusCode ? null : "http_error");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or SocketException or FormatException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(null, request.Options.TryGetValue(TargetRejected, out var rejected) && rejected ? "target_rejected" : "transport_error");
        }
    }

    internal static SocketsHttpHandler Handler(IWebhookNetwork network) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        MaxResponseHeadersLength = 16,
        MaxResponseDrainSize = 0,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await network.ResolveAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            if (context.DnsEndPoint.Port != 443 || !Approved(addresses))
            {
                context.InitialRequestMessage.Options.Set(TargetRejected, true);
                throw new HttpRequestException("webhook target rejected");
            }

            return await network.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
        },
    };

    private static Uri? Endpoint(string url) =>
        url is { Length: > 0 and <= 2048 } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
        && (!IPAddress.TryParse(uri.IdnHost, out var address) || Public(address))
            ? uri : null;

    private static bool Approved(IPAddress[] addresses) => addresses.Length is > 0 and <= 32 && addresses.All(Public);

    private static bool Public(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork
            && (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0 || !GlobalUnicastV6.Contains(address)))
        {
            return false;
        }

        // ISATAP embeds IPv4 in otherwise global IPv6 and can route an approved address to a private target.
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[8] is 0 or 2 && bytes[9] == 0 && bytes[10] == 0x5e && bytes[11] == 0xfe)
            {
                return false;
            }
        }

        return !DeniedNetworks.Any(prefix => prefix.Contains(address))
            || GloballyReachableExceptions.Any(prefix => prefix.Contains(address));
    }
}

internal sealed class WebhookNetwork : IWebhookNetwork
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

    public async ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
