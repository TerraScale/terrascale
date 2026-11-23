using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union GetOrganizationResponse(OrganizationBody, CredentialRejected, ResourceNotFound, BackendUnavailable);

internal sealed class GetOrganizationEndpoint(ManagementService management)
    : Endpoint<RouteInput, GetOrganizationResponse>
{
    public override void Configure()
    {
        Get(ManagementRoute.Organization);
        Name("getOrganization");
        Summary("Reads an organization the calling user owns");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<GetOrganizationResponse> HandleAsync(RouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await management.OwnedOrganizationAsync(HttpContext.Caller, request.Organization, cancellationToken);
        return outcome switch
        {
            OrganizationRecord organization => OrganizationBody.From(organization),
            CredentialRejected unauthorized => unauthorized,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
