using System.Globalization;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;
using TerraBase.Features.Callers;

namespace TerraBase.Features.Management;

internal sealed class InspectProjectClosureEndpoint(IProjectCatalog catalog) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Metadata(new Requires("closure"));
        Get(ClosureFanout.Route);
        Name("inspectProjectClosure");
        Summary("Answers what a project closure would find in this region");
        Description("Internal peer listener only, behind the shared peer credential; 404 elsewhere. The query carries version, closureId and authorityOrganizationId of the closure. "
            + "One read transaction, nothing is written: 200 empty when this region has no fence for the project and, for version 1, holds no database, key, backup destination or bucket of it; "
            + "this region's own 200 accepted or 202 pending when it already has the fence; 409 not_empty, closure_conflict, binding_conflict or platform_project_protected as the closure itself would answer. "
            + "A region asks every other serving region this before it writes the fence of a version 1 closure.");
        Tags("Administration");
        Produces<ProjectClosureStatus>();
        Produces<ProjectClosureStatus>(StatusCodes.Status202Accepted);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var project = DatabaseRoute.Project(HttpContext);
        var query = HttpContext.Request.Query;
        if (query[ClosureFanout.VersionQuery] is not [{ } text] || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version is not (1 or 2)
            || query[ClosureFanout.ClosureQuery] is not [{ } closureId] || query[ClosureFanout.OrganizationQuery] is not [{ } authorityOrganizationId]
            || !EntityIds.IsValid(project) || !EntityIds.IsValid(closureId) || !EntityIds.IsValid(authorityOrganizationId))
        {
            return ErrorResponse.From(new Validation("version must be 1 or 2; project, closureId and authorityOrganizationId must be canonical uppercase ULIDs"));
        }

        return CloseProjectEndpoint.Answered(await catalog.InspectClosureAsync(project, closureId, authorityOrganizationId, version, cancellationToken).ConfigureAwait(false));
    }
}
