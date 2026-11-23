using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class GetOrganizationEndpoint(ManagementService management)
    : Endpoint<RouteInput, Result<OrganizationBody, Unauthorized, NotFound, Unavailable>>
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

    public override async ValueTask<Result<OrganizationBody, Unauthorized, NotFound, Unavailable>> HandleAsync(RouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await management.OwnedOrganizationAsync(HttpContext.Caller, request.Organization, cancellationToken) switch
        {
            OrganizationRecord organization => OrganizationBody.From(organization),
            Unauthorized unauthorized => unauthorized,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
