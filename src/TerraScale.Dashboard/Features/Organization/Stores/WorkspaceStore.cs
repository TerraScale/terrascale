using System.Collections.Immutable;

namespace TerraScale.Dashboard.Shared.State;

public sealed partial class DashboardStore
{
    public OrganizationWorld ActiveOrganization => World.ActiveOrganization;
    public ImmutableArray<OrganizationWorld> Organizations => [.. World.Organizations.Values];
    public bool IsPersonalOrganization => ActiveOrganization.Kind == OrganizationKind.Personal;
    public bool FreeAllowanceEligible => IsPersonalOrganization
        && ActiveOrganization.Id == World.PersonalOrganizationId
        && ActiveOrganization.PersonalOwnerId == CurrentUser.Id;
    public bool CanManageActiveOrganization => TeamMembers.Any(m => m.UserId == CurrentUser.Id && m.Role == "owner");
    public bool CanProvisionResources => !ActiveOrganization.Archived
        && (FreeAllowanceEligible || ActiveOrganization.BillingStatus == "active");

    /// <summary>The pending Business organization the create-organization
    /// wizard is mid-way through: step 1 created it with billing still required,
    /// so its payment step may only run against this tenant.</summary>
    public bool HasPendingBusinessOrganization => ActiveOrganization
        is { Kind: OrganizationKind.Business, Archived: false, BillingStatus: "billing_required" };

    /// <summary>Bills the pending organization on the plan its payment screen
    /// selected. Returns false when there is no pending organization or the plan
    /// is not an available paid plan.</summary>
    public bool SetPendingOrganizationPlan(string planId)
    {
        if (!HasPendingBusinessOrganization
            || !Plans.Any(p => p.Id == planId && p.MonthlyUsd > 0 && p.Status == "Active"))
        {
            return false;
        }

        ActiveOrganization.PlanId = planId;
        return true;
    }

    public string? CreateBusinessOrganization(string name, string planId)
    {
        name = name.Trim();
        if (name.Length is 0 or > 128 || !Plans.Any(p => p.Id == planId && p.MonthlyUsd > 0 && p.Status == "Active"))
            return "Choose a name and an available paid plan.";
        lock (World)
        {
            // Repeated form submissions resume the unfinished setup, regardless
            // of the plan selected later on the payment screen.
            var pending = World.Organizations.Values.FirstOrDefault(o => o.Kind == OrganizationKind.Business
                && o.BillingStatus == "billing_required" && o.ActiveOrganizationName == name);
            if (pending is not null)
            {
                pending.PlanId = planId;
                World.ActiveOrganizationId = pending.Id;
                return null;
            }
            var id = AccountWorld.NewId("org");
            var organization = new OrganizationWorld
            {
                Id = id,
                Kind = OrganizationKind.Business,
                ActiveOrganizationName = name,
                PlanId = planId,
                TeamMembers = [new(CurrentUser.Id, CurrentUser.Name, CurrentUser.Email, "owner", "Active", false, DateTimeOffset.UtcNow.ToString("O"), 0)],
            };
            World.Organizations.Add(id, organization);
            World.ActiveOrganizationId = id;
        }
        return null;
    }

    public bool SwitchOrganization(string id)
    {
        if (!World.Organizations.TryGetValue(id, out var organization)
            || organization.Archived
            || (organization.PersonalOwnerId != CurrentUser.Id && !organization.TeamMembers.Any(m => m.UserId == CurrentUser.Id)))
            return false;
        World.ActiveOrganizationId = id;
        return true;
    }

    public bool SetPersonalArchived(bool archived)
    {
        if (!FreeAllowanceEligible || !CanManageActiveOrganization) return false;
        ActiveOrganization.Archived = archived;
        // Keep usage, projects, invoices and the immutable entitlement when restoring.
        if (archived) ActiveOrganization.PaidExcessEnabled = false;
        return true;
    }

    public string BeginPaymentSetup(string returnPath)
    {
        var organization = ActiveOrganization;
        organization.CheckoutSessionId = "cs_local_" + Guid.NewGuid().ToString("N");
        organization.CheckoutReturnPath = returnPath;
        return "/checkout/" + organization.CheckoutSessionId;
    }

    public OrganizationWorld? FindCheckout(string sessionId) => World.Organizations.Values
        .FirstOrDefault(o => o.CheckoutSessionId == sessionId && !o.Archived
            && o.TeamMembers.Any(m => m.UserId == CurrentUser.Id && m.Role == "owner"));

    public string? CompletePaymentSetup(string sessionId, bool canceled)
    {
        var organization = FindCheckout(sessionId);
        if (organization is null) return null;
        if (!canceled)
        {
            organization.PaymentMethodValidated = true;
            if (organization.Kind == OrganizationKind.Business) organization.BillingStatus = "active";
            else if (!World.OnboardingCompleted) CompleteOnboardingStep("payment-method");
        }
        var returnPath = organization.CheckoutReturnPath!;
        // Consume the session so a cancel/confirm replay cannot change billing state.
        organization.CheckoutSessionId = null;
        organization.CheckoutReturnPath = null;
        return returnPath + (returnPath.Contains('?') ? "&" : "?") + "payment=" + (canceled ? "canceled" : "success");
    }
}
