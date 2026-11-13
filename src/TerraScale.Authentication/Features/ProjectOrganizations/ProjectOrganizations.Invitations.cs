using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed partial class ProjectOrganizationDirectory
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public ProjectOrganizationInvitationBody InvitationBody(ProjectOrganizationInvitation invite) => new(invite.Id, invite.Email, invite.Role,
        invite.Status == "pending" && invite.ExpiresAt <= clock.GetUtcNow() ? "expired" : invite.Status, invite.CreatedAt, invite.ExpiresAt);

    public async Task<ProjectOrganizationInvitationBody> InviteAsync(string project, string id, ProjectInvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InviterName is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 100
            || name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator || c is '<' or '>')))
        {
            throw Invalid("inviter_name must be 1 to 100 plain-text characters without control or bidirectional formatting characters.");
        }

        var email = Users.NormalizeEmail(request.Email) ?? throw Invalid("email must be a valid address.");
        var role = Role(request.Role);
        using var reservation = mail.Reserve();
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        var secret = Tokens.RandomToken(48);
        var now = clock.GetUtcNow();
        var invite = new ProjectOrganizationInvitation(project, org.Value.Id, AuthIds.New(), email, role, Tokens.Sha256Hex(secret), "pending", now, now + InvitationLifetime);
        await this.CommitAsync([Change.Update(org, org.Value), Change.Insert(invite), this.Event(org.Value, "organizationInvitation.created", invite.Id)], cancellationToken).ConfigureAwait(false);
        await reservation.EnqueueEmailAsync(AuthEmailKind.Invitation, new AuthEmailValues(email)
        {
            Tenant = project,
            Organization = org.Value.Name,
            Inviter = request.InviterName ?? org.Value.Name,
            Link = new Uri(options.PublicOrigin, $"ui/{project}/organization-invitations?token={secret}"),
            ExpiresIn = InvitationLifetime,
        }).ConfigureAwait(false);
        return this.InvitationBody(invite);
    }

    public async Task<ProjectOrganizationInvitationPage> InvitationsAsync(string project, string id, int limit, string? after, CancellationToken cancellationToken)
    {
        var org = (await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false)).Value.Id;
        var cursor = PlatformDirectory.Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.ProjectOrganizationInvitations
            .Where(row => row.ProjectId == project && row.OrganizationId == org && string.Compare(row.Id, cursor) > 0)
            .OrderBy(row => row.Id).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new([.. rows.Take(limit).Select(this.InvitationBody)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task RevokeInvitationAsync(string project, string id, string invitationId, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        var invite = await data.ReadAsync((db, token) => db.ProjectOrganizationInvitations
            .Where(row => row.ProjectId == project && row.OrganizationId == org.Value.Id && row.Id == invitationId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        if (invite.Value.Status != "pending")
        {
            throw Conflict("invitation_consumed", "The invitation is no longer pending.");
        }

        await this.CommitAsync([Change.Update(org, org.Value), Change.Update(invite, invite.Value with { Status = "revoked" }),
            this.Event(org.Value, "organizationInvitation.revoked", invite.Value.Id)], cancellationToken).ConfigureAwait(false);
    }

    public async Task<Versioned<ProjectOrganizationInvitation>> InvitationAsync(string project, string secret, CancellationToken cancellationToken)
    {
        if (secret.Length != 48)
        {
            throw PlatformDirectory.NotFound();
        }

        var hash = Tokens.Sha256Hex(secret);
        var invite = await data.ReadAsync((db, token) => db.ProjectOrganizationInvitations
            .Where(row => row.ProjectId == project && row.TokenHash == hash).WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        return invite is { Value.Status: "pending" } && invite.Value.ExpiresAt > clock.GetUtcNow() ? invite
            : throw PlatformDirectory.NotFound();
    }

    public async Task<OrganizationClaims> AcceptAsync(UserRecord user, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var invite = await this.InvitationAsync(user.InstanceId, secret, cancellationToken).ConfigureAwait(false);
        if (invite.Value.Email != user.Email)
        {
            throw new ApiException(403, "invitation_email_mismatch", "Sign in with the address this invitation was sent to.");
        }

        var org = await this.ReadAsync(user.InstanceId, invite.Value.OrganizationId, cancellationToken).ConfigureAwait(false);
        var existing = await this.MemberAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken).ConfigureAwait(false);
        List<Change> changes = [Change.Assert(user), Change.Update(org, existing is null ? AddMember(org.Value, invite.Value.Role) : org.Value),
            Change.Update(invite, invite.Value with { Status = "accepted" }), this.Event(org.Value, "organizationInvitation.accepted", invite.Value.Id)];
        if (existing is null)
        {
            changes.Add(Change.Insert(new ProjectOrganizationMembership(user.InstanceId, org.Value.Id, user.Id, invite.Value.Role, clock.GetUtcNow())));
            changes.Add(this.Event(org.Value, "organizationMembership.created", user.Id, invite.Value.Role));
        }

        changes.AddRange(await this.ClearDismissalAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken).ConfigureAwait(false));
        await this.CommitAsync(changes, cancellationToken).ConfigureAwait(false);
        return new(org.Value.Id, org.Value.Slug, existing?.Value.Role ?? invite.Value.Role);
    }
}
