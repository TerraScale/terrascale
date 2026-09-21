namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Organizations.OrganizationSupport;

internal sealed class AcceptInvitationEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/invitations/{invitationId}/accept", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string invitationId, string? token, HttpRequest httpRequest, AuthStore store, SessionService sessions, AuthOptions options, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return Ui.Page("Invitation not found", """<div class="error">This invitation link is not valid.</div>""", status: 404);

        var now = Now();
        var invitation = await store.GetInvitationAsync(invitationId, ct);
        if (invitation is null || invitation.InstanceId != instance.Id)
            return Ui.Page("Invitation not found", """<div class="error">This invitation link is not valid.</div>""", status: 404);

        // Token first, invitation state second: a replayed link must read as used, not
        // as expired/revoked (an accepted invitation still has a perfectly valid token shape).
        var verification = string.IsNullOrEmpty(token) ? null : await store.FindVerificationTokenAsync(SecureTokens.Sha256Hex(token), ct);
        if (verification is null || verification.Purpose != "invitation" || verification.InstanceId != instance.Id
            || verification.UserId != invitation.Id || verification.Consumed || verification.ExpiresAt <= now)
            return Ui.Page("Invalid link", """<div class="error">This acceptance link is invalid or has already been used.</div>""", status: 400);

        if (invitation.Status != "pending" || invitation.ExpiresAt <= now)
            return Ui.Page("Invitation no longer valid", """<div class="error">This invitation has expired or was revoked. Ask an administrator to send a new one.</div>""", status: 400);

        var organization = await store.GetOrganizationAsync(invitation.OrgId, ct);
        if (organization is null || organization.InstanceId != instance.Id)
            return Ui.Page("Organization unavailable", """<div class="error">The organization that invited you no longer exists.</div>""", status: 400);

        var origin = Origin(options);
        if (await store.FindUserByEmailAsync(instance.Id, invitation.Email, ct) is null)
            return Ui.Page("Create your account", $"""
                <h1>Accept your invitation</h1>
                <p>No account exists for <strong>{Ui.Esc(invitation.Email)}</strong> yet. Sign up with this email address first, then open this invitation link again.</p>
                <p class="hint"><a href="{origin}/ui/{slug}/sign-up">Go to sign-up</a></p>
                """);

        var cookie = httpRequest.Cookies[SessionCookies.Name];
        if (string.IsNullOrEmpty(cookie) || await sessions.ResolveAsync(instance.Id, cookie, ct) is not { } current)
            return Ui.Page("Sign in to accept", $"""
                <h1>Accept your invitation</h1>
                <p>You're invited to join <strong>{Ui.Esc(organization.Name)}</strong> as <strong>{Ui.Esc(invitation.Email)}</strong>.</p>
                <p class="hint"><a href="{origin}/ui/{slug}/sign-in">Sign in</a> with that email address to accept.</p>
                """);

        if (SessionService.NormalizeEmail(current.User.Email ?? "") != invitation.Email)
            return Ui.Page("Wrong account", $"""
                <h1>Accept your invitation</h1>
                <div class="error">You're signed in as <strong>{Ui.Esc(current.User.Email ?? current.User.Id)}</strong>, but this invitation is for <strong>{Ui.Esc(invitation.Email)}</strong>.</div>
                <p class="hint"><a href="{origin}/ui/{slug}/sign-out">Sign out</a>, then sign in with the invited email address and reopen this link.</p>
                """);

        var alreadyMember = (await store.ListMembershipsByOrgAsync(organization.Id, ct)).Any(m => m.UserId == current.User.Id);

        // Atomic single-use gate: the consume is an exact-version compare-and-set, so of
        // two concurrent accepts of one link only the first proceeds; the loser reads
        // the token as already used and nothing is created.
        var consumed = await store.ConsumeVerificationTokenAsync(
            verification.TokenKey,
            record => record.Purpose == "invitation" && record.InstanceId == instance.Id
                && record.UserId == invitation.Id && !record.Consumed && record.ExpiresAt > now,
            ct);
        if (consumed is null)
            return Ui.Page("Invalid link", """<div class="error">This acceptance link is invalid or has already been used.</div>""", status: 400);

        // Atomic pending→accepted: a revoke that landed after the checks above wins here,
        // so a revoked invitation never creates a membership and is never reset to accepted.
        if (await store.TryTransitionInvitationAsync(invitation.Id, "pending", "accepted", ct) is null)
            return Ui.Page("Invitation no longer valid", """<div class="error">This invitation has expired or was revoked. Ask an administrator to send a new one.</div>""", status: 400);

        if (!alreadyMember)
        {
            try
            {
                await store.CreateMembershipAsync(new OrgMembership
                {
                    Id = "mem_" + SecureTokens.RandomToken(20),
                    InstanceId = instance.Id,
                    OrgId = organization.Id,
                    UserId = current.User.Id,
                    MemberKey = $"{organization.Id}:{current.User.Id}",
                    Role = invitation.Role,
                    CreatedAt = now,
                }, ct);
            }
            catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                alreadyMember = true;
            }
        }

        await AuditAsync(store, instance.Id, "invitation.accepted", current.User.Id, new Dictionary<string, object>
        {
            ["organization_id"] = organization.Id,
            ["invitation_id"] = invitation.Id,
        }, ct);

        var message = alreadyMember ? $"You're already a member of {organization.Name}." : $"You've joined {organization.Name}!";
        return Ui.Page("Invitation accepted", $"""
            <h1>Invitation accepted</h1>
            <div class="success">{Ui.Esc(message)}</div>
            <p class="hint"><a href="{origin}/ui/{slug}/sign-in">Continue</a></p>
            """);
    }
}
