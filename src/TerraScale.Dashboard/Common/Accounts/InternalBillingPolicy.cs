
namespace TerraScale.Dashboard.Common.Accounts
{
    // A copied paid-plan contract, committed with the organization's cap. This
    // hidden plan is domain state; it never changes the public plan catalog.
    public sealed record InternalBillingPolicy(string OrganizationId, string PlanId, string BasePlanKey, string ConfigurationId,
        BillingPlan Plan, PlanAllowances Allowances, PlanResourceLimits Resources,
        long WriteCentsPerMillion, long StorageCentsPerGBMonth)
    {
        public void Validate(OrganizationRecord organization)
        {
            if (OrganizationId != organization.Id || PlanId != organization.PlanId || !TerraScaleIds.IsValid(PlanId) ||
                string.IsNullOrEmpty(ConfigurationId) || !PlanCatalog.Plans.Any(plan => plan.Key == BasePlanKey && plan.PriceCents > 0) ||
                Plan is null || Plan.Id != PlanId || Plan.Key != "internal" || Plan.Name != "TerraScale Internal" ||
                Plan.PriceCents <= 0 || Plan.AnnualPriceCents < 0 || Plan.TrialDays < 0 || Allowances is null || Resources is null ||
                Allowances.MonthlyOperations < 0 || Allowances.StorageBytes < 0 || Allowances.MonthlyEgressBytes < 0 ||
                Resources.Databases < 0 || Resources.Members < 0 || Resources.Projects < 0 || WriteCentsPerMillion < 0 || StorageCentsPerGBMonth < 0)
            {
                throw new IOException("Internal billing policy identity or pricing is invalid.");
            }
        }
    }

    public sealed record InternalBillingAlert(string OwnerUserId, string ConfigurationId, long SpendCapCents,
        string Id, string Month, string Body, DateTimeOffset CreatedAt);
}
