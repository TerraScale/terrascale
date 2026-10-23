using TerraScale.Dashboard.Shared.UI;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Features.Onboarding;

public static class OnboardingEndpoints
{
    public static void AddOnboarding(this IServiceCollection services) => services.AddSingleton<OnboardingService>();

    public static void MapOnboarding(this WebApplication app)
    {
        app.MapGet("/register", (HttpContext context) => HttpRedirects.SeeOther("/onboarding" + context.Request.QueryString));
        app.MapGet("/onboarding", GetAsync);
        app.MapPost("/onboarding/step", SaveAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext context, DashboardSessions sessions, AccountService accounts, OnboardingService service)
    {
        var plans = await service.PlansAsync(context.RequestAborted);
        var requested = context.Request.Query["plan"].ToString();
        var returnTo = context.Request.Query["returnTo"].ToString();
        if (returnTo.Length == 0) returnTo = context.Request.Query["return"].ToString();
        var session = await sessions.ReadAsync(context);
        // Accounts are created on ts-auth's hosted sign-up, reached from its sign-in page.
        if (session is null)
        {
            var target = ReturnPath(returnTo) is { Length: > 0 } path ? path : "/onboarding" + (OfferedPlan(plans, requested) is { Length: > 0 } plan ? "?plan=" + Uri.EscapeDataString(plan) : "");
            return HttpRedirects.SeeOther("/login?returnTo=" + Uri.EscapeDataString(target));
        }
        var page = Page(context) with { Plans = plans, Plan = OfferedPlan(plans, requested) };
        var account = await accounts.EnsureAsync(session, context.RequestAborted);
        if (account.OnboardingCompleted) return HttpRedirects.SeeOther("/");
        var payment = context.Request.Query["payment"].ToString();
        if (payment == "success" && account.OnboardingStep == "payment-method")
        {
            await service.ConfirmPaymentAsync(session, context.RequestAborted);
            account = await accounts.EnsureAsync(session, context.RequestAborted);
        }
        page = page with
        {
            DisplayName = account.Name,
            Email = account.Email,
            Step = account.OnboardingStep,
            PaymentCanceled = payment == "canceled",
            Error = context.Request.Query["error"].ToString(),
            Plan = OfferedPlan(plans, requested).Length == 0 ? plans.FirstOrDefault()?.Key ?? "" : requested,
            Document = new("Activate your TerraScale account", DocumentModel.NonceFor(context))
        };
        if (payment == "success" && page.Step == "payment-method")
            page = page with { Error = "Payment setup could not be confirmed. Finish checkout or continue without payment." };
        return OnboardingView.Create(page);
    }

    private static async Task<IResult> SaveAsync(HttpContext context, DashboardSessions sessions, OnboardingService service)
    {
        if (!context.Request.HasFormContentType) return Results.BadRequest();
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Csrf.Validate(context, form)) return Results.StatusCode(403);
        var session = await sessions.ReadAsync(context);
        if (session is null) return HttpRedirects.SeeOther("/login");
        try { return HttpRedirects.SeeOther(await service.SaveAsync(session, form, context.RequestAborted)); }
        catch (OnboardingValidationException error)
        {
            var target = "/onboarding?error=" + Uri.EscapeDataString(error.Message);
            if (form["step"] == "payment-method")
            {
                var plans = await service.PlansAsync(context.RequestAborted);
                if (OfferedPlan(plans, form["plan"]) is { Length: > 0 } plan) target += "&plan=" + Uri.EscapeDataString(plan);
            }
            return HttpRedirects.SeeOther(target);
        }
        catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
    }

    private static OnboardingPage Page(HttpContext context) => new(new("Create account", DocumentModel.NonceFor(context)), Csrf.Token(context));
    private static string OfferedPlan(IReadOnlyList<BillingPlan> plans, string? raw) => plans.Any(plan => plan.Key == raw) ? raw! : "";
    private static string ReturnPath(string? value) => Csrf.LocalRedirect(value) is { } path && path != "/" ? path : "";
}
