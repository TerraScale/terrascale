using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>The organization to switch to.</summary>
/// <param name="OrganizationId">Id of an existing organization.</param>
public sealed record SwitchOrganizationRequest(string? OrganizationId);

/// <summary><c>PUT /v1/management/users/{user}/current-organization</c>: switches a user to an organization.</summary>
public sealed class SetCurrentOrganizationEndpoint(IManagementService management) : Endpoint<SwitchOrganizationRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(ManagementRoute.CurrentOrganization);
        Name("setCurrentOrganization");
        Summary("Switches a user to an organization");
        Description("user is the caller-supplied identity (for example the ts-auth subject); TerraBase does not authenticate users or check membership.");
        Tags(ManagementRoute.Tag);
        Produces<Organization>();
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.Management);
        Metadata(ListenerMetadata.Client);
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
