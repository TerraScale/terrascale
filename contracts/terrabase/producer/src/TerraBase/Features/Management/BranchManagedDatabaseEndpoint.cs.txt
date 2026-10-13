using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>POST …/databases/{database}/branches</c>: creates a database as a snapshot copy of another.</summary>
public sealed class BranchManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Database + "/branches");
        Name("branchDatabase");
        Summary("Branches (clones) a database");
        Description("The branch is a new database in the same project holding a consistent copy of the source taken now; later writes to either are independent. name follows the database name rules. Sent to the source database's home region; any other answers 421 wrong_region with X-TerraBase-Database-Home." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var placement = await DatabaseRegions.ElsewhereAsync(HttpContext, management, options.Region);
        if (placement is not Done)
        {
            return placement switch
            {
                Done => throw new System.Diagnostics.UnreachableException(),
                DatabaseRegions.Misdirected elsewhere => elsewhere,
                NotFound error => ManagementRoute.DatabaseNotFound(error),
                Unavailable error => ErrorResponse.From(error),
            };
        }

        return ManagementRoute.CheckDatabaseName(request) switch
        {
            Validation error => ErrorResponse.From(error),
            string name => await management.BranchDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"), name, cancellationToken) switch
            {
                ManagedDatabase value => TypedResults.Created(ManagementRoute.CreatedDatabasePath(HttpContext, value), ManagedDatabaseBody.From(value, Listeners.RegionOf(HttpContext))),
                NotFound error => ManagementRoute.DatabaseNotFound(error),
                Forbidden error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                QuotaExceeded error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}
