namespace TerraScale.Dashboard.Features.Billing.Internal
{
    public sealed record InternalBillingConfiguration(string OrganizationId, string BasePlanKey, long SpendCapCents,
        long WriteCentsPerMillion, long StorageCentsPerGBMonth);
    public sealed record InternalBillingReservation(InternalBillingPolicy Policy, long SpendCapCents, string PreviousConfigurationId = "");
}
