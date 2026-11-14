using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replicas;
using TerraBase.Features.Regions;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;
using System.Net.WebSockets;

namespace TerraBase.Features.Sql;

internal sealed class SqlSocketEndpoint(
    IHostApplicationLifetime lifetime,
    ISqlService sql,
    IAuthorizationService authorization,
    ITenantDirectory tenants,
    TerraBaseOptions options,
    IMemoryBudget memory,
    TimeProvider time,
    IQosAdmission admission,
    QosSessions sessions,
    IUsageMeter usage,
    IRequestLogs logs,
    RegionalIngress ingress) : EndpointWithoutRequest<IResult>
{
    public const string Protocol = "terrascale.sql.v1";

    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    public override void Configure()
    {
        Get(DatabaseRoute.Template + "/sql/ws");
        Verbs(HttpMethods.Get, "CONNECT");
        Name("connectSql");
        Summary("Runs SQL over a WebSocket");
        Description("""
            Upgrades to a WebSocket (subprotocol terrascale.sql.v1 when offered). Authenticate with the same bearer credential and sql:read scope as sql/execute, in one of two ways: an Authorization header on the upgrade, or, where the client cannot set headers (browsers), {"type":"auth","token":"…"} as the first frame within 5 seconds, answered by a result frame. The credential never goes in the URL. The credential is checked again before every frame; a revoked credential or a lost scope closes the socket with 1008. Frames are JSON text of at most 1 MiB (the auth frame at most 16 KiB), answered in order. {"id":1,"type":"execute","statements":[{"sql","args","named"}]} runs 1 to 256 statements exactly as sql/execute does: outside a transaction they run as one transaction, all or nothing; inside one they run in it. {"id":2,"type":"begin"}, {"type":"commit"} and {"type":"rollback"} open and end a transaction whose five-second idle and total limits apply only while waiting for the next message; a message in progress uses its message-progress budget, and accepted statements and commits finish under their execution budgets; after a failed statement the transaction only accepts rollback, and commit rolls it back. Answers echo the id: {"id","type":"result","results":[{"columns","rows","rows_affected","last_insert_rowid"}],"transaction"} or {"id","type":"error","error":{"code","message","statement","sqlstate"},"transaction"} with the same codes and SQLSTATEs as sql/execute and the Postgres wire; transaction tells whether a transaction is still open. A refused auth frame answers an unauthorized or forbidden_scope error and closes with 1008. An SDK needs no knowledge of the database region. A region that does not hold the database authenticates the client before proxying to a region that holds it, which authenticates again. This adds one inter-region round trip (about 170 ms between the current regions) and passes request admission at both regions. A region that holds the database serves supported reads locally; writes follow the SQL router, and a socket that wrote reads its own writes. A writer move closes a pinned session with 1012 and rolls back its open transaction. If the proxy leg drops while a commit is in flight, its outcome is unknown; the proxy never replays it. Binary frames close the socket with 1003, oversized frames with 1009, a restarting server closes it with 1001 and a regional writer handoff with 1012, after which the client reconnects. A client that keeps sending after the 1012 instead of answering it gets no answer to those frames and stays connected until the node stops; close the socket when you read a 1012. While that handoff holds the region's ingress, and on a node that was drained for a handoff (the old writer after the hold, or a follower drained for retirement) until that node stops, an upgrade answers 503 with Retry-After: 1. After a 1012, or a 1001 within the server's 2-second shutdown drain, the server still sends every answer queued before the close and runs no frame that arrives later; after a close for a client fault (1003, 1008, 1009, 1011) a client that does not answer within 2 seconds is disconnected.
            """);
        Tags("Queries");
        Produces(StatusCodes.Status101SwitchingProtocols);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Client, new AuthorizeAttribute(ApiKeyScopes.SqlRead), new Management.DatabaseRegions.ReplicaReads(), new RegionalIngress.RefusedWhileHeld());
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (DatabaseRoute.CheckParameters(HttpContext) is Validation invalid)
        {
            return ErrorResponse.From(invalid);
        }

        if (HttpContext.Request.Headers.Authorization.Count == 0 && !HttpContext.User.IsOperator())
        {
            return HttpContext.WebSockets.IsWebSocketRequest ? await ServeAsync(null, canWrite: false, cancellationToken) : Upgrade();
        }

        if (HttpContext.User.Identity?.IsAuthenticated != true)
        {
            return ErrorResponse.From(new Unauthorized("the credential is missing, unknown or expired", "invalid_key"));
        }

        if (!(await authorization.AuthorizeAsync(HttpContext.User, HttpContext, ApiKeyScopes.SqlRead)).Succeeded)
        {
            return ErrorResponse.From(new Forbidden("SQL needs the sql:read scope", "forbidden_scope"));
        }

        var canWrite = (await authorization.AuthorizeAsync(HttpContext.User, HttpContext, ApiKeyScopes.SqlWrite)).Succeeded;
        return await DatabaseRoute.TenantAsync(HttpContext, tenants,
            (database, _) => !HttpContext.WebSockets.IsWebSocketRequest
                ? ValueTask.FromResult(Upgrade())
                : ServeAsync(database, canWrite, cancellationToken),
            cancellationToken);
    }

    private static IResult Upgrade() => ErrorResponse.From(new Validation("connect with a WebSocket upgrade", "websocket_required"));

    private async ValueTask<IResult> ServeAsync(TenantContext? database, bool canWrite, CancellationToken cancellationToken)
    {
        HttpContext.ReleaseRequestLease();
        var offered = HttpContext.WebSockets.WebSocketRequestedProtocols.Contains(Protocol, StringComparer.Ordinal);
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = offered ? Protocol : null,
            KeepAliveInterval = KeepAlive,
            KeepAliveTimeout = Timeout.InfiniteTimeSpan,
        });
        HttpContext.RequestServices.GetRequiredService<SqlConnectionMetrics>().Accepted();
        var accepted = HttpContext.Features.GetRequiredFeature<RegionalIngress.Lease>();
        accepted.Dispose();
        using var connection = new SqlSocket(HttpContext, socket, lifetime, sql, authorization, tenants, options, memory, time, admission, sessions, usage, logs, ingress);
        try
        {
            await connection.RunAsync(database, canWrite, accepted.Fence, cancellationToken);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or ConnectionResetException)
        {
        }

        return TypedResults.Empty;
    }
}
