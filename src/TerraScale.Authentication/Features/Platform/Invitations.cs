using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>
/// Invitations to an organization role, or to one project's role (TS-598). The token is <c>tsi_</c> and 52 base32
/// characters, emailed to the invitee and answered once to the inviter; only its SHA-256 is stored, and
/// <c>uniques</c> maps that digest to the invitation and holds one pending invitation per email and target. Every
/// decision is one transaction with the membership it creates, so an invitation works once.
/// </summary>
public sealed class Invitations(AuthStore store, PlatformDirectory directory, Users users, SignInMail mail, AttemptLimiter limiter, AuthOptions options, TimeProvider clock)
{
    /// <summary>How long an invitation can be accepted.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Invitations an organization may send per hour, so its admins cannot flood inboxes.</summary>
    public const int PerOrganizationPerHour = 100;

    private const string Instance = PlatformDirectory.Instance;
    private const string Prefix = "tsi_";
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const string PendingKind = "invitation-pending";
    private const string TokenKind = "invitation-token";
    private static readonly SearchValues<char> Base32 = SearchValues.Create(Alphabet);

    /// <summary>Whether <paramref name="value"/> has the shape of an invitation token.</summary>
    public static bool IsToken(string? value) =>
        value is { Length: 56 } && value.StartsWith(Prefix, StringComparison.Ordinal) && value.AsSpan(Prefix.Length).IndexOfAnyExcept(Base32) < 0;

    /// <summary>An invitation's status as a caller sees it: a pending one past its expiry is <c>expired</c>.</summary>
    public string Status(InvitationRecord invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return invitation.Status == "pending" && invitation.ExpiresAt <= clock.GetUtcNow() ? "expired" : invitation.Status;
    }

    /// <summary>
    /// Invites <paramref name="email"/>: to the organization (needs admin, and owner to invite an owner) or, with
    /// <paramref name="projectId"/>, to that project (needs its admin role). Emails the dashboard's accept page
    /// (<c>&lt;dashboard origin&gt;/invitations/&lt;token&gt;</c>) when the dashboard client is configured and answers the token once.
    /// </summary>
    public async Task<(InvitationRecord Invitation, string Token)> CreateAsync(UserRecord caller, string organizationId, string email, string role, string? projectId, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        async Task<(StoreOperation Authority, string OrganizationName)> AuthorizeAsync()
        {
            if (projectId is null)
            {
                var access = await directory.OrganizationAccessAsync(caller.Id, organizationId, cancellationToken).ConfigureAwait(false);
                PlatformDirectory.RequireRole(access, role == Roles.Owner ? Roles.Owner : Roles.Admin);
                return (PlatformDirectory.Authority(access, role == Roles.Owner ? Roles.Owner : Roles.Admin), access.Organization.Value.Name);
            }

            var project = await directory.ProjectAccessAsync(caller.Id, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            PlatformDirectory.RequireRole(project, Roles.Admin);
            return (PlatformDirectory.Authority(project, Roles.Admin), project.Organization.Value.Name);
        }

        // Checked before charging, so only those who may invite spend the organization's budget; the commit checks again.
        _ = await AuthorizeAsync().ConfigureAwait(false);
        if (!await limiter.TryAcquireAsync(Instance, "invitation", organizationId, PerOrganizationPerHour, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(StatusCodes.Status429TooManyRequests, "rate_limited", "This organization sent too many invitations; retry within the hour.");
        }

        // Hold capacity across the commit: a known-full queue must never leave an undeliverable pending invite.
        // Dispose releases it on every failed authorization, conflict, cancellation or transaction failure.
        var dashboard = options.DashboardClient;
        using var reservation = dashboard is not null ? mail.Reserve() : null;
        cancellationToken.ThrowIfCancellationRequested();
        var (invitation, token, organizationName) = await directory.CommitAsync(async () =>
        {
            var (authority, organizationName) = await AuthorizeAsync().ConfigureAwait(false);
            if (await this.AlreadyHasAsync(organizationId, projectId, email, role, cancellationToken).ConfigureAwait(false))
            {
                throw PlatformDirectory.Conflict("already_member", "That address already has this access.");
            }

            var now = clock.GetUtcNow();
            var token = Prefix + RandomNumberGenerator.GetString(Alphabet, 52);
            var invitation = new InvitationRecord(AuthStore.NewId(), Instance, organizationId, projectId, email, role, caller.Id, now, now + Lifetime, "pending", Tokens.Sha256Hex(token));
            // Orders the invitation against a deletion of the organization or project, whose invitations are deleted after it.
            List<StoreOperation> operations = [authority, AuthStore.Require(AuthStore.OrganizationCollection, Instance, organizationId, PlatformDirectory.Filter("exists", "/id"))];
            if (projectId is not null)
            {
                operations.Add(AuthStore.Require(AuthStore.ProjectCollection, Instance, projectId, PlatformDirectory.Filter("exists", "/id")));
            }

            int? claim = null;
            var pending = PendingKey(organizationId, projectId, email);
            if (await store.FindUniqueAsync(Instance, PendingKind, pending, cancellationToken).ConfigureAwait(false) is { } held)
            {
                if (await this.ReadAsync(held.OwnerId, cancellationToken).ConfigureAwait(false) is { } earlier && this.Status(earlier.Value) == "pending")
                {
                    throw PendingConflict();
                }

                // An expired invitation stays in the history; its address can be invited again. A lost race rereads.
                operations.Add(AuthStore.Reclaim(Instance, PendingKind, pending, invitation.Id, held.Version));
            }
            else
            {
                claim = operations.Count;
                operations.Add(AuthStore.Claim(Instance, PendingKind, pending, invitation.Id));
            }

            operations.Add(AuthStore.Claim(Instance, TokenKind, invitation.TokenHash, invitation.Id));
            operations.Add(AuthStore.Create(AuthStore.InvitationCollection, Instance, invitation.Id, Serialize(invitation)));
            operations.Add(directory.Audit(organizationId, caller.Id, "invitation.created", email, projectId is null ? role : $"{projectId}/{role}"));
            return new PlatformDirectory.Plan<(InvitationRecord, string, string)>(operations, (invitation, token, organizationName), claim, PendingConflict());
        }, cancellationToken).ConfigureAwait(false);

        // The dashboard serves the accept page at /invitations/{token} on the origin of its first sign-in callback.
        if (dashboard is not null)
        {
            reservation!.EnqueueInvitation(new AuthEmailValues(invitation.Email)
            {
                Link = new Uri($"{dashboard.RedirectUris[0].GetLeftPart(UriPartial.Authority)}/invitations/{token}"),
                ExpiresIn = Lifetime,
                Organization = organizationName,
                Tenant = organizationId,
                Inviter = caller.Email,
            }, locale);
        }

        return (invitation, token);
    }

    /// <summary>
    /// A page of the organization's invitations in creation order: every one for an organization admin, or with
    /// <paramref name="projectId"/> that project's for its admin.
    /// </summary>
    public async Task<Page<InvitationRecord>> ListAsync(string userId, string organizationId, string? projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        List<System.Text.Json.Nodes.JsonObject> conditions = [PlatformDirectory.Equal("/organizationId", organizationId)];
        if (projectId is null)
        {
            PlatformDirectory.RequireRole(await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false), Roles.Admin);
        }
        else
        {
            PlatformDirectory.RequireRole(await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false), Roles.Admin);
            conditions.Add(PlatformDirectory.Equal("/projectId", projectId));
        }

        var page = await directory.PageAsync(AuthStore.InvitationCollection, conditions, PlatformJson.Default.InvitationRecord, "/id", limit, after, cancellationToken).ConfigureAwait(false);
        return new([.. page.Items.Select(item => item.Value).Where(item => item.InstanceId == Instance && item.OrganizationId == organizationId && (projectId is null || item.ProjectId == projectId))], page.Next);
    }

    /// <summary>Revokes a pending invitation; needs the admin role of its organization, or of its project.</summary>
    public Task RevokeAsync(string userId, string organizationId, string invitationId, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            if (!PlatformDirectory.IsId(invitationId) || await this.ReadAsync(invitationId, cancellationToken).ConfigureAwait(false) is not { } invitation
                || invitation.Value.OrganizationId != organizationId)
            {
                throw PlatformDirectory.NotFound();
            }

            StoreOperation authority;
            if (invitation.Value.ProjectId is { } projectId)
            {
                var access = await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
                PlatformDirectory.RequireRole(access, Roles.Admin);
                authority = PlatformDirectory.Authority(access, Roles.Admin);
            }
            else
            {
                var access = await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
                PlatformDirectory.RequireRole(access, Roles.Admin);
                authority = PlatformDirectory.Authority(access, Roles.Admin);
            }

            if (invitation.Value.Status != "pending")
            {
                throw PlatformDirectory.Conflict("not_pending", "The invitation is no longer pending.");
            }

            List<StoreOperation> operations = [authority, .. await this.CloseAsync(invitation, "revoked", userId, cancellationToken).ConfigureAwait(false)];
            operations.Add(directory.Audit(organizationId, userId, "invitation.revoked", invitation.Value.Email));
            return new PlatformDirectory.Plan<bool>(operations, true);
        }, cancellationToken);

    /// <summary>A pending, unexpired invitation by its token, with its organization's and project's names; 404 otherwise.</summary>
    public async Task<(InvitationRecord Invitation, string OrganizationName, string? ProjectName)> LookupAsync(string token, CancellationToken cancellationToken)
    {
        var invitation = await this.ByTokenAsync(token, cancellationToken).ConfigureAwait(false);
        if (invitation is null || this.Status(invitation.Value) != "pending"
            || await directory.OrganizationAsync(invitation.Value.OrganizationId, cancellationToken).ConfigureAwait(false) is not { } organization)
        {
            throw PlatformDirectory.NotFound();
        }

        string? projectName = null;
        if (invitation.Value.ProjectId is { } projectId)
        {
            projectName = (await directory.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound()).Value.Name;
        }

        return (invitation.Value, organization.Value.Name, projectName);
    }

    /// <summary>
    /// Accepts an invitation for its invitee, whose (verified) email must be the invitation's: an organization role
    /// makes the user a member, a project role grants it (as a collaborator when the user is not a member).
    /// </summary>
    public Task<InvitationRecord> AcceptAsync(UserRecord caller, string token, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var invitation = await this.DecidableAsync(caller, token, cancellationToken).ConfigureAwait(false);
            var record = invitation.Value;
            List<StoreOperation> operations;
            if (record.ProjectId is { } projectId)
            {
                operations = await directory.GrantOperationsAsync(projectId, caller.Id, record.Role, refuseHeld: true, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
                operations.Add(directory.Audit(record.OrganizationId, caller.Id, "project_member.role_set", caller.Id, $"{projectId}/{record.Role}"));
            }
            else
            {
                if (await directory.OrganizationAsync(record.OrganizationId, cancellationToken).ConfigureAwait(false) is null)
                {
                    throw PlatformDirectory.NotFound();
                }

                operations = await directory.JoinAsync(record.OrganizationId, caller.Id, record.Role, cancellationToken).ConfigureAwait(false)
                    ?? throw PlatformDirectory.Conflict("already_member", "You are already a member of this organization.");
                operations.Add(directory.Audit(record.OrganizationId, caller.Id, "member.joined", caller.Id, record.Role));
            }

            operations.AddRange(await this.CloseAsync(invitation, "accepted", caller.Id, cancellationToken).ConfigureAwait(false));
            operations.Add(directory.Audit(record.OrganizationId, caller.Id, "invitation.accepted", record.Email));
            return new PlatformDirectory.Plan<InvitationRecord>(operations, record);
        }, cancellationToken);

    /// <summary>Declines an invitation for its invitee; the same email rule as accepting.</summary>
    public Task DeclineAsync(UserRecord caller, string token, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var invitation = await this.DecidableAsync(caller, token, cancellationToken).ConfigureAwait(false);
            List<StoreOperation> operations = [.. await this.CloseAsync(invitation, "declined", caller.Id, cancellationToken).ConfigureAwait(false)];
            operations.Add(directory.Audit(invitation.Value.OrganizationId, caller.Id, "invitation.declined", invitation.Value.Email));
            return new PlatformDirectory.Plan<bool>(operations, true);
        }, cancellationToken);

    private static ApiException PendingConflict() =>
        PlatformDirectory.Conflict("invitation_pending", "An invitation for this address is already pending.");

    /// <summary>The unique claims an invitation holds while it exists: its token and, while pending, its address and target.</summary>
    internal static IEnumerable<(string Kind, string Key)> Claims(InvitationRecord invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        yield return (TokenKind, invitation.TokenHash);
        yield return (PendingKind, PendingKey(invitation.OrganizationId, invitation.ProjectId, invitation.Email));
    }

    private static string PendingKey(string organizationId, string? projectId, string email) => $"{organizationId}/{projectId ?? "-"}/{email}";

    private static JsonElement Serialize(InvitationRecord invitation) => JsonSerializer.SerializeToElement(invitation, PlatformJson.Default.InvitationRecord);

    // The invitation the caller may accept or decline: pending, unexpired and addressed to the caller's email.
    private async Task<Versioned<InvitationRecord>> DecidableAsync(UserRecord caller, string token, CancellationToken cancellationToken)
    {
        var invitation = await this.ByTokenAsync(token, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        if (!string.Equals(invitation.Value.Email, caller.Email, StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "email_mismatch", "The invitation is for another email address.");
        }

        return this.Status(invitation.Value) switch
        {
            "pending" => invitation,
            "expired" => throw PlatformDirectory.Conflict("invitation_expired", "The invitation has expired."),
            _ => throw PlatformDirectory.Conflict("not_pending", "The invitation is no longer pending."),
        };
    }

    // Records the decision at the version read and frees the address for a new invitation.
    private async Task<List<StoreOperation>> CloseAsync(Versioned<InvitationRecord> invitation, string status, string userId, CancellationToken cancellationToken)
    {
        var record = invitation.Value;
        var decided = record with { Status = status, DecidedAt = clock.GetUtcNow(), DecidedBy = userId };
        List<StoreOperation> operations = [AuthStore.Replace(AuthStore.InvitationCollection, Instance, record.Id, Serialize(decided), invitation.Version)];
        var pending = PendingKey(record.OrganizationId, record.ProjectId, record.Email);
        if (await store.FindUniqueAsync(Instance, PendingKind, pending, cancellationToken).ConfigureAwait(false) is { } held && held.OwnerId == record.Id)
        {
            operations.Add(AuthStore.Release(Instance, PendingKind, pending, held.Version));
        }

        return operations;
    }

    // Whether the address's account already holds the role (or more) the invitation would give.
    private async Task<bool> AlreadyHasAsync(string organizationId, string? projectId, string email, string role, CancellationToken cancellationToken)
    {
        if (await users.FindByEmailAsync(Instance, email, cancellationToken).ConfigureAwait(false) is not { } invitee)
        {
            return false;
        }

        try
        {
            return projectId is null
                ? (await directory.OrganizationAccessAsync(invitee.Id, organizationId, cancellationToken).ConfigureAwait(false)).Role is not null
                : Roles.Rank((await directory.ProjectAccessAsync(invitee.Id, organizationId, projectId, cancellationToken).ConfigureAwait(false)).Role) >= Roles.Rank(role);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status404NotFound)
        {
            return false;
        }
    }

    private async Task<Versioned<InvitationRecord>?> ByTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (!IsToken(token))
        {
            return null;
        }

        var digest = Tokens.Sha256Hex(token);
        return await store.FindUniqueAsync(Instance, TokenKind, digest, cancellationToken).ConfigureAwait(false) is { } owner
            && await this.ReadAsync(owner.OwnerId, cancellationToken).ConfigureAwait(false) is { } invitation
            && Tokens.FixedTimeEquals(invitation.Value.TokenHash, digest)
            ? invitation
            : null;
    }

    private async Task<Versioned<InvitationRecord>?> ReadAsync(string invitationId, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(AuthStore.InvitationCollection, Instance, invitationId, cancellationToken).ConfigureAwait(false);
        return document?.Document.Deserialize(PlatformJson.Default.InvitationRecord) is { } invitation && invitation.InstanceId == Instance && invitation.Id == invitationId
            ? new(invitation, document.Version)
            : null;
    }
}
