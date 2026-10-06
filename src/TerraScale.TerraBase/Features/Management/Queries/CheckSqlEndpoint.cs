using TerraScale.Endpoints;
using TerraScale.TerraBase.Errors;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Queries;

/// <summary><c>POST …/databases/{database}/sql/check</c>: validates and explains a SQL statement without running it.</summary>
public sealed class CheckSqlEndpoint(IManagementService management, IQueryService queries) : Endpoint<SqlRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Sql + "/check");
        Name("checkSql");
        Summary("Validates and explains a SQL statement");
        Description(QueriesExtensions.SqlDescription + " Invalid SQL returns ok false and the error. suggestions lists filtered or sorted fields that no index of the collection leads with." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<SqlCheckBody>();
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
            return ErrorResponse.From(new NotFound());
        }

        // SQL outside the subset is a check result, not an error.
        return SqlSubset.Parse(request.Sql) switch
        {
            Validation invalid => TypedResults.Ok(new SqlCheckBody(false, invalid.Message, null, false, [], [], [])),
            SqlStatement statement => await queries.CheckAsync(database, statement, cancellationToken) switch
            {
                SqlCheckBody check => TypedResults.Ok(check),
                Validation error => ErrorResponse.From(error),
            },
        };
    }
}
