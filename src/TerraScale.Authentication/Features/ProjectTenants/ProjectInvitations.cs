using Dapper;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
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

public sealed class ProjectInvitations(AuthData data, ProjectAuthSettings settings, SignInMail mail, AuthOptions options, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public async Task<ProjectInvitationBody> CreateAsync(string projectId, string email, string? redirectUrl, string inviter, string locale, CancellationToken cancellationToken,
        Versioned<WaitlistRecord>? waitlist = null)
    {
        var normalized = Users.NormalizeEmail(email) ?? throw new ApiException(400, "invalid_request", "Enter a valid email.");
        var (policy, version) = await settings.ReadVersionedAsync(projectId, cancellationToken);
        if (SignUpRestrictions.Blocked(policy.Restrictions ?? RestrictionsBody.Default, normalized))
        {
            throw new ApiException(400, "signup_refused", "Sign-up is unavailable for this address.");
        }

        if (redirectUrl is not null && !(policy.Configuration?.RedirectUrls.Contains(redirectUrl, StringComparer.Ordinal) ?? false))
        {
            throw new ApiException(400, "invalid_request", "The redirect must match a project redirect URL.");
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

        var refusal = await data.TransactAsync(async scope =>
        {
            if (waitlist is not null && await scope.Connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM users WHERE instance_id = @projectId AND email = @normalized)", new { projectId, normalized }, scope.Transaction))
            {
                return "The waitlist address already has an account.";
            }

            foreach (var change in changes)
            {
                scope.Stage(change);
            }

            return null;
        }, cancellationToken) switch
        {
            string refused => refused,
            null => null,
            Conflict => "The project settings or waitlist entry changed.",
        };
        if (refusal is not null)
        {
            throw new ApiException(409, "conflict", refusal);
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
        var take = limit + 1;
        var rows = (await data.ReadAsync(connection => connection.QueryAsync<ProjectInvitationRecord>(
            "SELECT * FROM project_invitations WHERE project_id = @projectId AND (@after IS NULL OR id > @after) ORDER BY id LIMIT @take", new { projectId, after, take }),
            cancellationToken)).AsList();
        return new([.. rows.Take(limit).Select(this.Map)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task RevokeAsync(string projectId, string id, CancellationToken cancellationToken)
    {
        var row = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectInvitationRecord>(
            "SELECT * FROM project_invitations WHERE project_id = @projectId AND id = @id", new { projectId, id }), cancellationToken))
            ?? throw new ApiException(404, "not_found", "Invitation not found.");
        if (row.Value.Status == "pending" && !await data.SaveAsync(Change.Update(row, row.Value with { Status = "revoked" }), cancellationToken))
        {
            throw new ApiException(409, "conflict", "The invitation changed.");
        }
    }

    public async Task<ProjectInvitationRecord?> LookupAsync(string projectId, string token, CancellationToken cancellationToken)
    {
        if (token.Length != 43 || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return null;
        }

        var digest = Tokens.Sha256Hex(token);
        var now = clock.GetUtcNow();
        return await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectInvitationRecord>(
            "SELECT * FROM project_invitations WHERE project_id = @projectId AND token_hash = @digest AND status = 'pending' AND expires_at > @now",
            new { projectId, digest, now }), cancellationToken);
    }

    public async Task<WaitlistPageBody> WaitlistAsync(string projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        var take = limit + 1;
        var rows = (await data.ReadAsync(connection => connection.QueryAsync<WaitlistRecord>(
            "SELECT * FROM project_waitlist WHERE project_id = @projectId AND (@after IS NULL OR id > @after) ORDER BY id LIMIT @take", new { projectId, after, take }),
            cancellationToken)).AsList();
        return new([.. rows.Take(limit)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task DecideAsync(string projectId, string id, bool approve, string inviter, string locale, CancellationToken cancellationToken)
    {
        var row = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<WaitlistRecord>(
            "SELECT * FROM project_waitlist WHERE project_id = @projectId AND id = @id", new { projectId, id }), cancellationToken))
            ?? throw new ApiException(404, "not_found", "Waitlist entry not found.");
        if (row.Value.Status != "pending")
        {
            throw new ApiException(409, "conflict", "The waitlist entry was already decided.");
        }

        if (approve)
        {
            await this.CreateAsync(projectId, row.Value.Email, null, inviter, locale, cancellationToken, row);
        }
        else if (!await data.SaveAsync(Change.Update(row, row.Value with { Status = "rejected" }), cancellationToken))
        {
            throw new ApiException(409, "conflict", "The waitlist entry changed.");
        }
    }

    private ProjectInvitationBody Map(ProjectInvitationRecord row) =>
        new(row.Id, row.Email, row.RedirectUrl, row.CreatedAt, row.ExpiresAt, row.Status == "pending" && row.ExpiresAt <= clock.GetUtcNow() ? "expired" : row.Status);
}
