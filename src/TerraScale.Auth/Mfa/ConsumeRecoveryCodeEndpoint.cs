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

internal sealed class ConsumeRecoveryCodeEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/mfa/recovery/consume", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return NotFoundPage();
        var user = await SessionUserAsync(store, sessions, http, instance.Id, ct);
        if (user is null)
            return Results.Redirect(SignIn(slug));

        var code = await FormValueAsync(http, "code", ct);
        if (string.IsNullOrWhiteSpace(code) || !await MfaGate.VerifyCodeAsync(store, options.MasterKey, user.Id, code.Trim(), ct))
            return Ui.Page("Use a recovery code", $"""
                <h1>Use a recovery code</h1>
                {Error("That recovery code is invalid or already used.")}
                <a href="/ui/{slug}/sign-in"><button class="secondary">Back to sign-in</button></a>
                """);

        // VerifyCodeAsync consumed the code above; issue a fresh session.
        var (_, cookieToken) = await sessions.CreateSessionAsync(
            instance.Id, user.Id, http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString(), ct);
        http.Response.Cookies.Append(SessionCookies.Name, cookieToken, SessionCookies.Options);
        return Results.Redirect($"/ui/{slug}/account");
    }
}
