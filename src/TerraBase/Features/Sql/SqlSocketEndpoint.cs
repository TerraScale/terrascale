using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Connections;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sql;

/// <summary><c>GET /v1/projects/{project}/databases/{database}/sql/ws</c>: runs SQL over a WebSocket.</summary>
internal sealed class SqlSocketEndpoint(IAuthorizationService authorization, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    public const string Protocol = "terrascale.sql.v1";

    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public override void Configure()
    {
        Get(DatabaseRoute.Template + "/sql/ws");
        Name("connectSql");
        Summary("Runs SQL over a WebSocket");
        Description("""
            Upgrades to a WebSocket (subprotocol terrascale.sql.v1 when offered). Authenticate with the same bearer credential and sql:read scope as sql/execute, in one of two ways: an Authorization header on the upgrade, or, where the client cannot set headers (browsers), {"type":"auth","token":"…"} as the first frame within 5 seconds, answered by a result frame. The credential never goes in the URL. The credential is checked again before every frame; a revoked credential or a lost scope closes the socket with 1008. Frames are JSON text of at most 1 MiB (the auth frame at most 16 KiB), answered in order. {"id":1,"type":"execute","statements":[{"sql","args","named"}]} runs 1 to 256 statements exactly as sql/execute does: outside a transaction they run as one transaction, all or nothing; inside one they run in it. {"id":2,"type":"begin"}, {"type":"commit"} and {"type":"rollback"} open and end a transaction of at most 5 seconds; after a failed statement the transaction only accepts rollback, and commit rolls it back. Answers echo the id: {"id","type":"result","results":[{"columns","rows","rows_affected","last_insert_rowid"}],"transaction"} or {"id","type":"error","error":{"code","message","statement","sqlstate"},"transaction"} with the same codes and SQLSTATEs as sql/execute and the Postgres wire; transaction tells whether a transaction is still open. A refused auth frame answers an unauthorized, forbidden_scope or wrong_region error and closes with 1008. In a replica region of the database, reads outside a transaction and READ ONLY transactions are answered from the local replica; writes and other transactions run at the home, and a socket that wrote reads its own writes. Binary frames close the socket with 1003, oversized frames with 1009, and a restarting server closes it with 1001.
            """);
        Tags("Queries");
        Produces(StatusCodes.Status101SwitchingProtocols);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Client, new AuthorizeAttribute(ApiKeyScopes.SqlRead), new Management.DatabaseRegions.ReplicaReads());
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (DatabaseRoute.CheckParameters(HttpContext) is Validation invalid)
        {
            return ErrorResponse.From(invalid);
        }

        if (HttpContext.Request.Headers.Authorization.Count == 0)
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
        return await DatabaseRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext when !HttpContext.WebSockets.IsWebSocketRequest => Upgrade(),
            TenantContext database => await ServeAsync(database, canWrite, cancellationToken),
        };
    }

    private static IResult Upgrade() => ErrorResponse.From(new Validation("connect with a WebSocket upgrade", "websocket_required"));

    private async ValueTask<IResult> ServeAsync(TenantContext? database, bool canWrite, CancellationToken cancellationToken)
    {
        HttpContext.Features.Get<QosRequestLease>()?.Lease.Dispose();
        var offered = HttpContext.WebSockets.WebSocketRequestedProtocols.Contains(Protocol, StringComparer.Ordinal);
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = offered ? Protocol : null,
            KeepAliveInterval = KeepAlive,
            KeepAliveTimeout = KeepAlive,
        });
        using var connection = new SqlSocket(HttpContext, socket);
        try
        {
            await connection.RunAsync(database, canWrite, cancellationToken);
        }
        catch (Exception error) when (error is OperationCanceledException or System.Net.WebSockets.WebSocketException or ConnectionResetException)
        {
        }

        return TypedResults.Empty;
    }
}
