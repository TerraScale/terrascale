using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>
/// <c>PUT /admin/organizations/{organization}/plan</c>: sets the plan of the memberless storage organization whose id is the
/// ts-auth organization id, before or after its first database enrolls. Served only on the internal administrative listener,
/// as <c>PUT /admin/projects/{project}/plan</c> is.
/// </summary>
public sealed class SetStorageOrganizationPlanEndpoint(IProjectCatalog catalog) : Endpoint<PlanRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/admin/organizations/{organization}/plan");
        Name("setStorageOrganizationPlan");
        Summary("Sets a ts-auth organization's plan");
        Description("Publishes hobby (the default), starter, pro, team or enterprise for the storage organization of a ts-auth organization. A paid plan for an organization with no databases yet creates its storage record, which its projects' first database enrollment adopts with the plan. An unchanged plan writes nothing. 404 not_found for hobby on an organization with no record, a deleted organization, or an organization created through management routes.");
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
        var organization = ManagementRoute.OrganizationId(HttpContext);
        if (!EntityIds.IsValid(organization))
        {
            return ErrorResponse.From(new Validation("organization must be a canonical uppercase ULID"));
        }

        return ManagementRoute.CheckPlan(request) switch
        {
            Validation error => ErrorResponse.From(error),
            TerraBase.Tenancy.PlanTier plan => await catalog.SetStoragePlanAsync(organization, plan, cancellationToken).ConfigureAwait(false) switch
            {
                Organization value => TypedResults.Ok(value),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}
