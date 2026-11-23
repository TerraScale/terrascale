using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class SetOrganizationPlanEndpoint(CatalogStore catalog)
    : Endpoint<PlanInput, Result<OrganizationBody, Validation, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Put(ManagementRoute.Organization + "/plan");
        Name("setOrganizationPlan");
        Summary("Sets an organization's plan");
        Description("Operators only: the internal peer credential or a caller token with the plans or admin grant.");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<OrganizationBody, Validation, Forbidden, NotFound, Unavailable>> HandleAsync(PlanInput request, CancellationToken cancellationToken) =>
        await PlanFlow.SetOrganizationPlanAsync(HttpContext.Caller, catalog, request, cancellationToken);
}

internal static class PlanFlow
{
    public static bool IsOperator(Caller caller, string grant) => caller switch
    {
        PeerCaller => true,
        ServiceCaller service => service.Grants.Contains(grant) || service.Grants.Contains("admin"),
        AnonymousCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => false,
    };

    public static async ValueTask<Result<OrganizationBody, Validation, Forbidden, NotFound, Unavailable>> SetOrganizationPlanAsync(
        Caller caller, CatalogStore catalog, PlanInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsOperator(caller, "plans"))
        {
            return new Forbidden("setting a plan needs the plans grant", "forbidden_caller");
        }

        if (!Plans.IsPlan(request.Plan))
        {
            return new Validation("plan must be hobby, starter, pro, team or enterprise", "invalid_plan");
        }

        if (ManagementService.Id(request.Organization) is not { } id)
        {
            return new NotFound("the organization does not exist", "unknown_organization");
        }

        return await catalog.SetOrganizationPlanAsync(id, request.Plan, cancellationToken) switch
        {
            Done => await catalog.GetOrganizationAsync(id, cancellationToken) switch
            {
                OrganizationRecord organization => OrganizationBody.From(organization),
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
