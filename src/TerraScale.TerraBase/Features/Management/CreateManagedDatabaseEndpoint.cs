using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>POST …/projects/{project}/databases</c>: creates an empty database in a project.</summary>
public sealed class CreateManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options) : Endpoint<DatabaseRegionsRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Databases);
        Name("createManagedDatabase");
        Summary("Creates a database in a project");
        Description("name: a lowercase letter, then lowercase letters, digits, _ or -, at most 63 characters; unique within the project. storageName addresses the database in the data API. homeRegion (optional, the called region by default) is the region the database is created and managed in: send the request to that region, any other answers 421 wrong_region with X-TerraBase-Database-Home. replicaRegions (optional) lists the other regions that hold a full copy and serve it too (docs/cross-region.md); each must register the project under the same ids." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagedDatabaseBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(DatabaseRegionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = ManagementRoute.DatabaseName(new NameRequest(request.Name));
        var home = request.HomeRegion ?? options.Region;
        var replicas = request.ReplicaRegions ?? [];
        if (home != options.Region)
        {
            return options.Wan?.Remotes.ContainsKey(home) == true
                ? DatabaseRegions.WrongRegion(home, [home, .. replicas])
                : ErrorResponse.From(new Validation("homeRegion must name a region this deployment replicates with"));
        }

        if (DatabaseRegions.Check(options, home, replicas) is { } invalid)
        {
            return ErrorResponse.From(invalid);
        }

        var result = await management.CreateDatabaseAsync(
            ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), name, cancellationToken, replicas);
        return result.Value is { } value
            ? TypedResults.Created($"{HttpContext.Request.Path}/{value.Id}", ManagedDatabaseBody.From(value, Listeners.RegionOf(HttpContext)))
            : ManagementRoute.Error(result.Error);
    }
}
