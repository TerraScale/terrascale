using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sql;

/// <summary><c>GET /v1/projects/{project}/databases/{database}/export</c>: downloads the database as a SQL dump.</summary>
internal sealed class ExportDatabaseEndpoint(IStorageEngine engine, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(DatabaseRoute.Template + "/export");
        Name("exportDatabase");
        Summary("Downloads the database as a SQL dump");
        Description("""
            Streams the database as one SQL script: BEGIN, then every user table's CREATE TABLE and its rows as
            INSERT INTO "<table>" VALUES (…) in primary key order, then indexes, then COMMIT, taken from one read
            snapshot. Identifiers are double-quoted, text single-quoted, blobs X'hex'. The same SQL dialect runs it
            back into a fresh database. Needs sql:read. The database's home region serves the export.
            """);
        Tags("Queries");
        Produces<byte[]>(StatusCodes.Status200OK, "application/sql");
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.SqlRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (DatabaseRoute.CheckParameters(HttpContext) is Validation invalid)
        {
            return ErrorResponse.From(invalid);
        }

        return await DatabaseRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await DumpAsync(database, cancellationToken),
        };
    }

    private async ValueTask<IResult> DumpAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var name = DatabaseRoute.Database(HttpContext);
        var file = EntityIds.IsValid(name) ? "database" : name;
        var response = HttpContext.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.ContentDisposition = $"attachment; filename=\"{file}.sql\"";
        response.ContentType = "application/sql";
        var transaction = await engine.BeginTransactionAsync(database, write: false, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            return ErrorResponse.From(DatabaseRoute.UnknownDatabase);
        }

        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                await response.StartAsync(cancellationToken).ConfigureAwait(false);
                await SqlDump.WriteAsync(transaction, response.BodyWriter, cancellationToken).ConfigureAwait(false);
                return Results.Empty;
            }
            catch (Exception error) when (error is OperationCanceledException or SqlStatementException or QueryCapacityException or StorageBusyException or InvalidOperationException)
            {
                HttpContext.Abort();
                return Results.Empty;
            }
        }
    }
}
