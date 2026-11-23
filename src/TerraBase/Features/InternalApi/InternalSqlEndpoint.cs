using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Contracts.Http;
using TerraScale.Endpoints;

namespace TerraBase.Features.InternalApi;

public sealed record InternalSqlInput(string? DatabaseId, IReadOnlyList<SqlHttpStatement>? Statements);

internal sealed class InternalSqlEndpoint(SqlFlow flow)
    : Endpoint<InternalSqlInput, Result<ExecuteBody, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Post(InternalRoute.Database + "/sql/execute");
        Name("runInternalSql");
        Summary("Runs SQL for platform services over the internal listener");
        Description("Authenticated with X-TerraBase-Peer-Credential or an X-TerraScale-Caller token; honors TerraScale-Bookmark and TerraScale-Consistency.");
        Tags(InternalRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<ExecuteBody, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        InternalSqlInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InternalRoute.DatabaseId(request.DatabaseId) is { } databaseId
            ? SqlRoute.V2(await flow.ExecuteByIdAsync(HttpContext, databaseId, request.Statements, cancellationToken))
            : new Validation("databaseId must be a canonical uppercase ULID", "invalid_database");
    }
}
