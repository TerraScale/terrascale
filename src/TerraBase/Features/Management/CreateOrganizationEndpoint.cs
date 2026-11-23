using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class CreateOrganizationEndpoint(ManagementService management)
    : Endpoint<NameInput, Result<Created<OrganizationBody>, Validation, Unauthorized, Conflict, Unavailable>>
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

    public override async ValueTask<Result<Created<OrganizationBody>, Validation, Unauthorized, Conflict, Unavailable>> HandleAsync(
        NameInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await management.CreateOrganizationAsync(HttpContext.Caller, request.Name, cancellationToken) switch
        {
            OrganizationRecord created => new Created<OrganizationBody>(string.Concat(ManagementRoute.Organizations, "/", created.Id.ToString()), OrganizationBody.From(created)),
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }
}
