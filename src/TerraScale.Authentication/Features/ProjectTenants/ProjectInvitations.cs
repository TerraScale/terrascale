using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

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
        var token = AuthIds.New();
        var now = clock.GetUtcNow();
        var invitation = new ProjectInvitationRecord(AuthIds.New(), projectId, normalized, Tokens.Sha256Hex(token), redirectUrl, now, now + Lifetime);
        List<Change> changes = [Change.Insert(invitation), version is null ? Change.Insert(policy) : Change.Assert(policy, version)];
        if (waitlist is not null)
        {
            changes.Add(Change.Update(waitlist, waitlist.Value with { Status = "approved" }));
        }

        if (!await data.SaveAsync(changes, cancellationToken))
        {
            throw new ApiException(409, "conflict", "The project settings or waitlist entry changed.");
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

    public async Task RevokeAsync(string projectId, string id, CancellationToken cancellationToken)
    {
        var row = await data.ReadAsync((db, token) => db.ProjectInvitations.Where(row => row.ProjectId == projectId && row.Id == id)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken) ?? throw new ApiException(404, "not_found", "Invitation not found.");
        if (row.Value.Status == "pending" && !await data.SaveAsync(Change.Update(row, row.Value with { Status = "revoked" }), cancellationToken))
        {
            throw new ApiException(409, "conflict", "The invitation changed.");
        }
    }

    public async Task<ProjectInvitationRecord?> LookupAsync(string projectId, string token, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(token))
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
        return new([.. rows.Take(limit)], rows.Count > limit ? rows[limit - 1].Id : null);
    }

    public async Task DecideAsync(string projectId, string id, bool approve, string inviter, string locale, CancellationToken cancellationToken)
    {
        var row = await data.ReadAsync((db, token) => db.Waitlist.Where(entry => entry.ProjectId == projectId && entry.Id == id)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken) ?? throw new ApiException(404, "not_found", "Waitlist entry not found.");
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
