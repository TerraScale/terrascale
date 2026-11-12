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
            + " comes from another region: it is answered for this region alone and is not sent on inside this request. With " + ClosureFanout.ScopeHeader + ": " + ClosureFanout.CheckScope
            + " the region writes nothing and answers 200 empty when it holds nothing of the project and has no fence, its own status when it has the fence, or the same 409. "
            + "A version 1 request asks every other serving region that way before it writes its fence: 409 not_empty when one of them holds a database, key, backup destination or bucket, "
            + "and 503 when one does not answer, both with nothing written. A pending answer lists in notEmptyRegions the regions that refused the closure with not_empty after the fence.");
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

        var scope = HttpContext.Request.Headers[ClosureFanout.ScopeHeader];
        var result = scope is [ClosureFanout.CheckScope]
            ? await catalog.InspectClosureAsync(project, request.ClosureId!, request.AuthorityOrganizationId!, request.Version, cancellationToken).ConfigureAwait(false)
            : await catalog.CloseProjectAsync(project, request.ClosureId!, request.AuthorityOrganizationId!, request.Version, scope is [ClosureFanout.RegionScope], cancellationToken)
                .ConfigureAwait(false);
        return result switch
        {
            ProjectClosureStatus { Status: ProjectClosure.Pending } pending => TypedResults.Json(pending, ManagementJsonContext.Default.ProjectClosureStatus, statusCode: StatusCodes.Status202Accepted),
            ProjectClosureStatus answered => TypedResults.Ok(answered),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
