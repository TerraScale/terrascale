using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Features.Memory;
using TerraBase.Features.Sql;
using TerraBase.Tenancy;
using TerraBase.Features.Qos;
using TerraBase.Features.Replicas;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

internal sealed class HomeSocketRelay(IHttpClientFactory http, InternalCredential credential, QosSessions sessions, IQosAdmission admission, TimeProvider time)
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

        if (!await ForwardingAuthorization.CheckAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        context.Features.Set(new HomeForwarding.ProxiedRequest());
        var limited = context.Request.Path.Value?.EndsWith("/sql/ws", StringComparison.Ordinal) == true && !context.User.IsOperator();
        var tenant = limited ? QosContext.Current ?? throw new InvalidOperationException("a relayed SQL socket must have an admitted organization") : null;
        using var session = tenant is not null && attempt == 0 ? sessions.TryEnter(tenant, relay: true) : null;
        if (tenant is not null && attempt == 0 && session is null)
        {
            context.ReleaseRequestLease();
            var refused = accepted ?? await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                SubProtocol = context.WebSockets.WebSocketRequestedProtocols.Contains(SqlSocketEndpoint.Protocol, StringComparer.Ordinal) ? SqlSocketEndpoint.Protocol : null,
                KeepAliveInterval = KeepAlive,
                KeepAliveTimeout = KeepAlive,
            }).ConfigureAwait(false);
            context.Features.Get<RegionalIngress.Lease>()?.Dispose();
            await RefuseAsync(context, refused, context.Features.Get<BrowserConnection>()?.Id, SqlErrors.TenantBusy,
                "organization session limit exceeded", cancellationToken).ConfigureAwait(false);
            return;
        }

        var answers = new SqlSocketInput(time);
        using var observed = answers.Observe(http.CreateClient(HomeLocator.HttpClientName));
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
            await upstream.ConnectAsync(route, observed, timeout.Token).ConfigureAwait(false);
        }
        catch (WebSocketException) when (upstream.HttpStatusCode is not 0)
        {
            var status = upstream.HttpStatusCode;
            var misplaced = status == HttpStatusCode.MisdirectedRequest
                    && upstream.HttpResponseHeaders?.ContainsKey("X-TerraBase-Database-Home") == true
                || status == HttpStatusCode.ServiceUnavailable
                    && upstream.HttpResponseHeaders?.ContainsKey(HomeForwarding.PlacementMissHeader) == true;
            if (misplaced
                && await RefreshAsync().ConfigureAwait(false) is { } fresh)
            {
                await RunAsync(context, fresh, cancellationToken, accepted, authFrame, attempt + 1).ConfigureAwait(false);
                return;
            }

            if (misplaced)
            {
                status = HttpStatusCode.ServiceUnavailable;
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
            answers.Complete();
            if (reply.MessageType == WebSocketMessageType.Text
                && JsonDocument.Parse(buffer.AsMemory(0, length)) is { } authReply)
            {
                using (authReply)
                {
                    if (authReply.RootElement.TryGetProperty("error", out var error)
                        && (error.TryGetProperty("message", out var message) && message.GetString() == HomeForwarding.PlacementMissMessage
                            || error.TryGetProperty("code", out var code) && code.GetString() == SqlErrors.WrongRegion))
                    {
                        if (await RefreshAsync().ConfigureAwait(false) is { } fresh)
                        {
                            await RunAsync(context, fresh, cancellationToken, client, authFrame, attempt + 1).ConfigureAwait(false);
                            return;
                        }
                        await RefuseAsync(context, client, context.Features.Get<BrowserConnection>()?.Id, SqlErrors.Unavailable,
                            $"the database region {home.Region} could not be reached", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }
            await client.SendAsync(buffer.AsMemory(0, length), reply.MessageType, true, authDeadline.Token).ConfigureAwait(false);
        }

        if (tenant is not null)
        {
            await RelayAsync(context, client, upstream, answers, tenant, home.Region, cancellationToken).ConfigureAwait(false);
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

    private async Task RelayAsync(HttpContext context, WebSocket client, WebSocket upstream, SqlSocketInput answers, TenantContext tenant, string home, CancellationToken cancellationToken)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var aborting = shutdown.Token.Register(client.Abort).ConfigureAwait(false);
        await using var abortUpstream = shutdown.Token.Register(upstream.Abort).ConfigureAwait(false);
        var closing = 0;
        using var state = new SessionRelay(time);
        using var clientSends = new SemaphoreSlim(1, 1);

        var memory = context.RequestServices.GetRequiredService<IMemoryBudget>();
        var toHome = PumpAsync(client, upstream, BeginClose, state, clientSends, context.Features.GetRequiredFeature<SqlSocketInput>(), answers: null, memory, tenant, home, Admit, shutdown.Token);
        var toClient = PumpAsync(upstream, client, BeginClose, state, clientSends, input: null, answers, memory, tenant, home, admit: null, shutdown.Token);
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

        (IDisposable? Lease, int RetryAfter) Admit()
        {
            var lease = admission.TryEnter(tenant, out var retryAfter);
            return (lease, retryAfter);
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

            await CloseAsync(to, WebSocketCloseStatus.EndpointUnavailable, "the relayed session ended", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReportAsync(WebSocket client, string code, string message, long? id, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Shutdown.DrainBudget);
        var error = new SqlError(code, message);
        var reply = new SqlSocketError(id, "error", SqlBatch.Body(new SqlBatchFailure(error, 0), "").Error, false);
        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(reply, SqlJsonContext.Default.SqlSocketError), WebSocketMessageType.Text, true, deadline.Token)
            .ConfigureAwait(false);
        await client.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, code, deadline.Token).ConfigureAwait(false);
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, Action beginClose, SessionRelay state, SemaphoreSlim clientSends, SqlSocketInput? input, SqlSocketInput? answers, IMemoryBudget memory, TenantContext tenant, string home, Func<(IDisposable? Lease, int RetryAfter)>? admit, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                WebSocketReceiveResult read;
                bool? answered = null;
                (long? Id, int RetryAfter, bool Transaction)? refused = null;
                var deadline = input is null ? state.Answer : state.Wait();
                var closing = Task.CompletedTask;
                var expiry = deadline.Token.Register(() => closing = ExpireAsync());
                if (input is not null)
                {
                    input.Progress = (bytes, started) => deadline.Progress(bytes, started);
                }

                if (answers is not null)
                {
                    answers.Progress = (bytes, started) => deadline.Progress(bytes, started);
                }

                var maxBytes = input is null ? HomeSessionFrames.MaxBytes : SqlBatch.MaxBytes;
                using var reservation = memory.TryReserve(16 * 1024, tenant) ?? throw new QosMemoryException();
                using var message = new MemoryStream(16 * 1024);
                try
                {
                    do
                    {
                        read = await from.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        if (message.Length + read.Count > maxBytes)
                        {
                            throw new InvalidDataException("relay message too large");
                        }

                        if (message.Length + read.Count > message.Capacity)
                        {
                            var capacity = Math.Min(maxBytes, Math.Max(message.Capacity * 2, (int)message.Length + read.Count));
                            if (!memory.TryGrow(reservation, capacity))
                            {
                                throw new QosMemoryException();
                            }

                            var previous = message.Capacity;
                            message.Capacity = capacity;
                            memory.Shrink(reservation, previous);
                        }

                        message.Write(buffer, 0, read.Count);
                    }
                    while (!read.EndOfMessage);
                    deadline.Complete();
                    input?.Complete();
                    answers?.Complete();
                    if (read.MessageType != WebSocketMessageType.Close)
                    {
                        if (input is not null)
                        {
                            using var call = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                            var kind = call.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
                            var snapshot = state.State;
                            var transactionOpen = snapshot.Transaction && !snapshot.Pending;
                            var commits = kind == "commit";
                            if (kind == "execute" && call.RootElement.TryGetProperty("statements", out var statements) && statements.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var statement in statements.EnumerateArray())
                                {
                                    if (statement.TryGetProperty("sql", out var sql) && sql.ValueKind == JsonValueKind.String
                                        && SqlScript.Classify(sql.GetString()) is SqlStatementPlan plan)
                                    {
                                        commits |= plan.Kind == SqlStatementKind.Commit || !transactionOpen && plan.Kind == SqlStatementKind.Write;
                                        transactionOpen = plan.Kind == SqlStatementKind.Begin || transactionOpen && plan.Kind is not (SqlStatementKind.Rollback or SqlStatementKind.Commit);
                                    }
                                }
                            }

                            long? requestId = call.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) ? number : null;
                            if (admit is not null && !state.TryAdmit(admit, out var retryAfter))
                            {
                                refused = (requestId, retryAfter, snapshot.Transaction);
                            }
                            else
                            {
                                state.Sent('R', kind == "begin", commits, requestId);
                            }
                        }
                        else
                        {
                            using var answer = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                            answered = answer.RootElement.TryGetProperty("transaction", out var transaction) && transaction.GetBoolean();
                        }
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ConnectionResetException or ObjectDisposedException or InvalidDataException or JsonException)
                {
                    break;
                }
                finally
                {
                    if (input is not null)
                    {
                        input.Progress = null;
                    }

                    if (answers is not null)
                    {
                        answers.Progress = null;
                    }

                    await expiry.DisposeAsync().ConfigureAwait(false);
                    await closing.ConfigureAwait(false);
                    if (input is not null)
                    {
                        deadline.Dispose();
                    }
                }

                async Task ExpireAsync()
                {
                    beginClose();
                    if (input is not null)
                    {
                        try
                        {
                            await state.DrainAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            var pending = state.Pending;
                            if (pending.Commits)
                            {
                                to.Abort();
                                await state.EndAsync(() => ReportAsync(from, SqlErrors.WriteOutcomeUnknown, SqlErrors.OutcomeUnknown(home).Message, pending.Id, cancellationToken)).ConfigureAwait(false);
                            }

                            to.Abort();
                            return;
                        }
                    }
                    else
                    {
                        var pending = state.Pending;
                        var error = pending.Commits ? SqlErrors.OutcomeUnknown(home) : HomeSessions.Unreachable(home);
                        from.Abort();
                        await state.EndAsync(() => ReportAsync(to, error.Code, error.Message, pending.Id, cancellationToken)).ConfigureAwait(false);
                        return;
                    }

                    var reason = deadline.IsPartial ? "incomplete message timeout exceeded"
                        : deadline.IsTransaction ? SqlErrors.TransactionExpired.Message : "session idle timeout exceeded";
                    await CloseAsync(from, WebSocketCloseStatus.PolicyViolation, reason, cancellationToken).ConfigureAwait(false);
                    await CloseAsync(to, WebSocketCloseStatus.PolicyViolation, reason, cancellationToken).ConfigureAwait(false);
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
                    var client = input is null ? to : from;
                    await clientSends.WaitAsync(sending.Token).ConfigureAwait(false);
                    try
                    {
                        if (refused is { } busy)
                        {
                            var error = new SqlError(SqlErrors.TenantBusy, $"organization request budget exceeded; retry after {busy.RetryAfter}s");
                            var reply = new SqlSocketError(busy.Id, "error", SqlBatch.Body(new SqlBatchFailure(error, 0), "").Error, busy.Transaction);
                            await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(reply, SqlJsonContext.Default.SqlSocketError), WebSocketMessageType.Text, true, sending.Token)
                                .ConfigureAwait(false);
                            continue;
                        }

                        await to.SendAsync(message.GetBuffer().AsMemory(0, (int)message.Length), read.MessageType, true, sending.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        clientSends.Release();
                    }

                    if (answered is { } transaction)
                    {
                        state.Answered('R', transaction);
                    }
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ConnectionResetException or ObjectDisposedException or InvalidDataException or JsonException)
                {
                    break;
                }
            }

            beginClose();
            if (input is null && state.Pending.Commits && !cancellationToken.IsCancellationRequested)
            {
                var pending = state.Pending;
                await state.EndAsync(() => ReportAsync(to, SqlErrors.WriteOutcomeUnknown, SqlErrors.OutcomeUnknown(home).Message, pending.Id, cancellationToken)).ConfigureAwait(false);
            }
            else
            {
                await CloseAsync(to, WebSocketCloseStatus.EndpointUnavailable, "the relayed session ended", cancellationToken).ConfigureAwait(false);
            }
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
