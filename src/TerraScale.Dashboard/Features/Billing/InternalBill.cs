using System.Text.Json.Serialization;
using TerraScale.Dashboard.Features.Billing.Plans;

namespace TerraScale.Dashboard.Features.Billing;

public sealed record InternalBill(bool Available, bool ReportOnly, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    long Reads, long Writes, long LogicalBytes, long BaseCents, long WriteCents, long StorageCents, long TotalCents,
    long SpendCapCents, bool CapReached, long WriteCentsPerMillion, long StorageCentsPerGBMonth,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Alert = null)
{
    public static string Money(long cents) => BillingPlanPresentation.Money(cents);
}
