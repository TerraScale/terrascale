using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Authorization;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Tenancy;
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
        using var preauthentication = sessions.TryPreauthenticate(IpAllowlists.ClientOf(context));
        var offered = context.WebSockets.WebSocketRequestedProtocols.Contains(SqlSocketEndpoint.Protocol, StringComparer.Ordinal);
        using var client = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = offered ? SqlSocketEndpoint.Protocol : null,
            KeepAliveInterval = KeepAlive,
            KeepAliveTimeout = KeepAlive,
        }).ConfigureAwait(false);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var aborting = shutdown.Token.Register(client.Abort).ConfigureAwait(false);
        if (preauthentication is null)
        {
            await RefuseAsync(client, SqlErrors.TenantBusy, "organization session limit exceeded", cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[]? authFrame = null;
        TenantContext? tenant;
        using (var authenticating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            authenticating.CancelAfter(SqlSocket.AuthDeadline);
            context.RequestAborted = authenticating.Token;
            var authClosing = Task.CompletedTask;
            await using var expiry = authenticating.Token.Register(() => authClosing = ExpireAuthenticationAsync()).ConfigureAwait(false);
            try
            {
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    var bytes = new byte[16 * 1024];
                    var length = 0;
                    ValueWebSocketReceiveResult read;
                    do
                    {
                        read = await client.ReceiveAsync(bytes.AsMemory(length), shutdown.Token).ConfigureAwait(false);
                        length += read.Count;
                        if (read.MessageType != WebSocketMessageType.Text || length == bytes.Length && !read.EndOfMessage)
                        {
                            await RefuseAsync(client, SqlErrors.Unauthorized, "the first frame is an auth frame", cancellationToken).ConfigureAwait(false);
                            return;
                        }
                    }
                    while (!read.EndOfMessage);
                    var auth = JsonSerializer.Deserialize(bytes.AsSpan(0, length), SqlJsonContext.Default.SqlSocketRequest);
                    if (auth is not { Type: "auth", Token: { Length: > 0 and <= 8192 } token })
                    {
                        await RefuseAsync(client, SqlErrors.Unauthorized, "the first frame is an auth frame", cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    context.Request.Headers.Authorization = "Bearer " + token;
                    var authenticated = await ApiKeysExtensions.AuthenticateAgainAsync(context).ConfigureAwait(false);
                    if (!authenticated.Succeeded)
                    {
                        await RefuseAsync(client, SqlErrors.Unauthorized, "the credential was refused", cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    context.User = authenticated.Principal;
                    context.Request.Headers.Remove("Authorization");
                    authFrame = bytes[..length];
                }

                tenant = await TenantAsync(context, authenticating.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && authenticating.IsCancellationRequested
                && error is OperationCanceledException or WebSocketException)
            {
                await authClosing.ConfigureAwait(false);
                return;
            }
            catch (JsonException)
            {
                await RefuseAsync(client, SqlErrors.Unauthorized, "the first frame is an auth frame", cancellationToken).ConfigureAwait(false);
                return;
            }

            async Task ExpireAuthenticationAsync()
            {
                shutdown.CancelAfter(Shutdown.DrainBudget);
                await CloseAsync(client, WebSocketCloseStatus.PolicyViolation, "no auth frame within 5 seconds", shutdown.Token).ConfigureAwait(false);
            }
        }

        context.RequestAborted = cancellationToken;
        if (tenant is null)
        {
            await RefuseAsync(client, SqlErrors.Unauthorized, "the credential was refused", cancellationToken).ConfigureAwait(false);
            return;
        }

        using var lease = sessions.TryEnter(tenant, relay: true);
        preauthentication.Dispose();
        if (lease is null)
        {
            await RefuseAsync(client, SqlErrors.TenantBusy, "organization session limit exceeded", cancellationToken).ConfigureAwait(false);
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
        catch (Exception error) when (error is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            await RefuseAsync(client, SqlErrors.Unavailable, "the home could not serve the session; retry", cancellationToken).ConfigureAwait(false);
            return;
        }

        context.Features.GetRequiredFeature<RegionalIngress.Lease>().Dispose();
        await using var abortUpstream = shutdown.Token.Register(upstream.Abort).ConfigureAwait(false);
        if (authFrame is not null)
        {
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sending.CancelAfter(ConnectTimeout);
            await upstream.SendAsync(authFrame, WebSocketMessageType.Text, true, sending.Token).ConfigureAwait(false);
        }

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

    private static async ValueTask<TenantContext?> TenantAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var services = context.RequestServices;
        if (!(await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context.User, context, ApiKeyScopes.SqlRead).ConfigureAwait(false)).Succeeded
            || context.Request.RouteValues["project"] is not string project
            || (context.Features.Get<ApiKey>() is { } key ? key.ProjectId is not null && key.ProjectId != project
                : context.User.FindFirst("project_id")?.Value != project)
            || await services.GetRequiredService<IProjectCatalog>().ProjectOrganizationAsync(project, cancellationToken).ConfigureAwait(false) is not string organization
            || await services.GetRequiredService<ITenantDirectory>().PlanAsync(organization, cancellationToken).ConfigureAwait(false) is not PlanTier plan)
        {
            return null;
        }

        return context.Features.Get<ApiKey>() is { } granted && granted.OrganizationId != organization
            ? null : new TenantContext(organization, plan, project);
    }

    private static async Task RefuseAsync(WebSocket client, string code, string message, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Shutdown.DrainBudget);
        var error = new SqlError(code, message);
        var reply = new SqlSocketError(null, "error", SqlBatch.Body(new SqlBatchFailure(error, 0), "").Error, false);
        try
        {
            await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(reply, SqlJsonContext.Default.SqlSocketError), WebSocketMessageType.Text, true, deadline.Token)
                .ConfigureAwait(false);
            await client.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, code, deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            client.Abort();
        }
    }

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
                    using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    sending.CancelAfter(QosSessions.MessageTimeout);
                    await to.SendAsync(buffer.AsMemory(0, read.Count), read.MessageType, read.EndOfMessage, sending.Token).ConfigureAwait(false);
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
