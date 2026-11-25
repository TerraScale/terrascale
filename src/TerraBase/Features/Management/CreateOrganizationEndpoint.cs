using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union CreateOrganizationResponse(Created<OrganizationBody>, InvalidInput, CredentialRejected, CallerForbidden, StateConflict, BackendUnavailable);

internal sealed class CreateOrganizationEndpoint(ManagementService management)
    : Endpoint<NameInput, CreateOrganizationResponse>
{
    public override void Configure()
    {
        Post(ManagementRoute.Organizations);
        Name("createOrganization");
        Summary("Creates an organization owned by the calling user");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<CreateOrganizationResponse> HandleAsync(
        NameInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await management.CreateOrganizationAsync(HttpContext.Caller, request.Name, cancellationToken);
        return outcome switch
        {
            OrganizationRecord created => new Created<OrganizationBody>(string.Concat(ManagementRoute.Organizations, "/", created.Id.ToString()), OrganizationBody.From(created)),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
