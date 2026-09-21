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

internal sealed class ResetPasswordEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapMethods("/ui/{slug}/reset-password", [HttpMethods.Get, HttpMethods.Post], HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        string token;
        IFormCollection? form = null;
        if (HttpMethods.IsGet(request.Method))
            token = request.Query["token"].ToString();
        else
        {
            form = await request.ReadFormAsync(ct);
            token = form["token"].ToString();
        }
        if (token.Length == 0)
            return InvalidLinkPage(slug, instance);

        if (form is null)
            return await TokenValidAsync(store, instance.Id, token, "reset_password", ct)
                ? ResetPasswordPage(slug, instance, token)
                : InvalidLinkPage(slug, instance);

        var password = form["password"].ToString();
        if (password.Length < 8)
            return ResetPasswordPage(slug, instance, token, "Password must be at least 8 characters.");

        var record = await ConsumeTokenAsync(store, instance.Id, token, "reset_password", ct);
        var user = record?.UserId is { } userId ? await store.GetUserAsync(userId, ct) : null;
        if (user is null || user.Deleted || user.Banned)
            return InvalidLinkPage(slug, instance);

        await store.UpsertUserAsync(user with
        {
            PasswordHash = PasswordHasher.Hash(password),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        await RevokeSessionsAsync(store, user.Id, except: null, ct);
        await AuditAsync(store, instance.Id, user.Id, "password.reset", new Dictionary<string, object> { ["user_id"] = user.Id });
        return DonePage(slug, instance, "Password updated", "Your password has been updated and all sessions were signed out. Sign in with your new password.");
    }

    private static IResult ResetPasswordPage(string slug, Instance brand, string token, string? error = null) =>
        Ui.Page("Choose a new password", $"""
            <h1>Choose a new password</h1>
            {ErrorDiv(error)}
            <form method="post">
            <input type="hidden" name="token" value="{Ui.Esc(token)}">
            <label for="rp-password">New password</label>
            <input id="rp-password" name="password" type="password" minlength="8" required autofocus>
            <button type="submit">Update password</button>
            </form>
            """, themeColor: brand.ThemeColor, logoUrl: brand.LogoUrl);

    private static async Task<bool> TokenValidAsync(AuthStore store, string instanceId, string token, string purpose, CancellationToken ct) =>
        await store.FindVerificationTokenAsync(SecureTokens.Sha256Hex(token), ct) is { } record
            && record.InstanceId == instanceId
            && record.Purpose == purpose
            && !record.Consumed
            && record.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
