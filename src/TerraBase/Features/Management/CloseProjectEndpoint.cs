using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class CloseProjectEndpoint(ManagementService management)
    : Endpoint<ProjectClosureInput, Result<ProjectClosureBody, Validation, Forbidden, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Put("/admin/projects/{project}/closure");
        Name("adminCloseProject");
        Summary("Closes an empty project for ts-auth");
        Description("{\"version\": 1 | 2, \"closureId\": \"<id>\", \"authorityOrganizationId\": \"<ULID>\"}: deletes the project and its API keys once it holds no database. "
            + "Repeating the request after the project is gone answers accepted again.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<ProjectClosureBody, Validation, Forbidden, Conflict, Unavailable>> HandleAsync(
        ProjectClosureInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "closure"))
        {
            return new Forbidden("project closure needs an operator credential", "forbidden_caller");
        }

        if (request.Version is not int version || version is not (1 or 2))
        {
            return new Validation("version is 1 or 2", "unsupported_version");
        }

        if (string.IsNullOrWhiteSpace(request.ClosureId) || request.ClosureId.Length > 128)
        {
            return new Validation("closureId is 1 to 128 characters", "invalid_request");
        }

        if (ManagementService.Id(request.Project) is not { } projectId || ManagementService.Id(request.AuthorityOrganizationId) is not { } organizationId)
        {
            return new Validation("project and authorityOrganizationId must be canonical uppercase ULIDs", "invalid_request");
        }

        return await management.CloseProjectAsync(projectId, organizationId, cancellationToken) switch
        {
            Done => new ProjectClosureBody("accepted", request.ClosureId, 0, version),
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }
}
