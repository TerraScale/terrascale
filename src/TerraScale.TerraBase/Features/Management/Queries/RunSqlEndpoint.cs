using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

/// <summary><c>POST …/databases/{database}/sql</c>: runs a read-only SQL statement.</summary>
public sealed class RunSqlEndpoint(IManagementService management, IQueryService queries) : Endpoint<SqlRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Sql);
        Name("runSql");
        Summary("Runs a read-only SQL statement on a database");
        Description(QueriesExtensions.SqlDescription + " Rows stop at the limit or at 8 MiB of JSON; truncated reports either. "
            + "A statement that needs more than the engine's work budget (5 million SQLite instructions or 5 seconds) answers 400 query too expensive. "
            + ProjectApiRoute.IdentityDescription);
        Tags("Queries");
        Produces<SqlResultBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(SqlRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is not { } database)
        {
            return ErrorResponse.From(new NotFound());
        }

        return SqlSubset.Parse(request.Sql) switch
        {
            Validation error => ErrorResponse.From(error),
            SqlStatement statement => await queries.RunAsync(database, statement, cancellationToken) switch
            {
                SqlResultBody result => TypedResults.Ok(result),
                Validation error => ErrorResponse.From(error),
            },
        };
    }
}
