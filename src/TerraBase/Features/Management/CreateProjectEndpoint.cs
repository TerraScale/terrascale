using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union CreateProjectResponse(Created<ProjectBody>, InvalidInput, CredentialRejected, ResourceNotFound, StateConflict, BackendUnavailable);

internal sealed class CreateProjectEndpoint(ManagementService management, CatalogStore catalog, TimeProvider time)
    : Endpoint<NameInput, CreateProjectResponse>
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

    public override async ValueTask<CreateProjectResponse> HandleAsync(
        NameInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await management.OwnedOrganizationAsync(HttpContext.Caller, request.Organization, cancellationToken);
        return outcome switch
        {
            OrganizationRecord organization => ManagementService.DisplayName(request.Name) switch
            {
                string name => await catalog.CreateProjectAsync(new ProjectRecord(Ulid.NewUlid(time.GetUtcNow()), organization.Id, name, time.GetUtcNow(), null), cancellationToken) switch
                {
                    ProjectRecord project => new Created<ProjectBody>(string.Concat("/v1/projects/", project.Id.ToString()), ProjectBody.From(project)),
                    StateConflict conflict => conflict,
                    BackendUnavailable unavailable => unavailable,
                },
                InvalidInput invalid => invalid,
            },
            CredentialRejected unauthorized => unauthorized,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
