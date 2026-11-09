using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Connections;
using TerraBase.Features.Qos;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

// The home serves the relayed socket exactly as one that reached it directly, so every region answers alike
// (RFC 8441, HTTP/2 extended CONNECT on the internal listener; docs/terrabase/cross-region.md).
internal sealed class HomeSocketRelay(IHttpClientFactory http, InternalCredential credential)
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public async Task RunAsync(HttpContext context, HomeNode home, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(home);
        context.Features.Get<QosRequestLease>()?.Lease.Dispose();
        using var upstream = new ClientWebSocket
        {
            Options =
            {
                KeepAliveInterval = KeepAlive,
                CollectHttpResponseDetails = true,
                HttpVersion = HttpVersion.Version20,
                HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            },
        };
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
        {
            upstream.Options.AddSubProtocol(protocol);
        }

        // The same headers a forwarded request carries, so the home runs the same token, IP-allowlist and scope checks.
        foreach (var (name, values) in context.Request.Headers)
        {
            if (HomeForwarding.IsForwardable(name) && !name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
            {
                // SetRequestHeader replaces, so one header joins its values as the wire format does.
                upstream.Options.SetRequestHeader(name, string.Join(", ", values.Where(value => value is not null)));
            }
        }

        upstream.Options.SetRequestHeader(HomeForwarding.ForwardedForHeader, (IpAllowlists.ClientOf(context) ?? IPAddress.None).ToString());
        credential.Sign(upstream.Options);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            var route = new UriBuilder(
                new Uri(home.Address, context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent()))
            { Scheme = "ws" }.Uri;
            await upstream.ConnectAsync(route, http.CreateClient(HomeLocator.HttpClientName), timeout.Token).ConfigureAwait(false);
        }
        catch (WebSocketException) when (upstream.HttpStatusCode is not 0)
        {
            // The home answered the upgrade with a refusal: the client sees the same status it would have seen there,
            // with the refusal headers the home set.
            var status = upstream.HttpStatusCode;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-TerraBase-Region"] = home.Region;
            if (upstream.HttpResponseHeaders is { } headers)
            {
                foreach (var (name, values) in headers)
                {
                    if (name is "Retry-After" or "WWW-Authenticate")
                    {
                        context.Response.Headers[name] = values.ToArray();
                    }
                }
            }

            await BadRequests.WriteAsync(context, (int)status, RefusalCode(status), RefusalMessage(status), op: null).ConfigureAwait(false);
            return;
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            // The home cannot be reached: the client sees the forwarded request's 421 wrong_region, naming the home,
            // so both surfaces answer alike.
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-TerraBase-Database-Home"] = home.Region;
            var url = HomeForwarding.RetryUrl(context, home.Region);
            context.Response.Headers.Location = url;
            await BadRequests.WriteAsync(context, StatusCodes.Status421MisdirectedRequest, "wrong_region",
                $"the database is served in {home.Region}: send it to https://{HomeForwarding.Host(home.Region)}", op: null, url: url).ConfigureAwait(false);
            return;
        }

        using var client = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = upstream.SubProtocol,
            KeepAliveInterval = KeepAlive,
            KeepAliveTimeout = KeepAlive,
        });

        var toHome = PumpAsync(client, upstream, cancellationToken);
        var toClient = PumpAsync(upstream, client, cancellationToken);
        await Task.WhenAll(toHome, toClient).ConfigureAwait(false);
    }

    private static string RefusalCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "invalid_key",
        HttpStatusCode.Forbidden => "forbidden_scope",
        HttpStatusCode.NotFound => "unknown_database",
        HttpStatusCode.TooManyRequests => "busy",
        >= HttpStatusCode.InternalServerError => "internal_error",
        _ => "invalid_request",
    };

    private static string RefusalMessage(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "the credential is missing, unknown or expired",
        HttpStatusCode.Forbidden => "the credential cannot run SQL on this database",
        HttpStatusCode.NotFound => "the database does not exist",
        >= HttpStatusCode.InternalServerError => "the home could not serve the session; retry",
        _ => "the home refused the session",
    };

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                WebSocketReceiveResult read;
                try
                {
                    read = await from.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ConnectionResetException)
                {
                    break;
                }

                if (read.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync(to, read.CloseStatus ?? WebSocketCloseStatus.NormalClosure, read.CloseStatusDescription).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await to.SendAsync(buffer.AsMemory(0, read.Count), read.MessageType, read.EndOfMessage, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ConnectionResetException or ObjectDisposedException)
                {
                    break;
                }
            }

            await CloseAsync(to, WebSocketCloseStatus.EndpointUnavailable, "the relayed session ended").ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string? description)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(status, description, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException or ConnectionResetException)
        {
        }
    }
}
