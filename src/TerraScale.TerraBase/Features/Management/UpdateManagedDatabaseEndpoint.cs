using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>PATCH …/databases/{database}</c>: sets a database's replica regions (docs/cross-region.md).</summary>
public sealed class UpdateManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options) : Endpoint<DatabaseRegionsRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(ManagementRoute.Database);
        Name("updateManagedDatabase");
        Summary("Sets a database's replica regions");
        Description("Body {\"replicaRegions\":[…]}, the complete new list of regions besides the home region that hold a full copy and serve the database. homeRegion may be sent but cannot change; a name cannot change. Sent to the database's home region; any other answers 421 wrong_region with X-TerraBase-Database-Home. An added region takes the database once it registers the project under the same ids; a removed region stops serving it and drops its copy." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagedDatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(DatabaseRegionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (user, organization, project, id) = (ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"));
        if (await management.GetDatabaseAsync(user, organization, project, id, cancellationToken) is not { } database)
        {
            return ManagementRoute.Error(ManagementError.NotFound);
        }

        var home = database.Home(options.Region);
        if (home != options.Region)
        {
            return DatabaseRegions.WrongRegion(home, database.Regions(options.Region));
        }

        var invalid = request.Name is not null ? new Validation("a database cannot be renamed")
            : request.HomeRegion is { } requested && requested != home ? new Validation("homeRegion cannot change")
            : request.ReplicaRegions is not { } replicas ? new Validation("replicaRegions is required")
            : DatabaseRegions.Check(options, home, replicas);
        if (invalid is not null)
        {
            return ErrorResponse.From(invalid);
        }

        var result = await management.SetDatabaseRegionsAsync(user, organization, project, id, request.ReplicaRegions!, cancellationToken);
        return result.Value is { } value ? TypedResults.Ok(ManagedDatabaseBody.From(value, options.Region)) : ManagementRoute.Error(result.Error);
    }
}
