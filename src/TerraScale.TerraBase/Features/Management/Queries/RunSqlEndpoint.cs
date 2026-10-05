using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Queries;

/// <summary><c>POST …/databases/{database}/sql</c>: runs a read-only SQL statement.</summary>
public sealed class RunSqlEndpoint(IManagementService management, IQueryService queries) : Endpoint<SqlRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Sql);
        Name("runSql");
        Summary("Runs a read-only SQL statement on a database");
        Description(QueriesExtensions.SqlDescription + " Rows stop at the limit or at 8 MiB of JSON; truncated reports either. "
            + "A statement that needs more than the engine's work budget (5 million SQLite instructions or 5 seconds) answers 400 query too expensive. "
            + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<SqlResultBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(SqlRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is not { } database)
        {
            return TypedResults.NotFound();
        }

        var parsed = SqlSubset.Parse(request.Sql);
        if (parsed.Statement is not { } statement)
        {
            return TypedResults.BadRequest(parsed.Error);
        }

        try
        {
            return TypedResults.Ok(await queries.RunAsync(database, statement, cancellationToken));
        }
        catch (QueryCapacityException)
        {
            return TypedResults.BadRequest("query too expensive");
        }
    }
}
