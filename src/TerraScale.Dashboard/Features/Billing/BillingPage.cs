using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Billing;

public sealed record BillingPage(ShellModel Shell, IReadOnlyList<BillingPlan> Plans, string Error = "")
{
    public BillingBinding? Binding { get; init; }
    public BillingCard? PaymentMethod { get; init; }
    public bool PaymentMethodUnavailable { get; init; }
    public TerraScale.Dashboard.Features.Billing.InternalBill? InternalBill { get; init; }
    public bool CanManage => Binding?.Kind != "internal" && Shell.Context.Role == "owner" && Shell.Context.Organization is { Status: "active", Archived: false, DeletionRequested: false, InternalBilling: null } organization && organization.OwnerUserId == Shell.Context.UserId;
    public BillingPlanPresentation PlanChanges { get; init; } = new(null, null, null, "");
    public BillingHistoryPresentation History { get; init; } = null!;
}

public sealed record BillingHistoryPresentation(IReadOnlyList<BillingInvoice> Invoices, InvoiceHistoryPage Page,
    InvoiceSummary Summary, DateTimeOffset Now, string Error, long SpendingCapCents);
