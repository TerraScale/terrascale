using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class SetEnrolledProjectPlanEndpoint(CatalogStore catalog)
    : Endpoint<PlanInput, EnrolledProjectResponse>
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

    public override async ValueTask<EnrolledProjectResponse> HandleAsync(PlanInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "plans"))
        {
            return new CallerForbidden("setting a plan needs the plans grant", "forbidden_caller");
        }

        if (!Plans.IsPlan(request.Plan))
        {
            return new InvalidInput("plan must be hobby, starter, pro, team or enterprise", "invalid_plan");
        }

        if (ManagementService.Id(request.Project) is not { } id)
        {
            return new ResourceNotFound("the project does not exist", "unknown_project");
        }

        var outcome = await catalog.SetProjectPlanAsync(id, request.Plan, cancellationToken);
        return outcome switch
        {
            Completed => await catalog.GetProjectAsync(id, cancellationToken) switch
            {
                ProjectRecord project => ProjectBody.From(project),
                ResourceNotFound missing => missing,
                BackendUnavailable unavailable => unavailable,
            },
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
