using System.Globalization;
using System.Net.WebSockets;

namespace TerraScale.Platform.Features.Shapes.Endpoints;

public sealed class GetShapeHintsEndpoint(ShapeReader reader, ShapeNotifier notifier, TimeProvider timeProvider)
    : EndpointWithoutRequest<Result<ShapeSocketClosed, ShapeRefetch, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    private static readonly TimeSpan AuthWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    public override void Configure()
    {
        Get(ShapeParameters.Route + "/ws");
        Verbs(HttpMethods.Get, HttpMethods.Connect);
        Name("connectShapeHints");
        Summary("Waits for shape changes over a WebSocket");
        Description("Requires sql:read. Offer terrascale.shape.v1. Send handle and a decimal offset, plus the shape's where and columns, in the URL. Authenticate with "
            + "Authorization on the upgrade or a first JSON text frame {\"type\":\"auth\",\"token\":\"…\"} within five seconds. The server sends {\"type\":\"ready\"}, "
            + "then {\"type\":\"new_data\",\"handle\",\"offset\",\"next_offset\",\"manifest\"} hints without rows. A stale handle sends must_refetch and closes.");
        Tags("Live");
        AllowAnonymous();
    }

    public override async ValueTask<Result<ShapeSocketClosed, ShapeRefetch, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        CancellationToken cancellationToken)
    {
        var query = HttpContext.Request.Query;
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return new Validation("a WebSocket upgrade offering terrascale.shape.v1 is required");
        }

        if (ShapeParameters.Only(query, "handle", "offset", "where", "columns") is { } unknown)
        {
            return unknown;
        }

        var handle = ShapeParameters.Parameter(query, "handle");
        var offset = ShapeParameters.Parameter(query, "offset") is { } text ? ShapeOffset.ParseDecimal(text) : null;
        if (handle is null || ShapeParameters.CheckHandle(handle) is not null || offset is not { } start)
        {
            return new Validation("handle and a decimal offset are required");
        }

        var route = new ShapeRouteRequest(RouteValue("project"), RouteValue("database"), RouteValue("table"));
        return ShapeQuery.From(route.Table, query) switch
        {
            ShapeQuery shape => await ConnectAsync(route, shape, handle, start, cancellationToken),
            Validation invalid => invalid,
        };
    }

    private async ValueTask<Result<ShapeSocketClosed, ShapeRefetch, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> ConnectAsync(
        ShapeRouteRequest route,
        ShapeQuery query,
        string handle,
        long offset,
        CancellationToken cancellationToken)
    {
        if (HttpContext.Request.Headers.Authorization.Count > 0 || HttpContext.Request.Headers.ContainsKey(PlatformOptions.PeerCredentialHeader))
        {
            return await reader.ResolveAsync(HttpContext, route, query, cancellationToken) switch
            {
                ResolvedShape resolved when resolved.Shape.Handle != handle => ShapeReader.Refetch(resolved.Shape, "shape lineage changed"),
                ResolvedShape resolved => await StreamAsync(await AcceptAsync(), route, resolved, offset, cancellationToken),
                Validation invalid => invalid,
                Unauthorized unauthorized => unauthorized,
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            };
        }

        var socket = await AcceptAsync();
        if (await FirstFrameTokenAsync(socket, cancellationToken) is not { } token)
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
            return ShapeSocketClosed.Instance;
        }

        HttpContext.Request.Headers.Authorization = "Bearer " + token;
        var resolvedAfterAuth = await reader.ResolveAsync(HttpContext, route, query, cancellationToken);
        var refusal = resolvedAfterAuth switch
        {
            ResolvedShape resolved when resolved.Shape.Handle != handle => "must_refetch",
            ResolvedShape => null,
            Validation => "invalid_request",
            Unauthorized => "invalid_key",
            Forbidden => "forbidden",
            NotFound => "not_found",
            Unavailable => "unavailable",
        };
        if (refusal is not null)
        {
            await SendAsync(socket, new ShapeHintFrame("error", Code: refusal, RetryAfter: refusal == "unavailable" ? 1 : null), cancellationToken);
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
            return ShapeSocketClosed.Instance;
        }

        return resolvedAfterAuth switch
        {
            ResolvedShape resolved => await StreamAsync(socket, route, resolved, offset, cancellationToken),
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ShapeSocketClosed> StreamAsync(WebSocket socket, ShapeRouteRequest route, ResolvedShape resolved, long offset, CancellationToken cancellationToken)
    {
        using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = DrainAsync(socket, disconnected);
        var current = resolved;
        var checkedAt = timeProvider.GetUtcNow();
        var open = await SendAsync(socket, new ShapeHintFrame("ready"), disconnected.Token);
        while (open && !disconnected.IsCancellationRequested)
        {
            if (current.Shape.Head > offset)
            {
                var manifest = await reader.ManifestAsync(current.Shape, offset, disconnected.Token) switch
                {
                    ShapeMirrorManifest found => found,
                    ShapeRefetch => new ShapeMirrorManifest(current.Shape.Handle, []),
                    Unavailable => new ShapeMirrorManifest(current.Shape.Handle, []),
                };
                var next = current.Shape.Head.ToString(CultureInfo.InvariantCulture);
                open = await SendAsync(socket, new ShapeHintFrame("new_data", current.Shape.Handle, offset.ToString(CultureInfo.InvariantCulture), next, manifest), disconnected.Token);
                offset = current.Shape.Head;
                continue;
            }

            await notifier.WaitAsync(current.Shape.Database, Poll, disconnected.Token);
            if (disconnected.IsCancellationRequested)
            {
                break;
            }

            var recheck = timeProvider.GetUtcNow() - checkedAt >= Recheck;
            var refreshed = await reader.ResolveAsync(HttpContext, route, current.Query, disconnected.Token);
            var step = refreshed switch
            {
                ResolvedShape next when next.Shape.Handle != current.Shape.Handle => "must_refetch",
                ResolvedShape => null,
                Validation => "must_refetch",
                Unauthorized => "invalid_key",
                Forbidden => "forbidden",
                NotFound => "must_refetch",
                Unavailable => recheck ? "unavailable" : null,
            };
            if (step is not null)
            {
                _ = await SendAsync(socket, new ShapeHintFrame("error", Code: step, RetryAfter: step == "unavailable" ? 1 : null), disconnected.Token);
                break;
            }

            current = refreshed switch
            {
                ResolvedShape next => next,
                Validation or Unauthorized or Forbidden or NotFound or Unavailable => current,
            };
            if (recheck)
            {
                checkedAt = timeProvider.GetUtcNow();
            }
        }

        await disconnected.CancelAsync();
        await receive;
        await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
        return ShapeSocketClosed.Instance;
    }

    private async Task<WebSocket> AcceptAsync() =>
        await HttpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { SubProtocol = ShapeHeaders.HintProtocol });

    private string RouteValue(string name) =>
        HttpContext.Request.RouteValues[name] as string ?? throw new InvalidOperationException($"the route has no {name} value");

    private static async Task<string?> FirstFrameTokenAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AuthWindow);
        var buffer = new byte[16 * 1024];
        var length = 0;
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(length), deadline.Token);
                if (received.MessageType != WebSocketMessageType.Text || (length += received.Count) >= buffer.Length)
                {
                    return null;
                }

                if (received.EndOfMessage)
                {
                    break;
                }
            }
        }
        catch (WebSocketException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return TerraBaseAnswers.Parse(buffer.AsSpan(0, length), ShapesJsonContext.Default.ShapeHintAuth) is { Type: "auth", Token: { Length: > 0 and <= 8192 } token }
            && !token.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character))
            ? token
            : null;
    }

    private static async Task<bool> SendAsync(WebSocket socket, ShapeHintFrame frame, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, ShapesJsonContext.Default.ShapeHintFrame);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            return true;
        }
        catch (WebSocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task DrainAsync(WebSocket socket, CancellationTokenSource disconnected)
    {
        var open = true;
        var buffer = new byte[1024];
        while (open && socket.State == WebSocketState.Open)
        {
            open = await ReceiveOnceAsync(socket, buffer, disconnected.Token);
        }

        await disconnected.CancelAsync();
    }

    private static async Task<bool> ReceiveOnceAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken);
            return received.MessageType != WebSocketMessageType.Close;
        }
        catch (WebSocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            socket.Dispose();
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await socket.CloseOutputAsync(socket.State == WebSocketState.CloseReceived ? WebSocketCloseStatus.NormalClosure : status, "shape hint stream ended", deadline.Token);
        }
        catch (WebSocketException)
        {
            socket.Abort();
        }
        catch (OperationCanceledException)
        {
            socket.Abort();
        }
        finally
        {
            socket.Dispose();
        }
    }
}
