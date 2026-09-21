namespace TerraScale.Auth.Users;

using System.Net;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Users.UserFlow;

internal sealed class SignInEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapMethods("/ui/{slug}/sign-in", [HttpMethods.Get, HttpMethods.Post], HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, AuthOptions options, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        var queryNext = request.Query["next"].ToString();
        if (HttpMethods.IsGet(request.Method))
            return SignInPage(slug, instance, next: queryNext);

        var form = await request.ReadFormAsync(ct);
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        var requested = form["next"].ToString() is { Length: > 0 } formNext ? formNext : queryNext;

        var user = await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(email), ct);
        if (!SessionService.VerifySignInPassword(user, password))
            return SignInPage(slug, instance, "Invalid email or password.", email, requested);
        if (user!.Banned)
            return SignInPage(slug, instance, "This account is suspended.", email, requested);

        if (await MfaGate.RequiredForUserAsync(store, user.Id, ct))
        {
            var (pending, _) = await MfaGate.BeginPendingAsync(store, instance.Id, user.Id, ct);
            var safe = SafeNext(requested, $"/ui/{slug}/account", options);
            return Results.Redirect($"/ui/{slug}/mfa/verify?token={pending}&next={Uri.EscapeDataString(safe)}");
        }

        await EstablishSessionAsync(store, sessions, request.HttpContext, instance, user, "password", ct);
        return Results.Redirect(SafeNext(requested, $"/ui/{slug}/account", options));
    }

    private static IResult SignInPage(string slug, Instance brand, string? error = null, string email = "", string next = "") =>
        Ui.Page("Sign in", $"""
            <h1>Sign in</h1>
            {ErrorDiv(error)}
            <form method="post">
            <input type="hidden" name="next" value="{Ui.Esc(next)}">
            <label for="si-email">Email</label>
            <input id="si-email" name="email" type="email" value="{Ui.Esc(email)}" required autofocus>
            <label for="si-password">Password</label>
            <input id="si-password" name="password" type="password" required>
            <button type="submit">Sign in</button>
            </form>
            <form method="post" action="/ui/{slug}/magic-link">
            <label for="si-magic">Or email a magic link</label>
            <input id="si-magic" name="email" type="email" value="{Ui.Esc(email)}" required>
            <button type="submit" class="secondary">Send magic link</button>
            </form>
            <p class="hint"><a href="/ui/{slug}/passkeys/assert">Sign in with a passkey</a> · <a href="/ui/{slug}/passcode">Sign in with an email code</a></p>
            <p class="hint"><a href="/ui/{slug}/connections/list">Sign in with SSO</a></p>
            <p class="hint"><a href="/ui/{slug}/forgot-password">Forgot your password?</a> · <a href="/ui/{slug}/sign-up">Create an account</a></p>
            """, themeColor: brand.ThemeColor, logoUrl: brand.LogoUrl);

    /// <summary>Only same-site relative paths or this deployment's own origin are allowed redirect targets.</summary>
    private static string SafeNext(string? next, string fallback, AuthOptions options)
    {
        if (next is { Length: > 0 })
        {
            // Browsers treat "\" as "/" and strip control characters before parsing a URL,
            // so "/\evil" or "/\t/evil" must not pass as site-relative paths.
            if (next.StartsWith('/') && !next.StartsWith("//") && !next.Contains('\\') && !next.Any(char.IsControl))
                return next;
            var origin = options.PublicOrigin.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            if (string.Equals(next, origin, StringComparison.OrdinalIgnoreCase)
                || next.StartsWith(origin + "/", StringComparison.OrdinalIgnoreCase))
                return next;
        }
        return fallback;
    }
}
