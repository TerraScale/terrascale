namespace TerraScale.Auth.Mfa;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.Endpoints;
using static TerraScale.Auth.Mfa.MfaSupport;

internal sealed class GetEnrollmentPageEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/mfa/enroll", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpContext http, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return NotFoundPage();
        if (await SessionUserAsync(store, sessions, http, instance.Id, ct) is null)
            return Results.Redirect(SignIn(slug));

        return Ui.Page("Two-factor authentication", $"""
            <h1>Two-factor authentication</h1>
            <p class="muted">Protect your account with a time-based one-time password (TOTP) app. After enrolling you'll enter a 6-digit code at every sign-in.</p>
            <form method="post" action="/ui/{slug}/mfa/enroll/start">
            <button>Set up authenticator</button>
            </form>
            """);
    }
}
