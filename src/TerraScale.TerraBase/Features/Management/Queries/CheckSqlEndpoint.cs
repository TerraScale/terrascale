using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

/// <summary><c>POST …/databases/{database}/sql/check</c>: validates and explains a SQL statement without running it.</summary>
public sealed class CheckSqlEndpoint(IManagementService management, IQueryService queries) : Endpoint<SqlRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Sql + "/check");
        Name("checkSql");
        Summary("Validates and explains a SQL statement");
        Description(QueriesExtensions.SqlDescription + " Invalid SQL returns ok false and the error. suggestions lists filtered or sorted fields that no index of the collection leads with." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Queries");
        Produces<SqlCheckBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(SqlRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var found = await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        if (found is not TenantContext database)
        {
            return found switch
            {
                NotFound => ErrorResponse.From(new NotFound()),
                Unavailable error => ErrorResponse.From(error),
                TenantContext => throw new UnreachableException(),
            };
        }

        // SQL outside the subset is a check result, not an error.
        return SqlSubset.Parse(request.Sql) switch
        {
            Validation invalid => TypedResults.Ok(new SqlCheckBody(false, invalid.Message, null, false, [], [], [])),
            SqlStatement statement => await queries.CheckAsync(database, statement, cancellationToken) switch
            {
                SqlCheckBody check => TypedResults.Ok(check),
                NotFound error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
            },
        };
    }
}
