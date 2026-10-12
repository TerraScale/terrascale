using System.ComponentModel;
using ModelContextProtocol.Server;
using Stripe;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Billing;

[McpServerToolType]
public sealed class BillingMcpTools(IHttpContextAccessor http, StripeBilling billing)
{
    [McpServerTool(Name = "cancel_checkout", Title = "Cancel plan checkout", OpenWorld = true, Destructive = false, UseStructuredContent = true), Description("Cancel the organization's checkout in progress without changing its plan. Owner only.")]
    public Task<CheckoutCanceled> CancelCheckout(string? organizationId = null, string? projectId = null) => Invoke(async () =>
    {
        await billing.CancelCheckoutAsync(Scope(organizationId, projectId), "", Cancellation);
        return new CheckoutCanceled(true);
    });

    [McpServerTool(Name = "preview_plan_change", Title = "Preview plan change", ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false, UseStructuredContent = true), Description("Estimate the charge and effective date of changing a subscribed organization's plan, without changing it. Owner only.")]
    public Task<BillingPlanPreview> PreviewPlanChange(
        [Description("Plan key from list_plans, such as starter, pro, or business. change_plan and preview_plan_change also accept free for Personal workspaces.")] string plan,
        [Description("Billing interval: monthly or annual. Omit for free.")] string interval = "",
        string? organizationId = null, string? projectId = null) =>
        Invoke(() => billing.PreviewPlanChangeAsync(Scope(organizationId, projectId), plan, interval, Cancellation));

    [McpServerTool(Name = "change_plan", Title = "Change plan", OpenWorld = true, Idempotent = true, Destructive = false, UseStructuredContent = true), Description("Change a subscribed organization's plan. Upgrades apply now (charging a prorated amount); downgrades and switching a Personal workspace to free are scheduled for renewal and can be undone with undo_plan_change. Owner only.")]
    public Task<PlanChanged> ChangePlan(
        [Description("Plan key from list_plans, such as starter, pro, or business. change_plan and preview_plan_change also accept free for Personal workspaces.")] string plan,
        [Description("Billing interval: monthly or annual. Omit for free.")] string interval = "",
        string? organizationId = null, string? projectId = null) => Invoke(async () =>
        new PlanChanged(await billing.ChangePlanAsync(Scope(organizationId, projectId), plan, interval, Cancellation)));

    [McpServerTool(Name = "undo_plan_change", Title = "Undo scheduled plan change", OpenWorld = true, Idempotent = true, Destructive = false, UseStructuredContent = true), Description("Cancel a plan change scheduled for renewal so the current plan continues. Owner only.")]
    public Task<PlanChanged> UndoPlanChange(string? organizationId = null, string? projectId = null) => Invoke(async () =>
        new PlanChanged(await billing.UndoPlanChangeAsync(Scope(organizationId, projectId), Cancellation)));

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;

    private async Task<T> Invoke<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (OnboardingValidationException error) { throw new AccountAccessException(error.Message, 400); }
        catch (Exception error) when (error is StripeException or HttpRequestException or IOException ||
                                      error is OperationCanceledException && !Cancellation.IsCancellationRequested)
        { throw new AccountAccessException("Stripe could not complete this billing change. Reload billing and try again.", 502); }
    }
}

public sealed record CheckoutStarted(
    [property: Description("URL the organization owner must open in a browser signed in to the dashboard to review and confirm payment. Relative URLs are dashboard pages.")] string CheckoutUrl);
public sealed record CheckoutCanceled(bool Canceled);
public sealed record PlanChanged(
    [property: Description("current: the plan was already active; changed: the upgrade applied now; pending: the upgrade waits for Stripe to confirm payment; scheduled: the change applies at renewal; undone: the scheduled change was canceled.")] string Outcome);
