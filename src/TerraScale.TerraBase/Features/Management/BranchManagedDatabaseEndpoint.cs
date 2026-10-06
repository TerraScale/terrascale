using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>POST …/databases/{database}/branches</c>: creates a database as a snapshot copy of another.</summary>
public sealed class BranchManagedDatabaseEndpoint(IManagementService management) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Database + "/branches");
        Name("branchManagedDatabase");
        Summary("Branches (clones) a database");
        Description("The branch is a new database in the same project holding a consistent copy of the source taken now; later writes to either are independent. name follows the database name rules." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagedDatabaseBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await management.BranchDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"), ManagementRoute.DatabaseName(request), cancellationToken);
        return result.Value is { } value
            ? TypedResults.Created($"{ManagementRoute.Organizations}/{ManagementRoute.OrganizationId(HttpContext)}/projects/{value.ProjectId}/databases/{value.Id}", ManagedDatabaseBody.From(value, Listeners.RegionOf(HttpContext)))
            : ManagementRoute.Error(result.Error);
    }
}
