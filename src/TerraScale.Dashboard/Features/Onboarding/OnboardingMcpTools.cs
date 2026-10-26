using System.ComponentModel;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Features.Onboarding;

[McpServerToolType]
public sealed class OnboardingMcpTools(IHttpContextAccessor http, AccountService accounts, OnboardingService onboarding)
{
    [McpServerTool(Name = "get_onboarding", Title = "Get onboarding", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("Show your account setup progress: the current step, every step's state, and the paid plans the payment step offers.")]
    public async Task<OnboardingStatus> Status()
    {
        var account = (await accounts.GetContextAsync(Account().Session, Cancellation)).Account;
        var step = account.OnboardingCompleted ? "" : account.OnboardingStep;
        var plans = await onboarding.PlansAsync(Cancellation);
        return new(account.OnboardingCompleted, step, OnboardingProgress.Steps(step),
            plans.Select(plan => new OnboardingOfferedPlan(plan.Key, plan.Name, plan.IntervalPriceCents, plan.BillingInterval, plan.PriceCents, plan.Price + " " + plan.PriceUnit, plan.Allowances.MonthlyEgressBytes, plan.TrialDays)).ToArray());
    }

    [McpServerTool(Name = "save_onboarding_step", Title = "Save onboarding step", OpenWorld = true, Destructive = false, UseStructuredContent = true),
     Description("Complete one account setup step. account-information takes displayName; payment-method takes continueOnFree, or plan and interval to start a checkout the user must finish in a browser at checkoutUrl; project takes name and region and finishes setup.")]
    public async Task<OnboardingSaved> Save(
        [Description("Step to save: account-information, payment-method, or project.")] string step,
        [Description("account-information: your display name.")] string displayName = "",
        [Description("payment-method: continue on the Free plan without payment.")] bool continueOnFree = false,
        [Description("payment-method: paid plan key to check out, such as starter or pro.")] string plan = "",
        [Description("payment-method: billing interval, monthly or annual.")] string interval = "",
        [Description("project: project name, up to 100 characters.")] string name = "",
        [Description("project: a region code TerraBase serves, such as GRU.")] string region = "")
    {
        var session = Account().Session;
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["step"] = step,
            ["displayName"] = displayName,
            ["bypass"] = continueOnFree ? "1" : "",
            ["plan"] = plan,
            ["interval"] = interval,
            ["name"] = name,
            ["region"] = region
        });
        string destination;
        try { destination = await onboarding.SaveAsync(session, form, Cancellation); }
        catch (OnboardingValidationException error) { throw new AccountAccessException(error.Message, 422); }
        var account = (await accounts.GetContextAsync(session, Cancellation)).Account;
        return new(account.OnboardingCompleted, account.OnboardingCompleted ? "" : account.OnboardingStep,
            destination is "/" or "/onboarding" ? null : destination);
    }

    private AccountContext Account() => http.HttpContext?.Items[DashboardMcp.ContextKey] as AccountContext
        ?? throw new AccountAccessException("Authentication is required.", 401);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}

public sealed record OnboardingStatus(bool Completed,
    [property: Description("Current step: account-information, payment-method, or project. Empty once setup is complete.")] string Step,
    IReadOnlyList<OnboardingStep> Steps,
    [property: Description("Paid plans the payment step offers.")] IReadOnlyList<OnboardingOfferedPlan> Plans);
public sealed record OnboardingOfferedPlan(string Key, string Name,
    [property: Description(PlanPriceText.PriceCents)] long PriceCents,
    [property: Description(PlanPriceText.BillingInterval)] string BillingInterval,
    [property: Description(PlanPriceText.MonthlyEquivalentCents)] long MonthlyEquivalentCents,
    [property: Description(PlanPriceText.Price)] string Price, long MonthlyEgressBytes, int TrialDays);
public sealed record OnboardingSaved(bool Completed,
    [property: Description("Step to complete next, empty once setup is complete.")] string Step,
    [property: Description("URL to open in a browser to finish payment checkout. This tool never confirms or completes payment.")] string? CheckoutUrl = null);
