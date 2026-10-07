using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>The organization to switch to.</summary>
/// <param name="OrganizationId">Id of an existing organization.</param>
public sealed record SwitchOrganizationRequest(string? OrganizationId);

/// <summary><c>PUT /v1/management/current-organization</c>: switches the acting user to one of their organizations.</summary>
public sealed class SetCurrentOrganizationEndpoint(IManagementService management) : Endpoint<SwitchOrganizationRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(ManagementRoute.CurrentOrganization);
        Name("setCurrentOrganization");
        Summary("Switches the acting user to an organization");
        Description(ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<Organization>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(SwitchOrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.OrganizationId))
        {
            return TypedResults.BadRequest("organizationId is required");
        }

        return ManagementRoute.Ok(await management.SetCurrentOrganizationAsync(ManagementRoute.UserId(HttpContext), request.OrganizationId, cancellationToken));
    }
}
