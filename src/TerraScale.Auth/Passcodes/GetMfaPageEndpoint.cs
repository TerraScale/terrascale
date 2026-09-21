namespace TerraScale.Auth.Passcodes;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.Endpoints;
using static TerraScale.Auth.Passcodes.PasscodeSupport;

internal sealed class GetMfaPageEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/passcode/mfa", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string? token, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return NotFoundPage();
        return await PeekPendingAsync(store, instance.Id, token, ct) is null
            ? ExpiredPage(slug)
            : Ui.Page("Two-factor verification", MfaFormBody(slug, token!));
    }

    private static string MfaFormBody(string slug, string token) => $$"""
        <h1>Two-factor verification</h1>
        <p class="muted">Enter the code from your authenticator app to finish signing in.</p>
        <form method="post" action="/ui/{{slug}}/passcode/mfa">
        <input type="hidden" name="mfa_token" value="{{Ui.Esc(token)}}">
        <label for="code">Authenticator code</label>
        <input id="code" name="code" inputmode="numeric" autocomplete="one-time-code" autofocus required>
        <button>Verify</button>
        </form>
        <p class="hint"><a href="/ui/{{slug}}/sign-in">Back to sign in</a></p>
        """;

    private static IResult ExpiredPage(string slug) => Ui.Page("Sign-in expired", $"""
        <h1>Your sign-in expired</h1>
        <div class="error">This step is no longer valid — it may have timed out. Please sign in again.</div>
        <a href="/ui/{slug}/sign-in"><button class="secondary">Back to sign-in</button></a>
        """);
}
