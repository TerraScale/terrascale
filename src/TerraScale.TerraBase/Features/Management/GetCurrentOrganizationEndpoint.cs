using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>GET /v1/management/users/{user}/current-organization</c>: reads the organization a user last switched to.</summary>
public sealed class GetCurrentOrganizationEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.CurrentOrganization);
        Name("getCurrentOrganization");
        Summary("Reads the organization a user last switched to");
        Description("user is the caller-supplied identity (for example the ts-auth subject); TerraBase does not authenticate users. 404 when the user never switched or the organization was deleted.");
        Tags(ManagementRoute.Tag);
        Produces<Organization>();
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.Management);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.GetCurrentOrganizationAsync(ManagementRoute.UserId(HttpContext), cancellationToken) is { } organization
            ? TypedResults.Ok(organization)
            : TypedResults.NotFound();
}
