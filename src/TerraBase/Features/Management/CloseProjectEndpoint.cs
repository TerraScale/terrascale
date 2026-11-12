using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class CloseProjectEndpoint(IProjectCatalog catalog) : Endpoint<ProjectClosureRequest, IResult>
{
    public override void Configure()
    {
        Put("/admin/projects/{project}/closure");
        Name("closeProject");
        Summary("Fences a ts-auth project for closure");
        Description("Version 1 requires an empty project. Version 2 is ts-auth's explicit account-erasure handoff: permanently fences the project, purges its databases in every region, revokes its TerraBase keys, removes its backup destination and erases its file buckets with their files. "
            + "The region that takes the request sends the same closure to every other serving region, which fences and erases its own records. 200 accepted once every catalog holder and every one of those regions confirmed the project empty and active database erasure; 202 pending while any has not "
            + "(or one sees a database). 409 not_empty, closure_conflict, binding_conflict or platform_project_protected (the ts-auth and ts-dashboard platform projects) writes nothing. A never-enrolled project is fenced too: "
            + "there is no 404. Retry with the same closureId after a lost response. A request that carries " + ClosureFanout.ScopeHeader + ": " + ClosureFanout.RegionScope
            + " comes from another region: it is answered for this region alone and is not sent on.");
        Tags("Administration");
        Produces<ProjectClosureStatus>();
        Produces<ProjectClosureStatus>(StatusCodes.Status202Accepted);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<IResult> HandleAsync(ProjectClosureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = DatabaseRoute.Project(HttpContext);
        if (request.Version is not (1 or 2) || !EntityIds.IsValid(project) || !EntityIds.IsValid(request.ClosureId) || !EntityIds.IsValid(request.AuthorityOrganizationId))
        {
            return ErrorResponse.From(new Validation("version must be 1 or 2; project, closureId and authorityOrganizationId must be canonical uppercase ULIDs"));
        }

        var forwarded = HttpContext.Request.Headers[ClosureFanout.ScopeHeader] is [ClosureFanout.RegionScope];
        return await catalog.CloseProjectAsync(project, request.ClosureId!, request.AuthorityOrganizationId!, request.Version, forwarded, cancellationToken).ConfigureAwait(false) switch
        {
            ProjectClosureStatus { Status: ProjectClosure.Accepted } accepted => TypedResults.Ok(accepted),
            ProjectClosureStatus pending => TypedResults.Json(pending, ManagementJsonContext.Default.ProjectClosureStatus, statusCode: StatusCodes.Status202Accepted),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
