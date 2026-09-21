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

internal sealed class ConfirmEnrollmentEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/mfa/enroll/confirm", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string? factor, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return NotFoundPage();
        var user = await SessionUserAsync(store, sessions, http, instance.Id, ct);
        if (user is null)
            return Results.Redirect(SignIn(slug));

        var record = factor is null ? null : await store.GetMfaFactorAsync(factor, ct);
        if (record is null || record.InstanceId != instance.Id || record.UserId != user.Id || record.Type != "totp" || record.Confirmed)
            return NotFoundPage();
        var code = await FormValueAsync(http, "code", ct);
        if (string.IsNullOrWhiteSpace(code)
            || !Totp.Verify(SecretBox.Decrypt(options.MasterKey, record.SecretBox!), code.Trim(), DateTimeOffset.UtcNow))
            return Ui.Page("Set up your authenticator", $"""
                <h1>Set up your authenticator</h1>
                {Error("That code didn't match. Try again.")}
                {ConfirmForm(slug, record.Id)}
                """);

        await store.UpsertMfaFactorAsync(record with { Confirmed = true }, ct);
        var codes = NewRecoveryCodes();
        await StoreRecoveryCodesAsync(store, instance.Id, user.Id, codes, ct);
        await AuditAsync(store, instance.Id, "mfa.enrolled", user.Id, new() { ["factorId"] = record.Id, ["type"] = "totp" }, ct);

        var rows = string.Join("\n", codes.Select(c => $"<div class=\"row\"><code>{c}</code></div>"));
        return Ui.Page("Recovery codes", $"""
            <h1>Recovery codes</h1>
            <p class="muted">Two-factor is on. Each code below works once if you ever lose your authenticator. They will not be shown again.</p>
            <div class="list">
            {rows}
            </div>
            <a href="/ui/{slug}/account"><button class="secondary">I saved my codes</button></a>
            """);
    }

    private static async Task StoreRecoveryCodesAsync(AuthStore store, string instanceId, string userId, string[] codes, CancellationToken ct)
    {
        var hashed = codes.Select(SecureTokens.Sha256Hex).ToArray();
        var existing = (await store.ListMfaFactorsAsync(userId, ct)).FirstOrDefault(f => f.Type == "recovery");
        if (existing is null)
            await store.CreateMfaFactorAsync(new MfaFactor
            {
                Id = "mfa_" + SecureTokens.RandomToken(20),
                InstanceId = instanceId,
                UserId = userId,
                Type = "recovery",
                RecoveryCodes = hashed,
                Confirmed = true,
                CreatedAt = Now(),
            }, ct);
        else
            await store.UpsertMfaFactorAsync(existing with { RecoveryCodes = hashed }, ct);
    }
}
