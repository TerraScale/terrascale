using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
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
        Description("The branch is a new database in the same project holding a consistent copy of the source taken now; later writes to either are independent. name follows the database name rules.");
        Tags(ManagementRoute.Tag);
        Produces<ManagedDatabase>(StatusCodes.Status201Created);
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(ApiKeyScopes.Management);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await management.BranchDatabaseAsync(ManagementRoute.Value(HttpContext, "organization"), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"), ManagementRoute.DatabaseName(request), cancellationToken);
        return result.Value is { } value
            ? TypedResults.Created($"{ManagementRoute.Organizations}/{ManagementRoute.Value(HttpContext, "organization")}/projects/{value.ProjectId}/databases/{value.Id}", value)
            : ManagementRoute.Error(result.Error);
    }
}
