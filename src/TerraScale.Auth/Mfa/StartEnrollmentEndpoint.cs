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

internal sealed class StartEnrollmentEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/mfa/enroll/start", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return NotFoundPage();
        var user = await SessionUserAsync(store, sessions, http, instance.Id, ct);
        if (user is null)
            return Results.Redirect(SignIn(slug));

        var factors = await store.ListMfaFactorsAsync(user.Id, ct);
        if (factors.Any(f => f.Type == "totp" && f.Confirmed))
            return Ui.Page("Already enrolled", $"""
                <h1>Already enrolled</h1>
                {Error("You already have a confirmed authenticator. Delete it before enrolling a new one.")}
                <a href="/ui/{slug}/account"><button class="secondary">Back to account</button></a>
                """, status: 409);

        // One enrollment at a time: drop any abandoned half-finished one.
        foreach (var stale in factors.Where(f => f.Type == "totp" && !f.Confirmed))
            await store.DeleteMfaFactorAsync(stale.Id, ct);

        var secret = RandomNumberGenerator.GetBytes(20);
        var factor = await store.CreateMfaFactorAsync(new MfaFactor
        {
            Id = "mfa_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            UserId = user.Id,
            Type = "totp",
            SecretBox = SecretBox.Encrypt(options.MasterKey, secret),
            Confirmed = false,
            CreatedAt = Now(),
        }, ct);

        var base32 = Encodings.Base32Encode(secret);
        var account = user.Email ?? user.Id;
        var otpauth = $"otpauth://totp/ts-auth:{Uri.EscapeDataString(account)}?secret={base32}&issuer=ts-auth";
        return Ui.Page("Set up your authenticator", $"""
            <h1>Set up your authenticator</h1>
            <p class="muted">Add the entry below to your authenticator app, then confirm with a code it generates.</p>
            <label>Secret (base32)</label>
            <div class="row"><code>{base32}</code></div>
            <label>Manual entry URI</label>
            <div class="row"><code>{Ui.Esc(otpauth)}</code></div>
            {ConfirmForm(slug, factor.Id)}
            """);
    }
}
