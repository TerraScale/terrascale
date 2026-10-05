using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>GET /v1/management/current-organization</c>: reads the organization the acting user last switched to.</summary>
public sealed class GetCurrentOrganizationEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.CurrentOrganization);
        Name("getCurrentOrganization");
        Summary("Reads the organization the acting user last switched to");
        Description("404 when the user never switched, or is no longer a member of that organization. " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<Organization>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.GetCurrentOrganizationAsync(ManagementRoute.UserId(HttpContext), cancellationToken) is { } organization
            ? TypedResults.Ok(organization)
            : TypedResults.NotFound();
}
