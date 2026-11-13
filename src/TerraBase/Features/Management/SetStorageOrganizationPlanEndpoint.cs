using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;
using TerraBase.Features.Callers;

namespace TerraBase.Features.Management;

internal sealed class SetStorageOrganizationPlanEndpoint(IProjectCatalog catalog, PlanFanout fanout) : Endpoint<PlanRequest, IResult>
{
    public override void Configure()
    {
        Metadata(new Requires("plans", "admin"));
        Put("/admin/organizations/{organization}/plan");
        Name("setStorageOrganizationPlan");
        Summary("Sets a ts-auth organization's plan");
        Description("Publishes hobby, starter, pro, team or enterprise in every region for the storage organization of a ts-auth organization. Publication before any databases creates its storage record, which its projects' first database enrollment adopts with the plan. An unchanged published plan writes nothing. 403 platform_plan_locked for the platform organization. 404 not_found for a deleted organization or an organization created through management routes. 503 plan_unpublished when a region cannot accept the plan; retry publication.");
        Tags("Administration");
        Produces<Organization>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<IResult> HandleAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organization = ManagementRoute.OrganizationId(HttpContext);
        if (!EntityIds.IsValid(organization))
        {
            return ErrorResponse.From(new Validation("organization must be a canonical uppercase ULID"));
        }

        return ManagementRoute.CheckPlan(request) switch
        {
            Validation error => ErrorResponse.From(error),
            PlanTier plan => await catalog.SetStoragePlanAsync(organization, plan, cancellationToken).ConfigureAwait(false) switch
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
