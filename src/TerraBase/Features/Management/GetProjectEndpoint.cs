using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union GetProjectResponse(ProjectBody, CredentialRejected, ResourceNotFound, BackendUnavailable);

internal readonly union OwnedProjectLookup(ProjectRecord, CredentialRejected, ResourceNotFound, BackendUnavailable);

internal sealed class GetProjectEndpoint(ManagementService management, CatalogStore catalog)
    : Endpoint<RouteInput, GetProjectResponse>
{
    public override void Configure()
    {
        Get(ManagementRoute.Project);
        Name("getProject");
        Summary("Reads a project of an organization the calling user owns");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<GetProjectResponse> HandleAsync(RouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await ProjectFlow.OwnedProjectAsync(HttpContext.Caller, management, catalog, request, cancellationToken);
        return outcome switch
        {
            ProjectRecord project => ProjectBody.From(project),
            CredentialRejected unauthorized => unauthorized,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}

internal static class ProjectFlow
{
    public static async ValueTask<OwnedProjectLookup> OwnedProjectAsync(
        Caller caller, ManagementService management, CatalogStore catalog, RouteInput request, CancellationToken cancellationToken)
    {
        var outcome = await management.OwnedOrganizationAsync(caller, request.Organization, cancellationToken);
        return outcome switch
        {
            OrganizationRecord organization => ManagementService.Id(request.Project) is { } projectId
                ? await catalog.GetProjectAsync(projectId, cancellationToken) switch
                {
                    ProjectRecord project when project.OrganizationId == organization.Id => project,
                    ProjectRecord or ResourceNotFound => new ResourceNotFound("the project does not exist", "unknown_project"),
                    BackendUnavailable unavailable => unavailable,
                }
                : new ResourceNotFound("the project does not exist", "unknown_project"),
            CredentialRejected unauthorized => unauthorized,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
