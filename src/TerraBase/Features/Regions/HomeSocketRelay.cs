using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Features.Qos;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

internal sealed class HomeSocketRelay(IHttpClientFactory http, InternalCredential credential, QosSessions sessions)
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public async Task RunAsync(HttpContext context, HomeNode home, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(home);
        context.ReleaseRequestLease();
        using var lease = sessions.TryRelay();
        if (lease is null)
        {
            var offered = context.WebSockets.WebSocketRequestedProtocols.Contains(SqlSocketEndpoint.Protocol, StringComparer.Ordinal);
            using var refused = await context.WebSockets.AcceptWebSocketAsync(offered ? SqlSocketEndpoint.Protocol : null).ConfigureAwait(false);
            var error = new SqlError(SqlErrors.TenantBusy, "organization session limit exceeded");
            var reply = new SqlSocketError(null, "error", SqlBatch.Body(new SqlBatchFailure(error, 0), context.TraceIdentifier).Error, false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Shutdown.DrainBudget);
            await refused.SendAsync(JsonSerializer.SerializeToUtf8Bytes(reply, SqlJsonContext.Default.SqlSocketError), WebSocketMessageType.Text, true, deadline.Token)
                .ConfigureAwait(false);
            await refused.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, SqlErrors.TenantBusy, deadline.Token).ConfigureAwait(false);
            return;
        }

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
        catch (Exception error) when (error is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            await HomeForwarding.MisdirectedAsync(context, home.Region).ConfigureAwait(false);
            return;
        }

        using var client = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = upstream.SubProtocol,
            KeepAliveInterval = KeepAlive,
            KeepAliveTimeout = KeepAlive,
        });
        context.Features.GetRequiredFeature<RegionalIngress.Lease>().Dispose();

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var aborting = shutdown.Token.Register(() =>
        {
            client.Abort();
            upstream.Abort();
        }).ConfigureAwait(false);
        var closing = 0;
        var toHome = PumpAsync(client, upstream, BeginClose, shutdown.Token);
        var toClient = PumpAsync(upstream, client, BeginClose, shutdown.Token);
        try
        {
            await Task.WhenAny(toHome, toClient).ConfigureAwait(false);
            BeginClose();
            await Task.WhenAll(toHome, toClient).ConfigureAwait(false);
        }
        finally
        {
            client.Abort();
            upstream.Abort();
            await Task.WhenAll(toHome, toClient).ConfigureAwait(false);
        }

        void BeginClose()
        {
            if (Interlocked.Exchange(ref closing, 1) == 0)
            {
                shutdown.CancelAfter(Shutdown.DrainBudget);
            }
        }
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

    private static async Task PumpAsync(WebSocket from, WebSocket to, Action beginClose, CancellationToken cancellationToken)
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
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ConnectionResetException or ObjectDisposedException)
                {
                    break;
                }

                if (read.MessageType == WebSocketMessageType.Close)
                {
                    beginClose();
                    await CloseAsync(to, read.CloseStatus ?? WebSocketCloseStatus.NormalClosure, read.CloseStatusDescription, cancellationToken).ConfigureAwait(false);
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

            beginClose();
            await CloseAsync(to, WebSocketCloseStatus.EndpointUnavailable, "the relayed session ended", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(status, description, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException or ConnectionResetException)
        {
            socket.Abort();
        }
    }
}
