using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union DeleteProjectResponse(Completed, CredentialRejected, ResourceNotFound, StateConflict, BackendUnavailable);

internal sealed class DeleteProjectEndpoint(ManagementService management, CatalogStore catalog)
    : Endpoint<RouteInput, DeleteProjectResponse>
{
    public override void Configure()
    {
        Delete(ManagementRoute.Project);
        Name("deleteProject");
        Summary("Deletes a project without databases and revokes its API keys");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<DeleteProjectResponse> HandleAsync(RouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await ProjectFlow.OwnedProjectAsync(HttpContext.Caller, management, catalog, request, cancellationToken);
        return outcome switch
        {
            ProjectRecord project => await catalog.DeleteProjectAsync(project.Id, cancellationToken) switch
            {
                Completed done => done,
                StateConflict conflict => conflict,
                BackendUnavailable unavailable => unavailable,
            },
            CredentialRejected unauthorized => unauthorized,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
