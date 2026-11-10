using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class SetEnrolledProjectPlanEndpoint(IProjectCatalog catalog, PlanFanout fanout) : Endpoint<PlanRequest, IResult>
{
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
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<IResult> HandleAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = DatabaseRoute.Project(HttpContext);
        if (!EntityIds.IsValid(project))
        {
            return ErrorResponse.From(new Validation("project must be a canonical uppercase ULID"));
        }

        return ManagementRoute.CheckPlan(request) switch
        {
            Validation error => ErrorResponse.From(error),
            PlanTier plan => await catalog.SetEnrolledPlanAsync(project, plan, cancellationToken).ConfigureAwait(false) switch
            {
                Organization value => await fanout.PublishAsync(HttpContext, value.Id, plan, cancellationToken).ConfigureAwait(false) is { } error
                    ? ErrorResponse.From(error) : TypedResults.Ok(value),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}
