using System.Globalization;
using Dapper;
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
        var org = await this.ReadAsync(project, id, cancellationToken);
        var secret = Tokens.RandomToken(48);
        var now = clock.GetUtcNow();
        var invite = new ProjectOrganizationInvitation(project, org.Value.Id, AuthIds.New(), email, role, Tokens.Sha256Hex(secret), "pending", now, now + InvitationLifetime);
        await this.CommitAsync([Change.Update(org, org.Value), Change.Insert(invite), this.Event(org.Value, "organizationInvitation.created", invite.Id)], cancellationToken);
        await reservation.EnqueueEmailAsync(AuthEmailKind.Invitation, new AuthEmailValues(email)
        {
            Tenant = project,
            Organization = org.Value.Name,
            Inviter = request.InviterName ?? org.Value.Name,
            Link = new Uri(options.PublicOrigin, $"ui/{project}/organization-invitations?token={secret}"),
            ExpiresIn = InvitationLifetime,
        });
        return this.InvitationBody(invite);
    }

    public async Task<ProjectOrganizationInvitationPage> InvitationsAsync(string project, string id, int limit, string? after, CancellationToken cancellationToken)
    {
        var org = (await this.ReadAsync(project, id, cancellationToken)).Value.Id;
        var cursor = PlatformDirectory.Cursor(after);
        var take = limit + 1;
        var rows = (await data.ReadAsync(connection => connection.QueryAsync<ProjectOrganizationInvitation>(
            "SELECT * FROM project_organization_invitations WHERE project_id = @project AND organization_id = @org AND id > @cursor ORDER BY id LIMIT @take",
            new { project, org, cursor, take }), cancellationToken)).AsList();
        return new([.. rows.Take(limit).Select(this.InvitationBody)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task RevokeInvitationAsync(string project, string id, string invitationId, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken);
        var organizationId = org.Value.Id;
        var invite = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectOrganizationInvitation>(
            "SELECT * FROM project_organization_invitations WHERE project_id = @project AND organization_id = @organizationId AND id = @invitationId",
            new { project, organizationId, invitationId }), cancellationToken)) ?? throw PlatformDirectory.NotFound();
        if (invite.Value.Status != "pending")
        {
            throw Conflict("invitation_consumed", "The invitation is no longer pending.");
        }

        await this.CommitAsync([Change.Update(org, org.Value), Change.Update(invite, invite.Value with { Status = "revoked" }),
            this.Event(org.Value, "organizationInvitation.revoked", invite.Value.Id)], cancellationToken);
    }

    public async Task<Versioned<ProjectOrganizationInvitation>> InvitationAsync(string project, string secret, CancellationToken cancellationToken)
    {
        if (secret.Length != 48)
        {
            throw PlatformDirectory.NotFound();
        }

        var hash = Tokens.Sha256Hex(secret);
        var invite = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectOrganizationInvitation>(
            "SELECT * FROM project_organization_invitations WHERE project_id = @project AND token_hash = @hash", new { project, hash }), cancellationToken));
        return invite is { Value.Status: "pending" } && invite.Value.ExpiresAt > clock.GetUtcNow() ? invite
            : throw PlatformDirectory.NotFound();
    }

    public async Task<OrganizationClaims> AcceptAsync(UserRecord user, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var invite = await this.InvitationAsync(user.InstanceId, secret, cancellationToken);
        if (invite.Value.Email != user.Email)
        {
            throw new ApiException(403, "invitation_email_mismatch", "Sign in with the address this invitation was sent to.");
        }

        var org = await this.ReadAsync(user.InstanceId, invite.Value.OrganizationId, cancellationToken);
        var existing = await this.MemberAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken);
        List<Change> changes = [Change.Assert(user), Change.Update(org, existing is null ? AddMember(org.Value, invite.Value.Role) : org.Value),
            Change.Update(invite, invite.Value with { Status = "accepted" }), this.Event(org.Value, "organizationInvitation.accepted", invite.Value.Id)];
        if (existing is null)
        {
            changes.Add(Change.Insert(new ProjectOrganizationMembership(user.InstanceId, org.Value.Id, user.Id, invite.Value.Role, clock.GetUtcNow())));
            changes.Add(this.Event(org.Value, "organizationMembership.created", user.Id, invite.Value.Role));
        }

        changes.AddRange(await this.ClearDismissalAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken));
        await this.CommitAsync(changes, cancellationToken);
        return new(org.Value.Id, org.Value.Slug, existing?.Value.Role ?? invite.Value.Role);
    }
}
