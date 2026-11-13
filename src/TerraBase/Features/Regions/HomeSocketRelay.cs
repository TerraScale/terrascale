using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Features.Qos;
using TerraBase.Features.Replicas;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

internal sealed class HomeSocketRelay(IHttpClientFactory http, InternalCredential credential)
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public async Task RunAsync(HttpContext context, HomeNode home, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(home);
        if (Listeners.IsForwarded(context))
        {
            await HomeForwarding.UnavailableAsync(context, home.Region).ConfigureAwait(false);
            return;
        }

        context.ReleaseRequestLease();
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

        foreach (var (name, values) in context.Request.Headers)
        {
            if (HomeForwarding.IsForwardable(name) && !name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
            {
                upstream.Options.SetRequestHeader(name, string.Join(", ", values.Where(value => value is not null)));
            }
        }

        if (Listeners.ArrivedOn(context) == TerraBaseListener.Client)
        {
            upstream.Options.SetRequestHeader(HomeForwarding.ForwardedForHeader, (IpAllowlists.ClientOf(context) ?? IPAddress.None).ToString());
        }

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
        catch (Exception error) when (error is WebSocketException or HttpRequestException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await HomeForwarding.UnavailableAsync(context, home.Region).ConfigureAwait(false);
            return;
        }

        using var client = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = upstream.SubProtocol,
            KeepAliveInterval = KeepAlive,
            KeepAliveTimeout = KeepAlive,
        });
        context.Features.GetRequiredFeature<RegionalIngress.Lease>().Dispose();

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
        HttpStatusCode.ServiceUnavailable => "unavailable",
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
