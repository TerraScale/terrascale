using TerraBase.Configuration;
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
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await DatabaseRegions.ElsewhereAsync(HttpContext, management, options.Region) is { } elsewhere)
        {
            return elsewhere;
        }

        var result = await management.BranchDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"), ManagementRoute.DatabaseName(request), cancellationToken);
        return result.Value is { } value
            ? TypedResults.Created(ManagementRoute.CreatedDatabasePath(HttpContext, value), ManagedDatabaseBody.From(value, Listeners.RegionOf(HttpContext)))
            : ManagementRoute.Error(result.Error);
    }
}
