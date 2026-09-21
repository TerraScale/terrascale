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

internal sealed class GetRecoveryPageEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/mfa/recovery", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpContext http, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return NotFoundPage();
        var user = await SessionUserAsync(store, sessions, http, instance.Id, ct);
        if (user is null)
            return Results.Redirect(SignIn(slug));

        var hasCodes = (await store.ListMfaFactorsAsync(user.Id, ct))
            .Any(f => f.Type == "recovery" && f.RecoveryCodes is { Length: > 0 });
        if (!hasCodes)
            return Ui.Page("No recovery codes", $"""
                <h1>No recovery codes left</h1>
                {Error("You have no unused recovery codes. Ask an administrator to reset your second factor.")}
                """);

        return Ui.Page("Use a recovery code", $"""
            <h1>Use a recovery code</h1>
            <p class="muted">Lost your authenticator? Enter one of your one-time recovery codes to unlock your account.</p>
            <form method="post" action="/ui/{slug}/mfa/recovery/consume">
            <label for="code">Recovery code</label>
            <input id="code" name="code" autocomplete="off" autofocus required>
            <button>Unlock account</button>
            </form>
            """);
    }
}
