using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Onboarding;

/// <summary>Account registration (activation step 1). Validates the form the
/// way ts-dashboard's client validation does, creates the account's world in
/// the in-memory store, starts its session, and returns to the wizard.</summary>
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
        if (error is null && store.FindAccount(email) is not null)
        {
            error = "An account with this email already exists.";
        }

        if (error is { Length: > 0 })
        {
            return Results.Redirect("/onboarding?error=" + Uri.EscapeDataString(error), false, false);
        }

        var world = AccountWorld.Fresh(displayName, email);
        world.AccountPassword = password;
        var token = store.BeginSession(world);
        HttpContext.Response.Cookies.Append(
            DashboardAuthMiddleware.SessionCookie,
            token,
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
