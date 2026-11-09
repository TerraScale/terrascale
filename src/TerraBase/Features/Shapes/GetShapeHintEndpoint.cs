using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Shapes;

internal sealed class GetShapeHintEndpoint(ITenantDirectory tenants, IShapeLineages lineages, IShapeLiveTail live,
    ShapeConnections connections, IMemoryBudget memory, IAuthorizationService authorization,
    TimeProvider time, IHostApplicationLifetime lifetime) : EndpointWithoutRequest<IResult>
{
    internal const string Protocol = "terrascale.shape.v1";

    public override void Configure()
    {
        Get(ShapeRoute.Shape + "/ws");
        Verbs(HttpMethods.Get, "CONNECT");
        Name("connectShapeHints");
        Summary("Waits for shape changes over a WebSocket");
        Description("Requires sql:read. Offer terrascale.shape.v1. Send handle and a decimal offset, plus the shape's where and columns, in the URL. Authenticate with Authorization on the upgrade or a first JSON text frame {\"type\":\"auth\",\"token\":\"…\"} within five seconds. Credentials never enter the URL. The server sends {\"type\":\"ready\"}, then {\"type\":\"new_data\",\"handle\",\"offset\",\"next_offset\",\"manifest\"} hints; the manifest lists sealed ciphertext objects for HTTP CDN catch-up. Hints contain no rows. Keep the last applied HTTP offset; hints may be lost and do not acknowledge delivery. On reconnect, resume HTTP catch-up from that offset. A stale handle sends must_refetch and closes with 1008. Credentials and grants are checked at least every 20 seconds and before hints; expiry or revocation closes with 1008. A slow consumer closes with 1013. Per-node organization limits share 1,024 live polls and sockets; excess authenticated upgrades answer 429 shape_busy and Retry-After: 1. First-frame authentication receives an error frame with retry_after before closing when overloaded. Ordinary organization request-rate limits apply to upgrades and authenticated first frames.");
        Tags("Live");
        Produces(StatusCodes.Status101SwitchingProtocols);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Client, OriginNoStore.Instance, new AuthorizeAttribute(ApiKeyScopes.SqlRead),
            new Management.DatabaseRegions.ReplicaReads());
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.Headers.Authorization.Count > 0)
        {
            if (HttpContext.User.Identity?.IsAuthenticated != true)
            {
                return ErrorResponse.From(new Unauthorized("the credential is missing, unknown or expired", "invalid_key"));
            }

            var tenant = await DatabaseRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken).ConfigureAwait(false);
            if (tenant is not TenantContext)
            {
                return tenant switch
                {
                    Validation error => ErrorResponse.From(error),
                    NotFound error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                    TenantContext => throw new System.Diagnostics.UnreachableException(),
                };
            }
        }

        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return ErrorResponse.From(new Validation("connect with a WebSocket upgrade", "websocket_required"));
        }

        if (DatabaseRoute.CheckParameters(HttpContext, "handle", "offset", "where", "columns") is Validation invalid)
        {
            return ErrorResponse.From(invalid);
        }

        if (!ShapeTables.IsHandle(HttpContext.Request.Query["handle"].ToString())
            || ShapeRoute.Decimal(HttpContext.Request.Query["offset"].ToString()) is not { } offset)
        {
            return ErrorResponse.From(new Validation("handle and a decimal offset are required"));
        }

        HttpContext.Features.Get<QosRequestLease>()?.Lease.Dispose();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        using var preauth = memory.TryReserve(MemoryLane.Foreground, 16 * 1024, null);
        if (preauth is null)
        {
            return ErrorResponse.From(new QuotaExceeded("shape connections are at capacity; retry later", "shape_busy"));
        }

        var watches = new List<IDisposable>();
        IDisposable? connection = null;
        WebSocket? socket = null;
        try
        {
            if (HttpContext.Request.Headers.Authorization.Count == 0)
            {
                socket = await AcceptAsync().ConfigureAwait(false);
                var buffer = new byte[16 * 1024];
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var length = 0;
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer.AsMemory(length), deadline.Token).ConfigureAwait(false);
                    if (received.MessageType != WebSocketMessageType.Text || (length += received.Count) >= buffer.Length)
                    {
                        return TypedResults.Empty;
                    }

                    if (received.EndOfMessage)
                    {
                        break;
                    }
                }

                var auth = JsonSerializer.Deserialize(buffer.AsSpan(0, length), ShapeHintJsonContext.Default.ShapeHintAuth);
                if (auth is not { Type: "auth", Token: { Length: > 0 and <= 8192 } token } || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
                {
                    return TypedResults.Empty;
                }

                HttpContext.Request.Headers.Authorization = "Bearer " + token;
            }

            if (await CheckAccessAsync(stop, watches).ConfigureAwait(false) is not ResolvedShape shape)
            {
                if (socket is not null)
                {
                    await SendAsync(socket, new ShapeHintFrame("error", Code: "invalid_key"), stop.Token).ConfigureAwait(false);
                    return TypedResults.Empty;
                }

                return ErrorResponse.From(new Unauthorized("the credential cannot read this shape", "invalid_key"));
            }

            var tenant = shape.Scope.Database;
            using var scope = QosContext.Enter(tenant, memory);
            using var rate = socket is not null ? HttpContext.RequestServices.GetRequiredService<IQosAdmission>().TryEnter(tenant, out _) : null;
            connection = connections.TryEnter(tenant);
            if (connection is null || socket is not null && rate is null)
            {
                if (socket is not null)
                {
                    await SendAsync(socket, new ShapeHintFrame("error", Code: "shape_busy", RetryAfter: 1), stop.Token).ConfigureAwait(false);
                    return TypedResults.Empty;
                }

                return ErrorResponse.From(new QuotaExceeded("shape connections are at capacity; retry later", "shape_busy"));
            }

            rate?.Dispose();
            if (shape.Lineage.Handle != HttpContext.Request.Query["handle"])
            {
                if (socket is not null)
                {
                    await SendAsync(socket, new ShapeHintFrame("error", Code: "must_refetch"), stop.Token).ConfigureAwait(false);
                    return TypedResults.Empty;
                }

                return ShapeRoute.Refetch(HttpContext, shape.Lineage, "shape lineage changed");
            }

            socket ??= await AcceptAsync().ConfigureAwait(false);
            await SendAsync(socket, new ShapeHintFrame("ready"), stop.Token).ConfigureAwait(false);
            using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var receive = ReceiveCloseAsync(socket, disconnected);
            var announced = ShapeProvisional.Empty;
            try
            {
                while (!disconnected.IsCancellationRequested)
                {
                    var read = await live.WaitAsync(shape.Scope, shape.Lineage, offset, announced, disconnected.Token).ConfigureAwait(false);
                    if (read is not ShapeBody body)
                    {
                        await SendAsync(socket, new ShapeHintFrame("error", Code: read is MustRefetch ? "must_refetch" : "unavailable", RetryAfter: 1), disconnected.Token).ConfigureAwait(false);
                        break;
                    }

                    using (body.Lease)
                    {
                        var current = await CheckAccessAsync(stop, null).ConfigureAwait(false);
                        if (current is not ResolvedShape checkedShape || checkedShape.Lineage != shape.Lineage)
                        {
                            await SendAsync(socket, new ShapeHintFrame("error", Code: "must_refetch"), disconnected.Token).ConfigureAwait(false);
                            break;
                        }

                        var at = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (body.Local is { } local && local != announced && body.NextOffset == at)
                        {
                            await SendAsync(socket, new ShapeHintFrame("provisional", shape.Lineage.Handle, at, at, Local: local), disconnected.Token).ConfigureAwait(false);
                            announced = local;
                            continue;
                        }

                        if (ShapeRoute.Decimal(body.NextOffset) is not { } next || next == offset)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), time, disconnected.Token).ConfigureAwait(false);
                            continue;
                        }

                        var manifest = body.Manifest ?? new ShapeMirrorManifest(shape.Lineage.Handle, []);
                        await SendAsync(socket, new ShapeHintFrame("new_data", shape.Lineage.Handle, at, body.NextOffset, manifest), disconnected.Token).ConfigureAwait(false);
                        offset = next;
                    }
                }
            }
            finally
            {
                disconnected.Cancel();
                await receive.ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or JsonException or QosMemoryException)
        {
        }
        finally
        {
            watches.ForEach(watch => watch.Dispose());
            connection?.Dispose();
            if (socket is not null)
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try
                    {
                        await socket.CloseOutputAsync(socket.State == WebSocketState.CloseReceived ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.PolicyViolation, "shape hint stream ended", deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is WebSocketException or OperationCanceledException)
                    {
                    }
                }

                socket.Dispose();
            }
        }

        return TypedResults.Empty;
    }

    private Task<WebSocket> AcceptAsync() => HttpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
    {
        SubProtocol = HttpContext.WebSockets.WebSocketRequestedProtocols.Contains(Protocol, StringComparer.Ordinal) ? Protocol : null,
        KeepAliveInterval = TimeSpan.FromSeconds(20),
        KeepAliveTimeout = TimeSpan.FromSeconds(20),
    });

    private async ValueTask<LineageResult> CheckAccessAsync(CancellationTokenSource stop, List<IDisposable>? watches)
    {
        var auth = await ApiKeysExtensions.AuthenticateAgainAsync(HttpContext).ConfigureAwait(false);
        if (!auth.Succeeded)
        {
            return new Forbidden("the credential no longer grants access");
        }

        HttpContext.User = auth.Principal;
        if (await UserAuthExtensions.AdmitShapeSocketAsync(HttpContext, stop, watch =>
            {
                if (watches is null)
                {
                    watch.Dispose();
                }
                else
                {
                    watches.Add(watch);
                }
            }).ConfigureAwait(false) is not null
            || !(await authorization.AuthorizeAsync(HttpContext.User, HttpContext, ApiKeyScopes.SqlRead).ConfigureAwait(false)).Succeeded)
        {
            return new Forbidden("the credential no longer grants access");
        }

        return await ShapeRoute.ResolveAsync(HttpContext, tenants, lineages, stop.Token).ConfigureAwait(false) switch
        {
            ResolvedShape shape => shape,
            _ => new Forbidden("the credential no longer grants access"),
        };
    }

    private async Task SendAsync(WebSocket socket, ShapeHintFrame frame, CancellationToken cancellationToken)
    {
        using var admission = memory.TryReserve(MemoryLane.Foreground, 64 * 1024) ?? throw new QosMemoryException();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, ShapeHintJsonContext.Default.ShapeHintFrame);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
    }

    private static async Task ReceiveCloseAsync(WebSocket socket, CancellationTokenSource disconnected)
    {
        try
        {
            var buffer = new byte[1];
            _ = await socket.ReceiveAsync(buffer, disconnected.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            await disconnected.CancelAsync().ConfigureAwait(false);
        }
    }
}
