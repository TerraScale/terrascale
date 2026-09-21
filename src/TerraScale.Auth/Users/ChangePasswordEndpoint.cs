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

internal sealed class ChangePasswordEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/account/password", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (await ResolvePageSessionAsync(request, sessions, instance.Id, ct) is not { } current)
            return RedirectToSignIn(slug, $"/ui/{slug}/account");

        var form = await request.ReadFormAsync(ct);
        var currentPassword = form["current"].ToString();
        var newPassword = form["new"].ToString();

        if (!SessionService.VerifyPassword(current.User, currentPassword))
            return AccountPage(slug, current.User, current.Session.Id, "Your current password is incorrect.");
        if (newPassword.Length < 8)
            return AccountPage(slug, current.User, current.Session.Id, "The new password must be at least 8 characters.");

        await store.UpsertUserAsync(current.User with
        {
            PasswordHash = PasswordHasher.Hash(newPassword),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        await RevokeSessionsAsync(store, current.User.Id, except: current.Session.Id, ct);
        await AuditAsync(store, instance.Id, current.User.Id, "user.password_changed");
        return Results.Redirect($"/ui/{slug}/account");
    }
}
