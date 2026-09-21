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

internal sealed class SignUpEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapMethods("/ui/{slug}/sign-up", [HttpMethods.Get, HttpMethods.Post], HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, IEmailSender emailSender, AuthOptions options, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (HttpMethods.IsGet(request.Method))
            return SignUpPage(slug, instance);

        var form = await request.ReadFormAsync(ct);
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        var name = form["name"].ToString().Trim();

        if (!SessionService.IsValidEmail(email))
            return SignUpPage(slug, instance, "Please enter a valid email address.", email, name);
        if (password.Length < 8)
            return SignUpPage(slug, instance, "Password must be at least 8 characters.", email, name);

        User user;
        try
        {
            // Pre-check because not every deployment enforces the unique emailKey index; the catch covers those that do.
            if (await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(email), ct) is not null)
                return SignUpPage(slug, instance, "This email is already registered.", email, name);
            user = await sessions.CreateUserAsync(instance.Id, email, username: null, password, Optional(name), ct);
        }
        catch (QuotaExceededException)
        {
            return SignUpPage(slug, instance, "This app has reached its user limit. The owner can raise it by upgrading the plan.", email, name);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return SignUpPage(slug, instance, "This email is already registered.", email, name);
        }

        await AuditAsync(store, instance.Id, user.Id, "user.signed_up");
        await EnrollByDomainAsync(store, instance.Id, user, ct);
        await EstablishSessionAsync(store, sessions, request.HttpContext, instance, user, "password", ct);

        var token = await IssueTokenAsync(store, instance.Id, user.Id, "verify_email", VerifyEmailTtl, ct);
        await emailSender.SendAsync(user.Email!, "Verify your email",
            $"Welcome to {instance.Name}!\n\nConfirm your email address by opening:\n\n{Link(options, $"/ui/{slug}/verify-email", token)}\n\nThis link expires in 24 hours.", ct);

        return Results.Redirect($"/ui/{slug}/account");
    }

    private static readonly TimeSpan VerifyEmailTtl = TimeSpan.FromHours(24);

    // ---- Handlers -------------------------------------------------------------------

    /// <summary>Auto-joins the user to any organization whose enrollment domain matches their email.</summary>
    private static async Task EnrollByDomainAsync(AuthStore store, string instanceId, User user, CancellationToken ct)
    {
        if (user.Email is null || user.Email.Split('@') is not [_, var domain, ..])
            return;
        foreach (var orgDomain in await store.ListOrgDomainsAsync(instanceId, ct))
        {
            if (string.Equals(orgDomain.Domain, domain, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await store.CreateMembershipAsync(new OrgMembership
                    {
                        Id = "mem_" + Crypto.SecureTokens.RandomToken(20),
                        InstanceId = instanceId,
                        OrgId = orgDomain.OrgId,
                        UserId = user.Id,
                        MemberKey = $"{orgDomain.OrgId}:{user.Id}",
                        Role = "member",
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    }, ct);
                    await store.CreateAuditEventAsync(new AuditEvent
                    {
                        Id = "evt_" + Crypto.SecureTokens.RandomToken(20),
                        InstanceId = instanceId,
                        Type = "organization.enrolled",
                        ActorId = user.Id,
                        Data = new Dictionary<string, object> { ["organization_id"] = orgDomain.OrgId, ["user_id"] = user.Id, ["domain"] = orgDomain.Domain },
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    }, ct);
                }
                catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                {
                    // Already a member — enrollment is idempotent.
                }
            }
        }
    }

    // ---- Pages ----------------------------------------------------------------------

    private static IResult SignUpPage(string slug, Instance brand, string? error = null, string email = "", string name = "") =>
        Ui.Page("Create your account", $"""
            <h1>Create your account</h1>
            {ErrorDiv(error)}
            <form method="post">
            <label for="su-email">Email</label>
            <input id="su-email" name="email" type="email" value="{Ui.Esc(email)}" required autofocus>
            <label for="su-password">Password</label>
            <input id="su-password" name="password" type="password" minlength="8" required>
            <label for="su-name">Name <span class="muted">(optional)</span></label>
            <input id="su-name" name="name" value="{Ui.Esc(name)}">
            <button type="submit">Create account</button>
            </form>
            <p class="hint">Already registered? <a href="/ui/{slug}/sign-in">Sign in</a></p>
            """, themeColor: brand.ThemeColor, logoUrl: brand.LogoUrl);

    private static string? Optional(string value) => value.Length == 0 ? null : value;
}
