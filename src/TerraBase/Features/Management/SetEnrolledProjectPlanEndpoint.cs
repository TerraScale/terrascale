using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class SetEnrolledProjectPlanEndpoint(CatalogStore catalog)
    : Endpoint<PlanInput, Result<ProjectBody, Validation, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Put("/admin/projects/{project}/plan");
        Name("adminSetProjectPlan");
        Summary("Sets an enrolled project's plan");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<ProjectBody, Validation, Forbidden, NotFound, Unavailable>> HandleAsync(PlanInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "plans"))
        {
            return new Forbidden("setting a plan needs the plans grant", "forbidden_caller");
        }

        if (!Plans.IsPlan(request.Plan))
        {
            return new Validation("plan must be hobby, starter, pro, team or enterprise", "invalid_plan");
        }

        if (ManagementService.Id(request.Project) is not { } id)
        {
            return new NotFound("the project does not exist", "unknown_project");
        }

        return await catalog.SetProjectPlanAsync(id, request.Plan, cancellationToken) switch
        {
            Done => await catalog.GetProjectAsync(id, cancellationToken) switch
            {
                ProjectRecord project => ProjectBody.From(project),
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
