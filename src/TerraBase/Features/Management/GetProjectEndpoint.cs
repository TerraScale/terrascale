using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class GetProjectEndpoint(ManagementService management, CatalogStore catalog)
    : Endpoint<RouteInput, Result<ProjectBody, Unauthorized, NotFound, Unavailable>>
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

    public override async ValueTask<Result<ProjectBody, Unauthorized, NotFound, Unavailable>> HandleAsync(RouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ProjectFlow.OwnedProjectAsync(HttpContext.Caller, management, catalog, request, cancellationToken) switch
        {
            ProjectRecord project => ProjectBody.From(project),
            Unauthorized unauthorized => unauthorized,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}

internal static class ProjectFlow
{
    public static async ValueTask<Result<ProjectRecord, Unauthorized, NotFound, Unavailable>> OwnedProjectAsync(
        Caller caller, ManagementService management, CatalogStore catalog, RouteInput request, CancellationToken cancellationToken) =>
        await management.OwnedOrganizationAsync(caller, request.Organization, cancellationToken) switch
        {
            OrganizationRecord organization => ManagementService.Id(request.Project) is { } projectId
                ? await catalog.GetProjectAsync(projectId, cancellationToken) switch
                {
                    ProjectRecord project when project.OrganizationId == organization.Id => project,
                    ProjectRecord or NotFound => new NotFound("the project does not exist", "unknown_project"),
                    Unavailable unavailable => unavailable,
                }
                : new NotFound("the project does not exist", "unknown_project"),
            Unauthorized unauthorized => unauthorized,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
}
