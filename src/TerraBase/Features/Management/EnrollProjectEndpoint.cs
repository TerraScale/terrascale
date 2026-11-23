using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class EnrollProjectEndpoint(CatalogStore catalog, TimeProvider time)
    : Endpoint<EnrollmentInput, Result<ProjectBody, Validation, Forbidden, NotFound, Unavailable>>
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

    public override async ValueTask<Result<ProjectBody, Validation, Forbidden, NotFound, Unavailable>> HandleAsync(EnrollmentInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "closure"))
        {
            return new Forbidden("enrollment needs an operator credential", "forbidden_caller");
        }

        if (ManagementService.Id(request.Project) is not { } projectId || ManagementService.Id(request.Organization) is not { } organizationId)
        {
            return new Validation("project and organization must be canonical uppercase ULIDs", "invalid_request");
        }

        var existing = await catalog.GetProjectAsync(projectId, cancellationToken) switch
        {
            ProjectRecord project => project,
            NotFound or Unavailable => null,
        };
        var record = new ProjectRecord(projectId, organizationId, existing?.Name ?? projectId.ToString(), existing?.CreatedAt ?? time.GetUtcNow(), existing?.Plan);
        return await catalog.GetOrganizationAsync(organizationId, cancellationToken) switch
        {
            OrganizationRecord => await catalog.UpsertProjectAsync(record, cancellationToken) switch
            {
                Done => ProjectBody.From(record),
                Unavailable unavailable => unavailable,
            },
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
