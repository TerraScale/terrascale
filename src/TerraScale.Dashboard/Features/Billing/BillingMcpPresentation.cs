using System.ComponentModel;
using System.Text.Json.Serialization;
using Stripe;

namespace TerraScale.Dashboard.Features.Billing;

public sealed record BillingMcpPlan(string Key, string Name,
    [property: Description(PlanPriceText.MonthlyPriceCents)] long MonthlyPriceCents,
    [property: Description(PlanPriceText.AnnualPriceCents)] long AnnualPriceCents,
    [property: Description(PlanPriceText.Intervals)] IReadOnlyList<string> Intervals,
    [property: Description(PlanPriceText.MonthlyPrice)] string MonthlyPrice,
    [property: Description(PlanPriceText.AnnualPrice)] string AnnualPrice, long MonthlyEgressBytes, int TrialDays, [property: Description("Database limit; 0 means unlimited.")] int MaxDatabases,
    [property: Description("Member limit; 0 means unlimited.")] int MaxMembers,
    [property: Description("Included operation units per month, shared by reads and writes.")] long OperationUnitsPerMonth,
    [property: Description("Bytes in one started operation unit.")] int OperationUnitBytes,
    IReadOnlyList<string> Features, [property: Description("Whether the organization is on this plan.")] bool Current);
public sealed record BillingMcpPlans(IReadOnlyList<BillingMcpPlan> Plans, string CurrentPlanKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CurrentInterval);
public sealed record BillingMcpChange(string PlanKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Interval,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? EffectiveAt,
    [property: Description("Stripe invoice page where the owner completes the pending payment, such as 3D Secure authentication."), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PaymentUrl = null);
public sealed record BillingMcpPaymentMethod(bool Validated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Brand = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastFour = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Expiry = null);
public sealed record BillingMcpSummary(string OrganizationId, string OrganizationName, string OrganizationKind, string PlanKey, string PlanName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Interval,
    [property: Description("Billing status as the billing page shows it, such as Active, Trialing, or Payment required.")] string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubscriptionStatus,
    [property: Description("Whether a live subscription exists; plan changes need one, otherwise use create_checkout.")] bool HasSubscription,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RenewalAt,
    bool TrialUsed, bool CheckoutInProgress,
    [property: Description("Plan change that applies at renewal; undo it with undo_plan_change."), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BillingMcpChange? ScheduledChange,
    [property: Description("Upgrade waiting for Stripe to confirm payment."), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BillingMcpChange? PendingChange,
    [property: Description("Payment method summary, shown to the owner only."), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BillingMcpPaymentMethod? PaymentMethod,
    [property: Description("Whether the caller owns the organization and may change billing.")] bool CanManage);
public sealed record BillingMcpPortal(
    [property: Description("Billing portal URL for the organization owner to open in a browser. Relative URLs are dashboard pages.")] string Url);

public sealed partial class StripeBilling
{
    public async Task<BillingMcpPlans> McpPlansAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await MemberAsync(context, cancellationToken);
        var facts = await McpFactsAsync(context, cancellationToken);
        return new(PlanCatalog.Plans.Where(plan => plan.PriceCents > 0).OrderBy(plan => plan.PriceCents).ThenBy(plan => plan.Key)
            .Select(plan =>
            {
                var limits = PlanCatalog.ResourceLimits(plan.Id);
                var allowance = PlanCatalog.Limits(plan.Id);
                return new BillingMcpPlan(plan.Key, plan.Name, plan.PriceCents, plan.AnnualPriceCents, plan.Intervals,
                    plan.PriceFor("monthly") + " " + BillingPlan.UnitFor("monthly"), plan.PriceFor("annual") + " " + BillingPlan.UnitFor("annual"), allowance.MonthlyEgressBytes, plan.TrialDays, limits.Databases,
                    limits.Members, allowance.MonthlyOperations, PlanCatalog.OperationUnitBytes,
                    new[] { plan.StorageAllowance, plan.OperationsAllowance, plan.EgressAllowance, plan.DatabaseAllowance, plan.MemberAllowance }
                        .Where(value => value != "Limit not published").ToArray(), plan.Key == facts.PlanKey);
            }).ToArray(), facts.PlanKey, Empty(facts.Interval));
    }

    public async Task<BillingMcpSummary> McpSummaryAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await MemberAsync(context, cancellationToken);
        var org = context.Organization!;
        var facts = await McpFactsAsync(context, cancellationToken);
        var binding = facts.Binding;
        var changes = facts.State;
        var owner = context.Role == "owner" && org.OwnerUserId == context.UserId;
        BillingCard? paymentMethod = null;
        if (owner)
            try { paymentMethod = await PaymentMethodAsync(context, cancellationToken); }
            catch (Exception error) when (error is HttpRequestException or StripeException or BillingPaymentMethodUnavailableException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { /* Card facts remain absent when the vendor cannot report them. */ }
        var checkoutInProgress = false;
        if (binding is { CheckoutId.Length: > 0 } && !facts.HasSubscription && binding.Status != "canceled")
        {
            var client = await ClientAsync(cancellationToken);
            var checkout = await client.V1.Checkout.Sessions.GetAsync(binding.CheckoutId, cancellationToken: cancellationToken);
            ValidateCheckout(binding, checkout);
            checkoutInProgress = checkout.Status == "open";
        }
        var status = facts.SubscriptionStatus == "trialing" ? "Trialing" : org.BillingStatus == "billing_required" ? "Payment required"
            : org.BillingStatus.Length == 0 ? "Active" : char.ToUpperInvariant(org.BillingStatus[0]) + org.BillingStatus[1..];
        return new(org.Id, org.Name, org.Kind, facts.PlanKey, PlanCatalog.For(org)?.Name ?? "",
            Empty(facts.Interval), status, Empty(facts.SubscriptionStatus), facts.HasSubscription,
            facts.HasSubscription ? changes?.RenewalAt ?? binding?.RenewalAt : null,
            binding?.TrialUsed ?? false, checkoutInProgress,
            changes is { ScheduledPlanKey.Length: > 0 } ? new(changes.ScheduledPlanKey, Empty(changes.ScheduledInterval), changes.ScheduledAt) : null,
            changes is { PendingPlanKey.Length: > 0 } ? new(changes.PendingPlanKey, Empty(changes.PendingInterval), null, owner ? Empty(changes.PaymentUrl) : null) : null,
            owner ? new(org.PaymentMethodValidated, paymentMethod?.Brand, paymentMethod?.LastFour, paymentMethod?.Expiry) : null, owner);
    }

    private async Task<McpBillingFacts> McpFactsAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var binding = await ReadAsync(context.Organization!.Id, cancellationToken);
        var snapshot = binding is { SubscriptionId.Length: > 0 } ? await PlanSnapshotAsync(context, cancellationToken) : null;
        var status = context.Organization.InternalBilling is not null ? context.Organization.BillingStatus : snapshot?.Subscription.Status ?? binding?.Status ?? "";
        var live = binding is { SubscriptionId.Length: > 0 } && status is not ("canceled" or "incomplete_expired");
        var plan = live ? snapshot?.State.PlanKey ?? binding!.EffectivePlan
            : PlanCatalog.For(context.Organization)?.Key ?? "";
        if (plan.Length == 0) throw new IOException("The current billing plan is unavailable.");
        var interval = context.Organization.InternalBilling is not null ? "monthly" : live ? snapshot?.State.Interval ?? binding!.EffectiveInterval
            : binding is { EffectiveInterval: "monthly" or "annual" } && binding.EffectivePlan == plan ? binding.EffectiveInterval : "";
        return new(binding, snapshot?.State, plan, interval, status, live);
    }

    private static string? Empty(string value) => value.Length == 0 ? null : value;
    private sealed record McpBillingFacts(BillingBinding? Binding, BillingPlanState? State, string PlanKey, string Interval, string SubscriptionStatus, bool HasSubscription);
}
