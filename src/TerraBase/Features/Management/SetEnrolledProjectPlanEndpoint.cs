using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>
/// <c>PUT /admin/projects/{project}/plan</c>: sets the plan of the storage organization a signed project enrolled. Served only on the internal administrative listener,
/// on the private network, as <c>POST /admin/api-keys</c> is.
/// </summary>
public sealed class SetEnrolledProjectPlanEndpoint(IProjectCatalog catalog) : Endpoint<PlanRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/admin/projects/{project}/plan");
        Name("setEnrolledProjectPlan");
        Summary("Sets an enrolled project's plan");
        Description("Publishes hobby (the default), starter, pro, team or enterprise for the organization TerraBase created when a signed project token created its first database. 404 not_found for an unknown project or an organization created through management routes, whose creator sets its plan.");
        Tags("Administration");
        Produces<Organization>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = DocumentRoute.Project(HttpContext);
        if (!EntityIds.IsValid(project))
        {
            return ErrorResponse.From(new Validation("project must be a canonical uppercase ULID"));
        }

        return ManagementRoute.CheckPlan(request) switch
        {
            Validation error => ErrorResponse.From(error),
            TerraBase.Tenancy.PlanTier plan => await catalog.SetEnrolledPlanAsync(project, plan, cancellationToken).ConfigureAwait(false) switch
            {
                Organization value => TypedResults.Ok(value),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}
