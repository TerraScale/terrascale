using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>DELETE /v1/management/organizations/{organization}</c>: deletes an organization without projects.</summary>
public sealed class DeleteOrganizationEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ManagementRoute.Organization);
        Name("deleteOrganization");
        Summary("Deletes an organization");
        Description("Fails with 409 while the organization still has projects.");
        Tags(ManagementRoute.Tag);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(ApiKeyScopes.Management);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.NoContent(await management.DeleteOrganizationAsync(ManagementRoute.Value(HttpContext, "organization"), cancellationToken));
}
