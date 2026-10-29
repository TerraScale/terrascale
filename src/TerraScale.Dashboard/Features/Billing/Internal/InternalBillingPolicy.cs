namespace TerraScale.Dashboard.Features.Billing.Internal
{
    public sealed record InternalBillingConfiguration(string OrganizationId, string BasePlanKey, long SpendCapCents,
        long WriteCentsPerMillion, long StorageCentsPerGBMonth);
    public sealed record InternalBillingReservation(InternalBillingPolicy Policy, long SpendCapCents, string PreviousConfigurationId = "");
    public sealed record InternalBillingSingleton(string OrganizationId, string PlanId);
    public sealed class InternalBillingReportException(InternalBill bill, Exception cause) : Exception("Internal billing report could not be completed.", cause)
    {
        public InternalBill Bill { get; } = bill;
    }
}
