using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Features.Billing;

public sealed record InternalBillingConfiguration(string OrganizationId, string BasePlanKey, long SpendCapCents,
    long WriteCentsPerMillion, long StorageCentsPerGiBMonth);
public sealed record InternalBillingReservation(InternalBillingPolicy Policy, long SpendCapCents, string PreviousConfigurationId = "");
public sealed record InternalBillingSingleton(string OrganizationId, string PlanId);
public sealed class InternalBillingReportException(InternalBill bill, Exception cause) : Exception("Internal billing report could not be completed.", cause)
{
    public InternalBill Bill { get; } = bill;
}

public sealed record InternalBill(bool Available, bool ReportOnly, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    long Reads, long Writes, long LogicalBytes, long BaseCents, long WriteCents, long StorageCents, long TotalCents,
    long SpendCapCents, bool CapReached, long WriteCentsPerMillion, long StorageCentsPerGiBMonth,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Alert = null)
{
    public static string Money(long cents) => BillingPlanPresentation.Money(cents);
}
