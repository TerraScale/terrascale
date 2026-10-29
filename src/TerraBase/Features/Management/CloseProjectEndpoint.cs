using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>
/// <c>PUT /admin/projects/{project}/closure</c>: ts-auth's project closure handoff (docs/terrabase/project-closure.md).
/// Served only on the internal listener, behind the peer credential, as <c>PUT /admin/projects/{project}/plan</c> is.
/// </summary>
internal sealed class CloseProjectEndpoint(IProjectCatalog catalog) : Endpoint<ProjectClosureRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/admin/projects/{project}/closure");
        Name("closeProject");
        Summary("Fences a ts-auth project for closure");
        Description("Version 1 requires an empty project. Version 2 is ts-auth's explicit account-erasure handoff: permanently fences the project, purges its databases in every region, revokes its TerraBase keys and removes its backup destination. 200 accepted once every catalog holder confirmed the project empty and active database erasure; 202 pending while any has not "
            + "(or one sees a database). 409 not_empty, closure_conflict or binding_conflict writes nothing. A never-enrolled project is fenced too: "
            + "there is no 404. Retry with the same closureId after a lost response.");
        Tags("Administration");
        Produces<ProjectClosureStatus>();
        Produces<ProjectClosureStatus>(StatusCodes.Status202Accepted);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(ProjectClosureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = DocumentRoute.Project(HttpContext);
        if (request.Version is not (1 or 2) || !EntityIds.IsValid(project) || !EntityIds.IsValid(request.ClosureId) || !EntityIds.IsValid(request.AuthorityOrganizationId))
        {
            return ErrorResponse.From(new Validation("version must be 1 or 2; project, closureId and authorityOrganizationId must be canonical uppercase ULIDs"));
        }

        return await catalog.CloseProjectAsync(project, request.ClosureId!, request.AuthorityOrganizationId!, request.Version, cancellationToken).ConfigureAwait(false) switch
        {
            ProjectClosureStatus { Status: ProjectClosure.Accepted } accepted => TypedResults.Ok(accepted),
            ProjectClosureStatus pending => TypedResults.Json(pending, TerraBaseJsonContext.Default.ProjectClosureStatus, statusCode: StatusCodes.Status202Accepted),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
