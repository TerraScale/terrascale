using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed record ProjectInvitationBody(string Id, string Email, string? RedirectUrl, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status);

public sealed record CreateProjectInvitationRequest(string Email, string? RedirectUrl = null);

public sealed record ProjectInvitationPageBody(IReadOnlyList<ProjectInvitationBody> Items, string? Next);

public sealed record WaitlistPageBody(IReadOnlyList<WaitlistRecord> Items, string? Next);

public readonly union InvitationCreated(ProjectInvitationBody, Validation, Conflict);

public readonly union InvitationRevoked(Done, NotFound, Conflict);

public readonly union WaitlistDecided(Done, NotFound, Validation, Conflict);

public readonly union Transacted(bool, Conflict);

public sealed class ProjectInvitations(AuthData data, ProjectAuthSettings settings, SignInMail mail, AuthOptions options, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public async Task<InvitationCreated> CreateAsync(string projectId, string email, string? redirectUrl, string inviter, string locale, CancellationToken cancellationToken,
        Versioned<WaitlistRecord>? waitlist = null)
    {
        var normalized = Users.NormalizeEmail(email);
        if (normalized is null)
        {
            return new Validation("Enter a valid email.", "invalid_request");
        }

        var (policy, version) = await settings.ReadVersionedAsync(projectId, cancellationToken);
        if (SignUpRestrictions.Blocked(policy.Restrictions ?? RestrictionsBody.Default, normalized))
        {
            return new Validation("Sign-up is unavailable for this address.", "signup_refused");
        }

        if (redirectUrl is not null && !(policy.Configuration?.RedirectUrls.Contains(redirectUrl, StringComparer.Ordinal) ?? false))
        {
            return new Validation("The redirect must match a project redirect URL.", "invalid_request");
        }

        using var reservation = mail.Reserve();
        var token = Tokens.RandomToken(43);
        var now = clock.GetUtcNow();
        var invitation = new ProjectInvitationRecord(AuthIds.New(), projectId, normalized, Tokens.Sha256Hex(token), redirectUrl, now, now + Lifetime);
        List<Change> changes = [Change.Insert(invitation)];
        if (version is not null)
        {
            changes.Add(Change.Assert(policy, version));
        }
        if (waitlist is not null)
        {
            changes.Add(Change.Update(waitlist, waitlist.Value with { Status = "approved" }));
        }

        Transacted committed;
        try
        {
            committed = await data.TransactAsync(async (db, probe) =>
            {
                if (waitlist is not null && await db.Users.AnyAsync(row => row.InstanceId == projectId && row.Email == normalized, probe))
                {
                    return new Transacted(new Conflict("The waitlist address already has an account.", "conflict"));
                }

                foreach (var change in changes)
                {
                    if (!change.Apply(db))
                    {
                        return new Transacted(new Conflict("The project settings or waitlist entry changed.", "conflict"));
                    }
                }

                return new Transacted(true);
            }, cancellationToken);
        }
        catch (DbUpdateException error) when (AuthData.IsConflict(error))
        {
            return new Conflict("The project settings or waitlist entry changed.", "conflict");
        }

        if (committed is Conflict refused)
        {
            return refused;
        }

        await reservation.EnqueueEmailAsync(AuthEmailKind.Invitation, new AuthEmailValues(normalized)
        {
            Link = new Uri(options.PublicOrigin, $"ui/{projectId}/invitations/{token}"),
            ExpiresIn = Lifetime,
            Organization = policy.Name ?? projectId,
            Inviter = inviter,
            Tenant = projectId,
        }, locale);
        return this.Map(invitation);
    }

    public async Task<ProjectInvitationPageBody> ListAsync(string projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        var rows = await data.ReadAsync((db, token) => db.ProjectInvitations.Where(row => row.ProjectId == projectId && (after == null || row.Id.CompareTo(after) > 0))
            .OrderBy(row => row.Id).Take(limit + 1).ToListAsync(token), cancellationToken);
        return new([.. rows.Take(limit).Select(this.Map)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task<InvitationRevoked> RevokeAsync(string projectId, string id, CancellationToken cancellationToken)
    {
        var row = await data.ReadAsync((db, token) => db.ProjectInvitations.Where(row => row.ProjectId == projectId && row.Id == id)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);
        if (row is null)
        {
            return new NotFound("Invitation not found.", "not_found");
        }

        if (row.Value.Status != "pending")
        {
            return new Done();
        }

        return await data.SaveAsync(Change.Update(row, row.Value with { Status = "revoked" }), cancellationToken)
            ? new Done()
            : new Conflict("The invitation changed.", "conflict");
    }

    public async Task<ProjectInvitationRecord?> LookupAsync(string projectId, string token, CancellationToken cancellationToken)
    {
        if (token.Length != 43 || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return null;
        }

        var digest = Tokens.Sha256Hex(token);
        var now = clock.GetUtcNow();
        return await data.ReadAsync((db, probe) => db.ProjectInvitations.SingleOrDefaultAsync(row =>
            row.ProjectId == projectId && row.TokenHash == digest && row.Status == "pending" && row.ExpiresAt > now, probe), cancellationToken);
    }

    public async Task<WaitlistPageBody> WaitlistAsync(string projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        var rows = await data.ReadAsync((db, token) => db.Waitlist.Where(row => row.ProjectId == projectId && (after == null || row.Id.CompareTo(after) > 0))
            .OrderBy(row => row.Id).Take(limit + 1).ToListAsync(token), cancellationToken);
        return new([.. rows.Take(limit).Select(row => row)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task<WaitlistDecided> DecideAsync(string projectId, string id, bool approve, string inviter, string locale, CancellationToken cancellationToken)
    {
        var row = await data.ReadAsync((db, token) => db.Waitlist.Where(entry => entry.ProjectId == projectId && entry.Id == id)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);
        if (row is null)
        {
            return new NotFound("Waitlist entry not found.", "not_found");
        }

        if (row.Value.Status != "pending")
        {
            return new Conflict("The waitlist entry was already decided.", "conflict");
        }

        if (approve)
        {
            return await this.CreateAsync(projectId, row.Value.Email, null, inviter, locale, cancellationToken, row) switch
            {
                ProjectInvitationBody => new Done(),
                Validation invalid => invalid,
                Conflict refused => refused,
            };
        }

        return await data.SaveAsync(Change.Update(row, row.Value with { Status = "rejected" }), cancellationToken)
            ? new Done()
            : new Conflict("The waitlist entry changed.", "conflict");
    }

    private ProjectInvitationBody Map(ProjectInvitationRecord row) =>
        new(row.Id, row.Email, row.RedirectUrl, row.CreatedAt, row.ExpiresAt, row.Status == "pending" && row.ExpiresAt <= clock.GetUtcNow() ? "expired" : row.Status);
}
