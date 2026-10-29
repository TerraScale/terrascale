using TerraBase.Errors;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>PUT /v1/management/organizations/{organization}/plan</c>: sets an organization's plan.</summary>
internal sealed class SetOrganizationPlanEndpoint(IManagementService management) : Endpoint<PlanRequest, IResult>
{
    public override void Configure()
    {
        Put(ManagementRoute.Organization + "/plan");
        Metadata(AnchoredRevocation.Instance);
        Name("setOrganizationPlan");
        Summary("Sets an organization's plan");
        Description("Publishes a resource plan authorized by the platform: hobby (the default), starter, pro, team or enterprise. Requires the platform operator (internal listener) acting as an organization owner. " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<Organization>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(PlanRequest request, CancellationToken cancellationToken) =>
        ManagementRoute.CheckPlan(request) switch
        {
            Validation error => ErrorResponse.From(error),
            TerraBase.Tenancy.PlanTier plan => await SetPlanAsync(plan, cancellationToken),
        };

    private async ValueTask<IResult> SetPlanAsync(TerraBase.Tenancy.PlanTier plan, CancellationToken cancellationToken)
    {
        var user = ManagementRoute.UserId(HttpContext);
        var organization = ManagementRoute.OrganizationId(HttpContext);
        var found = await management.GetOrganizationAsync(user, organization, cancellationToken).ConfigureAwait(false);
        if (found is not Organization)
        {
            return found switch
            {
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                Organization => throw new System.Diagnostics.UnreachableException(),
            };
        }

        if (!HttpContext.User.IsOperator())
        {
            return ErrorResponse.From(new Forbidden("plan changes need the platform operator"));
        }

        return await management.SetPlanAsync(user, organization, plan, cancellationToken).ConfigureAwait(false) switch
        {
            Organization value => TypedResults.Ok(value),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
