using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.InternalApi;

public sealed record InternalSqlSocketInput(string? DatabaseId);

internal sealed class InternalSqlSocketEndpoint(SqlSockets sockets, DatabaseAccess access)
    : Endpoint<InternalSqlSocketInput, Result<SocketClosed, Validation, Unauthorized, Forbidden, NotFound, Unavailable, SqlRefusal>>
{
    public override void Configure()
    {
        Verbs(HttpMethods.Get, HttpMethods.Connect);
        Route(InternalRoute.Database + "/sql/ws");
        Name("connectInternalSql");
        Summary("Runs SQL for platform services over a WebSocket on the internal listener");
        Description("Speaks the terrascale.sql.v2 frames against the database named by id; the platform peer credential authorizes every frame.");
        Tags(InternalRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<SocketClosed, Validation, Unauthorized, Forbidden, NotFound, Unavailable, SqlRefusal>> HandleAsync(
        InternalSqlSocketInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return new Validation("connect with a WebSocket upgrade", "websocket_required");
        }

        if (InternalRoute.DatabaseId(request.DatabaseId) is not { } databaseId)
        {
            return new Validation("databaseId must be a canonical uppercase ULID", "invalid_database");
        }

        return SqlRunner.Causality(HttpContext.Request) switch
        {
            SqlCausality causality => await access.DataByIdAsync(HttpContext.Caller, databaseId, cancellationToken) switch
            {
                ResolvedDatabase target => await sockets.ServeAsync(
                    HttpContext, target.Project.Id.ToString(), target.Database.Id.ToString(), target, causality, cancellationToken),
                Validation invalid => invalid,
                Unauthorized unauthorized => unauthorized,
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            SqlRefusal refusal => refusal,
        };
    }
}
