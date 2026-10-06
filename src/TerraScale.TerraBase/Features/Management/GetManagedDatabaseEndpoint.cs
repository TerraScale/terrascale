using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>GET …/databases/{database}</c>: reads a database.</summary>
public sealed class GetManagedDatabaseEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Database);
        Name("getManagedDatabase");
        Summary("Reads a database");
        Description(ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagedDatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.GetDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"), cancellationToken) is { } database
            ? TypedResults.Ok(ManagedDatabaseBody.From(database, Listeners.RegionOf(HttpContext)))
            : TypedResults.NotFound();
}
