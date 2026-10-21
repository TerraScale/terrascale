using System.Net.Mail;
using System.Text;
using TerraScale.Dashboard.Features.Auth;
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
        app.MapPost("/onboarding", RegisterAsync);
        app.MapPost("/onboarding/step", SaveAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext context, DashboardSessions sessions, AccountService accounts, OnboardingService service)
    {
        var plans = await service.PlansAsync(context.RequestAborted);
        var requested = context.Request.Query["plan"].ToString();
        var returnTo = context.Request.Query["returnTo"].ToString();
        if (returnTo.Length == 0) returnTo = context.Request.Query["return"].ToString();
        var page = Page(context) with { Plans = plans, Plan = OfferedPlan(plans, requested), ReturnTo = ReturnPath(returnTo) };
        var session = await sessions.ReadAsync(context);
        if (session is null) return OnboardingView.Create(page);
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
            Registration = false,
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

    private static async Task<IResult> RegisterAsync(HttpContext context, OnboardingService service, IAuthEnrollmentGateway auth, AuthFlows flows, TimeProvider time)
    {
        if (!context.Request.HasFormContentType) return Results.BadRequest();
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Csrf.Validate(context, form)) return Results.StatusCode(403);
        var plans = await service.PlansAsync(context.RequestAborted);
        var page = Page(context) with
        {
            DisplayName = form["displayName"].ToString(),
            Email = form["email"].ToString(),
            AcceptedTerms = form["terms"] == "true",
            Plans = plans,
            Plan = OfferedPlan(plans, form["plan"]),
            ReturnTo = ReturnPath(form["returnTo"])
        };
        try
        {
            var name = OnboardingService.DisplayName(page.DisplayName);
            var email = page.Email.Trim().ToLowerInvariant();
            if (email.Length == 0) throw new OnboardingValidationException("Enter your email address.", "email");
            if (email.Length > 254 || email.Any(char.IsControl) || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email ||
                !email[(email.IndexOf('@') + 1)..].Contains('.')) throw new OnboardingValidationException("Enter a valid email address, like name@company.com.", "email");
            var password = form["password"].ToString();
            if (password.Length == 0) throw new OnboardingValidationException("Enter a password.", "password");
            if (password.EnumerateRunes().Count() < 12) throw new OnboardingValidationException("Use a password with at least 12 characters.", "password");
            if (Encoding.UTF8.GetByteCount(password) > 1024) throw new OnboardingValidationException("Use a password with 1024 bytes or fewer.", "password");
            if (form["confirmPassword"].ToString().Length == 0) throw new OnboardingValidationException("Enter your password again.", "confirmPassword");
            if (password != form["confirmPassword"]) throw new OnboardingValidationException("Passwords do not match.", "confirmPassword");
            if (!page.AcceptedTerms) throw new OnboardingValidationException("Accept the Terms of Service to continue.", "terms");
            await context.RequestServices.GetRequiredService<IAuthAttemptBudget>().ReserveAsync(context, email, AuthBudgetKind.SignUpMail, context.RequestAborted);
            await flows.ReserveSignUpMailAsync(email, context.RequestAborted);
            var pending = await auth.SignUpAsync(email, password, EnrollmentEndpoints.ClientIp(context), context.RequestAborted);
            var location = page.ReturnTo.Length != 0 ? page.ReturnTo : "/onboarding" + (page.Plan.Length == 0 ? "" : "?plan=" + Uri.EscapeDataString(page.Plan));
            var now = time.GetUtcNow();
            var flow = new SignUpFlow(name, email, password, pending, location, now.AddHours(1), now.AddMinutes(1));
            await flows.StartSignUpAsync(context, flow);
            return AuthView.Create(EnrollmentEndpoints.SignUpPage(context, flow, flows));
        }
        catch (OnboardingValidationException error) { return Failure(page with { Error = error.Message, ErrorField = error.Field }, 422); }
        catch (AuthRequestRejectedException error)
        {
            if (error.RetryAfter > 0) context.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Failure(page with { Error = error.Message, ErrorField = error.Field }, error.StatusCode);
        }
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
    private static RazorSlices.RazorSlice Failure(OnboardingPage page, int status)
    {
        var view = OnboardingView.Create(page); view.StatusCode = status; return view;
    }
}
