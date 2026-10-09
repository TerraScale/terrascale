using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>PUT /v1/management/organizations/{organization}/plan</c>: sets an organization's plan.</summary>
public sealed class SetOrganizationPlanEndpoint(IManagementService management) : Endpoint<PlanRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(ManagementRoute.Organization + "/plan");
        Name("setOrganizationPlan");
        Summary("Sets an organization's plan");
        Description("Publishes a resource plan authorized by the platform: hobby (the default), pro or enterprise. Requires the management service credential acting as an organization owner. " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<Organization>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = ManagementRoute.Plan(request);
        var user = ManagementRoute.UserId(HttpContext);
        var organization = ManagementRoute.OrganizationId(HttpContext);
        if (await management.GetOrganizationAsync(user, organization, cancellationToken).ConfigureAwait(false) is null)
        {
            return ManagementRoute.Error(ManagementError.NotFound);
        }

        if (!HttpContext.User.IsManagementService())
        {
            return ErrorResponse.From(new Forbidden("plan changes need the management service credential"));
        }

        return ManagementRoute.Ok(await management.SetPlanAsync(user, organization, plan, cancellationToken));
    }
}
