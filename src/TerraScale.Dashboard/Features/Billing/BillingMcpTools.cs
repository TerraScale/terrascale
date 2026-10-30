using TerraScale.TerraBase.Contracts;
using System.ComponentModel;
using ModelContextProtocol.Server;
using Stripe;
using TerraScale.Dashboard.Features.Billing.Invoices;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Common.Validation;

namespace TerraScale.Dashboard.Features.Billing
{
    [McpServerToolType]
    public sealed class BillingMcpTools(IHttpContextAccessor http, StripeBilling billing, TimeProvider time, ILogger<BillingMcpTools> logger)
    {
        [McpServerTool(Name = "cancel_checkout", Title = "Cancel plan checkout", OpenWorld = true, Destructive = false, UseStructuredContent = true), Description("Cancel the organization's checkout in progress without changing its plan. Owner only.")]
        public Task<CheckoutCanceled> CancelCheckout(string? organizationId = null, string? projectId = null)
        {
            return Invoke(async () =>
        {
            await billing.CancelCheckoutAsync(Scope(organizationId, projectId), "", Cancellation);
            return new CheckoutCanceled(true);
        });
        }

        private const string IntervalDescription = "Billing interval: monthly (or list_plans' month) or annual (or year). Omit for monthly, and for free.";

        // list_plans publishes Stripe's month/year; checkout and plan changes take monthly/annual. Accept both and name the valid one.
        private static string Interval(string plan, string interval)
        {
            interval = interval.Trim() switch { "year" => "annual", "month" => "monthly", var value => value };
            if (interval.Length == 0 || PlanCatalog.Plans.SingleOrDefault(item => item.Key == plan) is not { PriceCents: > 0 } paid || paid.SupportsInterval(interval))
            {
                return interval;
            }

            throw new AccountAccessException($"{paid.Name} is billed {string.Join(" or ", paid.CheckoutIntervals)}: pass one of those or omit it.", 400);
        }

        [McpServerTool(Name = "preview_plan_change", Title = "Preview plan change", ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false, UseStructuredContent = true), Description("Estimate the charge and effective date of changing a subscribed organization's plan, without changing it. Owner only.")]
        public Task<BillingPlanPreview> PreviewPlanChange(
            [Description("Plan key from list_plans, such as starter or pro. change_plan and preview_plan_change also accept free for Personal workspaces.")] string plan,
            [Description(IntervalDescription)] string interval = "",
            string? organizationId = null, string? projectId = null)
        {
            return Invoke(() => billing.PreviewPlanChangeAsync(Scope(organizationId, projectId), plan, Interval(plan, interval), Cancellation));
        }

        [McpServerTool(Name = "change_plan", Title = "Change plan", OpenWorld = true, Idempotent = true, Destructive = false, UseStructuredContent = true), Description("Change a subscribed organization's plan. Upgrades apply now (charging a prorated amount); downgrades and switching a Personal workspace to free are scheduled for renewal and can be undone with undo_plan_change. Owner only.")]
        public Task<PlanChanged> ChangePlan(
            [Description("Plan key from list_plans, such as starter or pro. change_plan and preview_plan_change also accept free for Personal workspaces.")] string plan,
            [Description(IntervalDescription)] string interval = "",
            string? organizationId = null, string? projectId = null)
        {
            return Invoke(async () =>
            new PlanChanged(await billing.ChangePlanAsync(Scope(organizationId, projectId), plan, Interval(plan, interval), Cancellation)));
        }

        [McpServerTool(Name = "undo_plan_change", Title = "Undo scheduled plan change", OpenWorld = true, Idempotent = true, Destructive = false, UseStructuredContent = true), Description("Cancel a plan change scheduled for renewal so the current plan continues. Owner only.")]
        public Task<PlanChanged> UndoPlanChange(string? organizationId = null, string? projectId = null)
        {
            return Invoke(async () =>
            new PlanChanged(await billing.UndoPlanChangeAsync(Scope(organizationId, projectId), Cancellation)));
        }

        [McpServerTool(Name = "list_plans", Title = "List plans", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = true, Idempotent = true), Description("List the paid plans an organization can subscribe to, with prices in cents, limits, and which plan is current.")]
        public Task<BillingMcpPlans> Plans(string? organizationId = null, string? projectId = null)
        {
            return billing.McpPlansAsync(Scope(organizationId, projectId), Cancellation);
        }

        [McpServerTool(Name = "get_billing", Title = "Get billing", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = true, Idempotent = true), Description("Show an organization's plan, billing interval and status, renewal date, scheduled or pending plan changes, checkout state, and (for the owner) payment method summary.")]
        public Task<BillingMcpSummary> Billing(string? organizationId = null, string? projectId = null)
        {
            return billing.McpSummaryAsync(Scope(organizationId, projectId), Cancellation);
        }

        [McpServerTool(Name = "list_invoices", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true), Description("List organization invoices from Stripe newest first, ten per page, filtered by status or issued date.")]
        public async Task<BillingInvoiceList> Invoices(
            [Description("paid, open, void, or empty for all statuses.")] string status = "",
            [Description("3m, 6m, 12m, a four-digit year other than 0000, or empty for all dates.")] string issued = "",
            [Description("1-based page, ten invoices per page; clamped to available pages.")] int page = 1,
            string? organizationId = null, string? projectId = null)
        {
            var invoices = await billing.InvoicesAsync(Scope(organizationId, projectId), Cancellation);
            return InvoiceHistory.Page(invoices, status, issued, page, time.GetUtcNow());
        }

        [McpServerTool(Name = "create_checkout", Title = "Start plan checkout", OpenWorld = true, Destructive = false, UseStructuredContent = true), Description("Start a checkout that subscribes an organization without a subscription to a paid plan. Returns the URL the owner must open in a browser to pay; this tool never completes a payment. Owner only.")]
        public async Task<CheckoutStarted> Checkout(
            [Description("Plan key from list_plans, such as starter or pro. change_plan and preview_plan_change also accept free for Personal workspaces.")] string plan,
            [Description(IntervalDescription)] string interval = "", string? organizationId = null, string? projectId = null)
        {
            if (PlanCatalog.For(plan).PriceCents <= 0)
            {
                throw new AccountAccessException("Choose an available paid plan and a monthly or annual interval.", 400);
            }

            var url = await billing.StartCheckoutAsync(Scope(organizationId, projectId), plan, Interval(plan, interval), "/organization?tab=billing", Cancellation);
            return new(url);
        }

        [McpServerTool(Name = "get_billing_portal_url", Title = "Open billing portal", OpenWorld = true, Destructive = false, UseStructuredContent = true), Description("Create a billing portal session where the owner updates payment methods and downloads invoices. Returns the URL to open in a browser. Owner only.")]
        public async Task<BillingMcpPortal> Portal(string? organizationId = null, string? projectId = null)
        {
            return new(await billing.PortalAsync(Scope(organizationId, projectId), Cancellation));
        }

        private AccountContext Scope(string? organizationId, string? projectId)
        {
            return DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
        }

        private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;

        private async Task<T> Invoke<T>(Func<Task<T>> action)
        {
            try { return await action(); }
            catch (OnboardingValidationException error) { throw new AccountAccessException(error.Message, 400); }
            catch (Exception error) when (error is StripeException or TerraBaseException or HttpRequestException or IOException ||
                                          error is OperationCanceledException && !Cancellation.IsCancellationRequested)
            {
                logger.BillingMCPToolFailed(error.GetType().Name, BillingPlanEndpoints.Describe(error));
                throw new AccountAccessException("Stripe could not complete this billing change. Reload billing and try again.", 502);
            }
        }
    }

    public sealed record CheckoutStarted(
        [property: Description("URL the organization owner must open in a browser signed in to the dashboard to review and confirm payment. Relative URLs are dashboard pages.")] string CheckoutUrl);
    public sealed record CheckoutCanceled(bool Canceled);
    public sealed record PlanChanged(
        [property: Description("current: the plan was already active; changed: the upgrade applied now; pending: the upgrade waits for Stripe to confirm payment; scheduled: the change applies at renewal; undone: the scheduled change was canceled.")] string Outcome);
}
