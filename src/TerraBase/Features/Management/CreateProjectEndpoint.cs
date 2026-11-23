using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class CreateProjectEndpoint(ManagementService management, CatalogStore catalog, TimeProvider time)
    : Endpoint<NameInput, Result<Created<ProjectBody>, Validation, Unauthorized, NotFound, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Post(ManagementRoute.Projects);
        Name("createProject");
        Summary("Creates a project in an organization the calling user owns");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<Created<ProjectBody>, Validation, Unauthorized, NotFound, Conflict, Unavailable>> HandleAsync(
        NameInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await management.OwnedOrganizationAsync(HttpContext.Caller, request.Organization, cancellationToken) switch
        {
            OrganizationRecord organization => ManagementService.DisplayName(request.Name) switch
            {
                string name => await catalog.CreateProjectAsync(new ProjectRecord(Ulid.NewUlid(time.GetUtcNow()), organization.Id, name, time.GetUtcNow(), null), cancellationToken) switch
                {
                    ProjectRecord project => new Created<ProjectBody>(string.Concat("/v1/projects/", project.Id.ToString()), ProjectBody.From(project)),
                    Conflict conflict => conflict,
                    Unavailable unavailable => unavailable,
                },
                Validation invalid => invalid,
            },
            Unauthorized unauthorized => unauthorized,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
