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

internal sealed class VerifyMfaEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/mfa/verify", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return NotFoundPage();
        var token = await FormValueAsync(http, "token", ct);
        var next = await FormValueAsync(http, "next", ct);
        if (token is not { Length: > 0 } || await PeekPendingAsync(store, instance.Id, token, ct) is not { } userId)
            return ExpiredPage(slug);

        var code = await FormValueAsync(http, "code", ct);
        // A wrong guess keeps the pending token alive so the user can retry;
        // guess attempts are capped by the token's 10-minute TTL, not a counter.
        if (string.IsNullOrWhiteSpace(code) || !await MfaGate.VerifyCodeAsync(store, options.MasterKey, userId, code.Trim(), ct))
            return CodeFormPage(slug, token, next, "Invalid code. Try again.");

        // Consume-before-session: the consume is atomic, so a concurrent replay
        // of this token loses here and never mints a second session.
        if (await MfaGate.ConsumePendingAsync(store, instance.Id, token, ct) is null)
            return ExpiredPage(slug);

        var (_, cookieToken) = await sessions.CreateSessionAsync(
            instance.Id, userId, http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString(), ct);
        var user = await store.GetUserAsync(userId, ct);
        if (user is not null)
            await store.UpsertUserAsync(user with { LastSignInAt = Now(), UpdatedAt = Now() }, ct);
        await AuditAsync(store, instance.Id, "user.signed_in", userId, new() { ["method"] = "password+mfa" }, ct);

        http.Response.Cookies.Append(SessionCookies.Name, cookieToken, SessionCookies.Options);
        return Results.Redirect(SafeDestination(slug, next));
    }
}
