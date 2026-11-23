using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union EnrolledProjectResponse(ProjectBody, InvalidInput, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class EnrollProjectEndpoint(CatalogStore catalog, TimeProvider time)
    : Endpoint<EnrollmentInput, EnrolledProjectResponse>
{
    public override void Configure()
    {
        Put("/admin/projects/{project}/enrollment");
        Name("adminEnrollProject");
        Summary("Enrolls a project into an organization");
        Description("{\"organization\": \"<ULID>\"}: records the project under that organization so its databases, keys and usage resolve.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<EnrolledProjectResponse> HandleAsync(EnrollmentInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "closure"))
        {
            return new CallerForbidden("enrollment needs an operator credential", "forbidden_caller");
        }

        if (ManagementService.Id(request.Project) is not { } projectId || ManagementService.Id(request.Organization) is not { } organizationId)
        {
            return new InvalidInput("project and organization must be canonical uppercase ULIDs", "invalid_request");
        }

        var existingOutcome = await catalog.GetProjectAsync(projectId, cancellationToken);
        var existing = existingOutcome switch
        {
            ProjectRecord project => project,
            ResourceNotFound or BackendUnavailable => null,
        };
        var record = new ProjectRecord(projectId, organizationId, existing?.Name ?? projectId.ToString(), existing?.CreatedAt ?? time.GetUtcNow(), existing?.Plan);
        var outcome = await catalog.GetOrganizationAsync(organizationId, cancellationToken);
        return outcome switch
        {
            OrganizationRecord => await catalog.UpsertProjectAsync(record, cancellationToken) switch
            {
                Completed => ProjectBody.From(record),
                BackendUnavailable unavailable => unavailable,
            },
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
