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

internal sealed class GetPasscodePageEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/passcode", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        return instance is null ? NotFoundPage() : Ui.Page("Sign in with a code", PasscodePageBody(slug));
    }

    private static string PasscodePageBody(string slug) => $$"""
        <h1>Sign in with a code</h1>
        <div id="message" class="muted">We'll email you a one-time code.</div>
        <label for="email">Email</label>
        <input id="email" type="email" autocomplete="username" required>
        <button id="send">Send code</button>
        <label for="code">Code</label>
        <input id="code" inputmode="numeric" autocomplete="one-time-code" required>
        <button id="verify">Verify</button>
        <p class="hint"><a href="/ui/{{slug}}/sign-in">Back to sign in</a></p>
        <script>
        const message = document.getElementById('message');
        async function post(path, body) {
            const response = await fetch(path, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
            return { ok: response.ok, body: await response.json().catch(() => ({})) };
        }
        document.getElementById('send').onclick = async () => {
            message.textContent = 'Sending…';
            await post('/ui/{{slug}}/passcode/request', { email: document.getElementById('email').value });
            message.textContent = 'If that address has an account, a code is on its way.';
        };
        document.getElementById('verify').onclick = async () => {
            const result = await post('/ui/{{slug}}/passcode/verify', {
                email: document.getElementById('email').value,
                code: document.getElementById('code').value,
            });
            if (result.body.mfa_required)
                window.location = '/ui/{{slug}}/passcode/mfa?token=' + encodeURIComponent(result.body.mfa_token);
            else if (result.body.ok)
                window.location = '/ui/{{slug}}/account';
            else
                message.textContent = 'That code is wrong or has expired.';
        };
        </script>
        """;
}
