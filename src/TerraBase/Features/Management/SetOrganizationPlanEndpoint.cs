using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Platform;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union OrganizationPlanResponse(OrganizationBody, InvalidInput, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class SetOrganizationPlanEndpoint(CatalogStore catalog)
    : Endpoint<PlanInput, OrganizationPlanResponse>
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

    public override async ValueTask<OrganizationPlanResponse> HandleAsync(PlanInput request, CancellationToken cancellationToken) =>
        await PlanFlow.SetOrganizationPlanAsync(HttpContext.Caller, catalog, request, cancellationToken);
}

internal static class PlanFlow
{
    private static readonly CallerForbidden PlatformPlanLocked = new("the platform organization's plan cannot change", "platform_plan_locked");

    public static bool IsOperator(Caller caller, string grant) => caller switch
    {
        PeerCaller => true,
        ServiceCaller service => service.Grants.Contains(grant) || service.Grants.Contains("admin"),
        AnonymousCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => false,
    };

    public static async ValueTask<OrganizationPlanResponse> SetOrganizationPlanAsync(
        Caller caller, CatalogStore catalog, PlanInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsOperator(caller, "plans"))
        {
            return new CallerForbidden("setting a plan needs the plans grant", "forbidden_caller");
        }

        if (!Plans.IsPlan(request.Plan))
        {
            return new InvalidInput("plan must be hobby, starter, pro, team or enterprise", "invalid_plan");
        }

        if (ManagementService.Id(request.Organization) is not { } id)
        {
            return new ResourceNotFound("the organization does not exist", "unknown_organization");
        }

        if (id == PlatformCatalog.OrganizationId)
        {
            return PlatformPlanLocked;
        }

        var outcome = await catalog.SetOrganizationPlanAsync(id, request.Plan, cancellationToken);
        return outcome switch
        {
            Completed => await catalog.GetOrganizationAsync(id, cancellationToken) switch
            {
                OrganizationRecord organization => OrganizationBody.From(organization),
                ResourceNotFound missing => missing,
                BackendUnavailable unavailable => unavailable,
            },
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public static async ValueTask<OrganizationPlanResponse> SetStorageOrganizationPlanAsync(
        Caller caller, CatalogStore catalog, TimeProvider time, PlanInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsOperator(caller, "plans"))
        {
            return new CallerForbidden("setting a plan needs the plans grant", "forbidden_caller");
        }

        if (!Plans.IsPlan(request.Plan))
        {
            return new InvalidInput("plan must be hobby, starter, pro, team or enterprise", "invalid_plan");
        }

        if (ManagementService.Id(request.Organization) is not { } id)
        {
            return new InvalidInput("organization must be a canonical uppercase ULID", "invalid_request");
        }

        if (id == PlatformCatalog.OrganizationId)
        {
            return PlatformPlanLocked;
        }

        var outcome = await catalog.SetStorageOrganizationPlanAsync(id, request.Plan, time.GetUtcNow(), cancellationToken);
        return outcome switch
        {
            OrganizationRecord organization => OrganizationBody.From(organization),
            BackendUnavailable unavailable => unavailable,
        };
    }
}
