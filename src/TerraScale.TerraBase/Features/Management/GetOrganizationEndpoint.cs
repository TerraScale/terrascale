using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET /v1/management/organizations/{organization}</c>: reads an organization.</summary>
public sealed class GetOrganizationEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Organization);
        Name("getOrganization");
        Summary("Reads an organization");
        Description(ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<Organization>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.GetOrganizationAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), cancellationToken) is { } organization
            ? TypedResults.Ok(organization)
            : TypedResults.NotFound();
}
