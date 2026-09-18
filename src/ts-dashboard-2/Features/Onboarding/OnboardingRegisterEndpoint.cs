using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Onboarding;

/// <summary>Account registration (activation step 1). Validates the form the
/// way ts-dashboard's client validation does, starts the demo session, and
/// returns to the wizard for the next step.</summary>
public sealed class OnboardingRegisterEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    private const int MinPasswordLength = 12;

    public override void Configure()
    {
        Post("/onboarding/register");
        Summary("Creates the account from the onboarding registration form");
        Tags("Onboarding");
        AllowAnonymous();
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var displayName = form["displayName"].ToString().Trim();
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        var confirmPassword = form["confirmPassword"].ToString();
        var error = Validate(displayName, email, password, confirmPassword, form["terms"] == "true");
        if (error is { Length: > 0 })
        {
            return Results.Redirect("/onboarding?error=" + Uri.EscapeDataString(error), false, false);
        }

        store.CompleteOnboardingStep("account-information");
        HttpContext.Response.Cookies.Append(
            DashboardAuthMiddleware.SessionCookie,
            DashboardAuthMiddleware.SessionToken,
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
        return Results.Redirect("/onboarding", false, false);
    }

    private static string? Validate(string displayName, string email, string password, string confirmPassword, bool acceptedTerms) =>
        displayName.Length == 0 ? "This field is required."
        : email.Length == 0 || !email.Contains('@') || !email.Contains('.') ? "Enter a valid email address."
        : password.Length == 0 ? "This field is required."
        : password.Length < MinPasswordLength ? "Use at least 12 characters."
        : confirmPassword != password ? "Passwords do not match."
        : !acceptedTerms ? "You must accept the terms to continue."
        : null;
}
