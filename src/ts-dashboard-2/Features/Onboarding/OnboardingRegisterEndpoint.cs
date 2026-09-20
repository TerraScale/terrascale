using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Onboarding;

/// <summary>Account registration (activation step 1). Validates the form the
/// way ts-dashboard's client validation does. Demo mode creates the in-memory
/// world directly; live mode creates the user at ts-auth (the SDK's auth
/// service, management API) and signs in through the OIDC token exchange —
/// the world itself materializes from Terrabase state on the first request.</summary>
public sealed class OnboardingRegisterEndpoint(DashboardStore store, DashboardSecurityOptions security, LiveAuthSessionService? liveSessions = null) : EndpointWithoutRequest<IResult>
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
        if (error is null && !security.DemoMode)
        {
            error = await liveSessions!.RegisterAsync(email, password, displayName, ct);
        }
        else if (error is null && store.FindAccount(email) is not null)
        {
            error = "An account with this email already exists.";
        }

        if (error is { Length: > 0 })
        {
            return Results.Redirect("/onboarding?error=" + Uri.EscapeDataString(error), false, false);
        }

        string token;
        if (!security.DemoMode)
        {
            var signIn = await liveSessions!.SignInAsync(email, password, ct);
            if (!signIn.Ok)
            {
                return Results.Redirect("/onboarding?error=" + Uri.EscapeDataString("The account was created but sign-in failed. Try signing in."), false, false);
            }

            token = liveSessions.IssueCookie(signIn.Session!);
        }
        else
        {
            var world = AccountWorld.Fresh(displayName, email);
            world.AccountPassword = password;
            token = store.BeginSession(world);
        }

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
