using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Http;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sql;

public sealed record SqlSocketInput(string? Project, string? Database);

internal sealed class SqlSocketEndpoint(SqlSockets sockets, DatabaseAccess access)
    : Endpoint<SqlSocketInput, Result<SocketClosed, Validation, Unauthorized, Forbidden, NotFound, Unavailable, SqlRefusal>>
{
    public override void Configure()
    {
        Verbs(HttpMethods.Get, HttpMethods.Connect);
        Route(SqlRoute.Database + "/sql/ws");
        Name("connectSql");
        Summary("Runs SQL over a WebSocket");
        Description("Subprotocol terrascale.sql.v2 speaks SqlWsClientFrame/SqlWsServerFrame with bookmarks and fast/strong transactions; terrascale.sql.v1 or "
            + "no subprotocol speaks the v1 frames. Authenticate with the Authorization header or an {\"type\":\"auth\",\"token\":\"…\"} first frame within 5 seconds.");
        Tags("Queries");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<Result<SocketClosed, Validation, Unauthorized, Forbidden, NotFound, Unavailable, SqlRefusal>> HandleAsync(
        SqlSocketInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return new Validation("connect with a WebSocket upgrade", "websocket_required");
        }

        return SqlRunner.Causality(HttpContext.Request) switch
        {
            SqlCausality causality => HttpContext.Caller switch
            {
                AnonymousCaller => await sockets.ServeAsync(HttpContext, request.Project, request.Database, null, causality, cancellationToken),
                PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller =>
                    await access.DataAsync(HttpContext.Caller, request.Project, request.Database, cancellationToken) switch
                    {
                        ResolvedDatabase target => await sockets.ServeAsync(HttpContext, request.Project, request.Database, target, causality, cancellationToken),
                        Validation invalid => invalid,
                        Unauthorized unauthorized => unauthorized,
                        Forbidden forbidden => forbidden,
                        NotFound missing => missing,
                        Unavailable unavailable => unavailable,
                    },
            },
            SqlRefusal refusal => refusal,
        };
    }
}

internal sealed class SqlSockets(SqlRunner runner, DatabaseAccess access, CallerAuthentication authentication, IClusterCoordinator coordinator, TimeProvider time)
{
    public const string LegacyProtocol = "terrascale.sql.v1";

    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    public async ValueTask<SocketClosed> ServeAsync(
        HttpContext context, string? project, string? database, ResolvedDatabase? target, SqlCausality causality, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var offered = context.WebSockets.WebSocketRequestedProtocols;
        var v2 = offered.Contains(SqlWsFrameTypes.Subprotocol, StringComparer.Ordinal);
        var protocol = v2 ? SqlWsFrameTypes.Subprotocol : offered.Contains(LegacyProtocol, StringComparer.Ordinal) ? LegacyProtocol : null;
        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = protocol,
            KeepAliveInterval = KeepAlive,
        });
        await using var connection = new SqlSocket(socket, v2, project, database, runner, access, authentication, coordinator, time);
        await connection.RunAsync(target, causality, cancellationToken);
        return new SocketClosed();
    }
}

public sealed class SocketClosed : IResult
{
    public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;
}
