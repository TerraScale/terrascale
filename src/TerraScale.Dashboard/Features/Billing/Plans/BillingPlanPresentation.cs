using System.Globalization;
using Microsoft.Extensions.Primitives;
using TerraScale.Dashboard.Features.Billing.Invoices;
using TerraScale.Dashboard.Shared.Formatting;

namespace TerraScale.Dashboard.Features.Billing.Plans;

public sealed record BillingLimitWarning(string Title, string Detail);
public sealed record BillingPlanReview(BillingPlan? Plan, string Interval, string Price, string Effect, string Estimate,
    string Error, bool CanUndo, string Action, IReadOnlyList<BillingLimitWarning> Warnings);
public sealed record BillingPlanPresentation(BillingBinding? Binding, BillingPlanState? State, BillingPlanReview? Review, string Error)
{
    public static string Name(string key) => PlanCatalog.Plans.FirstOrDefault(plan => plan.Key == key)?.Name ?? key;
    public static string Scheduled(DateTimeOffset? at) => at is null ? "at the next renewal" : "on " + at.Value.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    public static async Task<BillingPlanPresentation> ForAsync(BillingPage page, StripeBilling billing, IQueryCollection query, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var context = page.Shell.Context;
        var binding = page.Binding;
        var subscribed = binding?.SubscriptionId is { Length: > 0 } && binding.Status is not ("canceled" or "incomplete_expired");
        BillingPlanState? state = null; var stateError = "";
        if (subscribed)
            try { state = await billing.PlanStateAsync(context, cancellationToken); }
            catch (Exception error) when (error is Stripe.StripeException or HttpRequestException or IOException or AccountAccessException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { stateError = "Could not load billing changes. Reload billing to retry."; }
        if (!page.CanManage || query["review"] != "1") return new(binding, state, null, stateError);
        var key = query["plan"].ToString().Trim().ToLowerInvariant(); var interval = query["interval"].ToString().Trim().ToLowerInvariant();
        var plan = page.Plans.FirstOrDefault(plan => plan.Key == key);
        if (interval.Length == 0 && plan is not null) interval = BillingPlan.DefaultInterval;
        string errorMessage = stateError; var canUndo = false; var price = ""; var effect = ""; var estimate = "";
        if (errorMessage.Length == 0 && state?.PendingPlanKey is { Length: > 0 } pending)
            errorMessage = "Stripe is still confirming the change to " + Name(pending) + ". You can change plans again once it’s confirmed.";
        else if (errorMessage.Length == 0 && state?.ScheduledPlanKey is { Length: > 0 } scheduled)
        { errorMessage = "A change to " + Name(scheduled) + " is scheduled to take effect " + Scheduled(state.ScheduledAt) + ". Undo it to choose a different plan."; canUndo = true; }
        else if (errorMessage.Length == 0 && state is { Entitled: false }) errorMessage = "Stripe reports a payment problem with this subscription. Resolve it in Manage payment before changing plans.";
        else if (errorMessage.Length == 0 && state is { CancelAtPeriodEnd: true }) errorMessage = "This subscription ends " + Scheduled(state.RenewalAt) + ". Resume it in Manage payment before changing plans.";
        if (errorMessage.Length == 0)
        {
            if (plan is null || key == "free" && (context.Organization!.Kind != "personal" || !subscribed)) errorMessage = "Choose an available plan.";
            else if (key != "free" && !plan.SupportsInterval(interval)) errorMessage = "Choose the published billing interval for this plan.";
            else
            {
                if (key == "free") interval = "";
                price = plan.PriceFor(interval) + (interval.Length == 0 ? "" : " " + BillingPlan.UnitFor(interval));
                if (!subscribed)
                {
                    effect = binding?.TrialUsed != true && plan.TrialDays > 0 ? "After Stripe confirms the " + plan.TrialDays + "-day trial" : "After Stripe confirms checkout";
                    estimate = "Stripe shows the final amount in Checkout";
                }
                else
                    try
                    {
                        var quote = await billing.PreviewPlanChangeAsync(context, key, interval, cancellationToken);
                        effect = quote.Immediate ? "After Stripe confirms the change and payment" : "At renewal on " + quote.EffectiveAt.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
                        estimate = quote.Immediate ? MoneyFormat.Money(quote.AmountDueCents, quote.Currency) + " estimated by Stripe (including " + MoneyFormat.Money(quote.ProrationCents, quote.Currency) + " in prorations)" : "No charge today";
                    }
                    catch (AccountAccessException access) { errorMessage = access.Message; }
                    catch (Exception error) when (error is Stripe.StripeException or HttpRequestException or IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    { errorMessage = "Stripe could not calculate the estimated charge. Try again."; }
            }
        }
        return new(binding, state, new(plan, interval, price, effect, estimate, errorMessage, canUndo, subscribed ? "/billing/change" : "/billing/checkout", plan is null ? [] : Warnings(context.Organization!, plan, now)), stateError);
    }
    private static IReadOnlyList<BillingLimitWarning> Warnings(OrganizationRecord org, BillingPlan plan, DateTimeOffset now)
    {
        var limits = PlanCatalog.ResourceLimits(plan.Key); var warnings = new List<BillingLimitWarning>();
        if (limits.Projects > 0 && org.Projects.Count > limits.Projects)
            warnings.Add(new("More projects than " + plan.Name + " allows", $"{org.Name} has {org.Projects.Count} projects and {plan.Name} allows {limits.Projects}. Existing projects stay, but new projects can’t be created until the organization is below the limit."));
        var databases = org.Projects.Values.Sum(project => project.Databases.Count);
        if (limits.Databases > 0 && databases > limits.Databases)
            warnings.Add(new("More databases than " + plan.Name + " allows", $"{org.Name} has {databases} databases and {plan.Name} allows {limits.Databases}. Existing databases stay, but new ones can’t be created until it’s under the limit."));
        var seats = org.Members.Values.Count(member => member.Status == "active") + org.Invitations.Values.Count(invitation => invitation.Status == "pending" && invitation.ExpiresAt > now);
        if (limits.Members > 0 && seats > limits.Members)
            warnings.Add(new("More members than " + plan.Name + " allows", $"{org.Name} has {seats} members and pending invitations and {plan.Name} allows {limits.Members}. Current members keep access, but new invitations are blocked until it’s under the limit."));
        return warnings;
    }
}
