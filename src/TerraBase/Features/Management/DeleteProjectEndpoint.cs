using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class DeleteProjectEndpoint(ManagementService management, CatalogStore catalog)
    : Endpoint<RouteInput, Result<Done, Unauthorized, NotFound, Conflict, Unavailable>>
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

    public override async ValueTask<Result<Done, Unauthorized, NotFound, Conflict, Unavailable>> HandleAsync(RouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ProjectFlow.OwnedProjectAsync(HttpContext.Caller, management, catalog, request, cancellationToken) switch
        {
            ProjectRecord project => await catalog.DeleteProjectAsync(project.Id, cancellationToken) switch
            {
                Done done => done,
                Conflict conflict => conflict,
                Unavailable unavailable => unavailable,
            },
            Unauthorized unauthorized => unauthorized,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
