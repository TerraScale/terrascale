using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
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

    public async Task<InvitationSent> InviteAsync(string project, string id, ProjectInvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InviterName is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 100
            || name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator || c is '<' or '>')))
        {
            return new Validation("inviter_name must be 1 to 100 plain-text characters without control or bidirectional formatting characters.");
        }

        var email = Users.NormalizeEmail(request.Email);
        if (email is null)
        {
            return new Validation("email must be a valid address.");
        }

        var role = Role(request.Role);
        if (role is null)
        {
            return new Validation("role must be admin or member.");
        }

        using var reservation = mail.Reserve();
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return NoSuchOrganization();
        }

        var secret = Tokens.RandomToken(48);
        var now = clock.GetUtcNow();
        var invite = new ProjectOrganizationInvitation(project, org.Value.Id, AuthIds.New(), email, role, Tokens.Sha256Hex(secret), "pending", now, now + InvitationLifetime);
        if (!await data.SaveAsync([Change.Update(org, org.Value), Change.Insert(invite), this.Event(org.Value, "organizationInvitation.created", invite.Id)], cancellationToken).ConfigureAwait(false))
        {
            return Refused();
        }

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

    public async Task<InvitationsListed> InvitationsAsync(string project, string id, int limit, string? after, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return NoSuchOrganization();
        }

        var cursor = PlatformDirectory.Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.ProjectOrganizationInvitations
            .Where(row => row.ProjectId == project && row.OrganizationId == org.Value.Id && string.Compare(row.Id, cursor) > 0)
            .OrderBy(row => row.Id).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new ProjectOrganizationInvitationPage([.. rows.Take(limit).Select(this.InvitationBody)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task<InvitationRevoked> RevokeInvitationAsync(string project, string id, string invitationId, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return NoSuchOrganization();
        }

        var invite = await data.ReadAsync((db, token) => db.ProjectOrganizationInvitations
            .Where(row => row.ProjectId == project && row.OrganizationId == org.Value.Id && row.Id == invitationId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (invite is null)
        {
            return NoSuchOrganization();
        }

        if (invite.Value.Status != "pending")
        {
            return new Conflict("The invitation is no longer pending.", "invitation_consumed");
        }

        return await data.SaveAsync([Change.Update(org, org.Value), Change.Update(invite, invite.Value with { Status = "revoked" }),
            this.Event(org.Value, "organizationInvitation.revoked", invite.Value.Id)], cancellationToken).ConfigureAwait(false)
            ? new Done()
            : Refused();
    }

    public async Task<Found<Versioned<ProjectOrganizationInvitation>>> InvitationAsync(string project, string secret, CancellationToken cancellationToken)
    {
        if (secret.Length != 48)
        {
            return new NotFound("No such resource.", "not_found");
        }

        var hash = Tokens.Sha256Hex(secret);
        var invite = await data.ReadAsync((db, token) => db.ProjectOrganizationInvitations
            .Where(row => row.ProjectId == project && row.TokenHash == hash).WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        return invite is { Value.Status: "pending" } && invite.Value.ExpiresAt > clock.GetUtcNow() ? invite
            : new NotFound("No such resource.", "not_found");
    }

    public async Task<InvitationAccepted> AcceptAsync(UserRecord user, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var invitation = await this.InvitationAsync(user.InstanceId, secret, cancellationToken).ConfigureAwait(false);
        if (invitation is not Versioned<ProjectOrganizationInvitation> invite)
        {
            return new NotFound("No such resource.", "not_found");
        }

        if (invite.Value.Email != user.Email)
        {
            return new Forbidden("Sign in with the address this invitation was sent to.", "invitation_email_mismatch");
        }

        var org = await this.ReadAsync(user.InstanceId, invite.Value.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return new NotFound("No such resource.", "not_found");
        }

        var existing = await this.MemberAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken).ConfigureAwait(false);
        if (RefusesMembership(org.Value, invite.Value.Role) is { } refused)
        {
            return refused;
        }

        List<Change> changes = [Change.Assert(user), Change.Update(org, existing is null ? AddMember(org.Value, invite.Value.Role) : org.Value),
            Change.Update(invite, invite.Value with { Status = "accepted" }), this.Event(org.Value, "organizationInvitation.accepted", invite.Value.Id)];
        if (existing is null)
        {
            changes.Add(Change.Insert(new ProjectOrganizationMembership(user.InstanceId, org.Value.Id, user.Id, invite.Value.Role, clock.GetUtcNow())));
            changes.Add(this.Event(org.Value, "organizationMembership.created", user.Id, invite.Value.Role));
        }

        changes.AddRange(await this.ClearDismissalAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken).ConfigureAwait(false));
        return await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false)
            ? new InvitationAccepted(new OrganizationClaims(org.Value.Id, org.Value.Slug, existing?.Value.Role ?? invite.Value.Role))
            : Refused();
    }
}

public readonly union InvitationSent(ProjectOrganizationInvitationBody, NotFound, Validation, Conflict);

public readonly union InvitationsListed(ProjectOrganizationInvitationPage, NotFound);

public readonly union InvitationRevoked(Done, NotFound, Conflict);

public readonly union InvitationAccepted(OrganizationClaims, NotFound, Forbidden, Conflict);
