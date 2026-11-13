using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using TerraBase.Features.Sql;
using TerraBase.Tenancy;
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
    internal sealed record BrowserConnection(WebSocket Socket, long? Id);

    public async Task RunAsync(HttpContext context, HomeNode home, CancellationToken cancellationToken, WebSocket? accepted = null, byte[]? authFrame = null, int attempt = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(home);
        if (Listeners.IsForwarded(context))
        {
            await HomeForwarding.UnavailableAsync(context, home.Region).ConfigureAwait(false);
            return;
        }

        context.Features.Set(new HomeForwarding.ProxiedRequest());
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
            if ((authFrame is null || !name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                && HomeForwarding.IsForwardable(name) && !name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
            {
                upstream.Options.SetRequestHeader(name, string.Join(", ", values.Where(value => value is not null)));
            }
        }

        if (Listeners.ArrivedOn(context) == TerraBaseListener.Client)
        {
            upstream.Options.SetRequestHeader(HomeForwarding.ForwardedForHeader, (IpAllowlists.ClientOf(context) ?? IPAddress.None).ToString());
        }

        upstream.Options.SetRequestHeader(ProxyRequestBudget.RemainingHeader, ProxyRequestBudget.For(context).Milliseconds);
        credential.Sign(upstream.Options);
        try
        {
            using var timeout = ProxyRequestBudget.For(context).Link(cancellationToken);
            var route = new UriBuilder(
                new Uri(home.Address, context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent()))
            { Scheme = "ws" }.Uri;
            await upstream.ConnectAsync(route, http.CreateClient(HomeLocator.HttpClientName), timeout.Token).ConfigureAwait(false);
        }
        catch (WebSocketException) when (upstream.HttpStatusCode is not 0)
        {
            var status = upstream.HttpStatusCode;
            if (status == HttpStatusCode.ServiceUnavailable
                && upstream.HttpResponseHeaders?.ContainsKey(HomeForwarding.PlacementMissHeader) == true
                && await RefreshAsync().ConfigureAwait(false) is { } fresh)
            {
                await RunAsync(context, fresh, cancellationToken, accepted, authFrame, attempt + 1).ConfigureAwait(false);
                return;
            }

            if (accepted is not null)
            {
                await RefuseAsync(context, accepted, context.Features.Get<BrowserConnection>()?.Id,
                    RefusalCode(status), RefusalMessage(status), cancellationToken).ConfigureAwait(false);
                return;
            }

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
            if (accepted is not null)
            {
                await RefuseAsync(context, accepted, context.Features.Get<BrowserConnection>()?.Id, "unavailable",
                    $"the database region {home.Region} could not be reached", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await HomeForwarding.UnavailableAsync(context, home.Region).ConfigureAwait(false);
            }
            return;
        }

        context.ReleaseRequestLease();
        using var client = accepted ?? await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = upstream.SubProtocol,
            KeepAliveInterval = KeepAlive,
            KeepAliveTimeout = KeepAlive,
        });
        context.Features.Get<RegionalIngress.Lease>()?.Dispose();

        if (authFrame is not null)
        {
            using var authDeadline = ProxyRequestBudget.For(context).Link(cancellationToken);
            await upstream.SendAsync(authFrame, WebSocketMessageType.Text, true, authDeadline.Token).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            var length = 0;
            ValueWebSocketReceiveResult reply;
            do
            {
                reply = await upstream.ReceiveAsync(buffer.AsMemory(length), authDeadline.Token).ConfigureAwait(false);
                length += reply.Count;
                if (length == buffer.Length && !reply.EndOfMessage)
                {
                    throw new WebSocketException("the destination auth response exceeds its bound");
                }
            } while (!reply.EndOfMessage);
            if (reply.MessageType == WebSocketMessageType.Text
                && JsonDocument.Parse(buffer.AsMemory(0, length)) is { } authReply)
            {
                using (authReply)
                {
                    if (authReply.RootElement.TryGetProperty("error", out var error)
                        && error.TryGetProperty("message", out var message)
                        && message.GetString() == HomeForwarding.PlacementMissMessage
                        && await RefreshAsync().ConfigureAwait(false) is { } fresh)
                    {
                        await RunAsync(context, fresh, cancellationToken, client, authFrame, attempt + 1).ConfigureAwait(false);
                        return;
                    }
                }
            }
            await client.SendAsync(buffer.AsMemory(0, length), reply.MessageType, true, authDeadline.Token).ConfigureAwait(false);
        }

        if (context.Request.Path.Value?.EndsWith("/sql/ws", StringComparison.Ordinal) == true && !context.User.IsOperator())
        {
            await context.RequestServices.GetRequiredService<SocketRequestRelay>()
                .RunAsync(context, client, upstream, cancellationToken).ConfigureAwait(false);
            return;
        }

        var toHome = PumpAsync(client, upstream, cancellationToken);
        var toClient = PumpAsync(upstream, client, cancellationToken);
        await Task.WhenAll(toHome, toClient).ConfigureAwait(false);

        async ValueTask<HomeNode?> RefreshAsync()
        {
            if (attempt != 0 || context.Request.RouteValues["project"] is not string project)
            {
                return null;
            }
            using var deadline = ProxyRequestBudget.For(context).Link(cancellationToken);
            var fresh = await context.RequestServices.GetRequiredService<IHomeLocator>()
                .RefreshAsync(project, context.Request.RouteValues["database"] as string, deadline.Token).ConfigureAwait(false);
            return fresh?.Region != home.Region ? fresh : null;
        }
    }

    internal static Task RefuseBusyAsync(HttpContext context, int retryAfter)
    {
        var browser = context.Features.GetRequiredFeature<BrowserConnection>();
        return RefuseAsync(context, browser.Socket, browser.Id, SqlErrors.TenantBusy,
            $"organization request budget exceeded; retry after {retryAfter}s", context.RequestAborted);
    }

    internal static async Task RefuseAsync(HttpContext context, WebSocket socket, long? id, string code, string message, CancellationToken cancellationToken)
    {
        var error = new SqlSocketError(id, "error",
            SqlBatch.Body(new SqlBatchFailure(new SqlError(code, message), 0), context.TraceIdentifier).Error, false);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(error, SqlJsonContext.Default.SqlSocketError),
            WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, code, cancellationToken).ConfigureAwait(false);
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
