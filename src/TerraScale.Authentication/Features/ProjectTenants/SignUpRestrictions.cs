using System.Collections.Frozen;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed record RestrictionsBody(IReadOnlyList<string> Allowlist, IReadOnlyList<string> Blocklist, bool BlockDisposableEmails = false, bool BlockEmailAliases = false)
{
    public static readonly RestrictionsBody Default = new([], []);
}

public sealed record ProjectInvitationRecord(string Id, string ProjectId, string Email, string TokenHash, string? RedirectUrl, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status = "pending");

public sealed record WaitlistRecord(string Id, string ProjectId, string Email, DateTimeOffset CreatedAt, string Status = "pending");

public sealed class SignUpRestrictions(AuthData data, ProjectAuthSettings settings, TimeProvider clock)
{
    private static readonly FrozenSet<string> DisposableDomains = LoadDomains();

    public static RestrictionsBody Normalize(RestrictionsBody restrictions)
    {
        ArgumentNullException.ThrowIfNull(restrictions);
        return restrictions with { Allowlist = NormalizeList(restrictions.Allowlist), Blocklist = NormalizeList(restrictions.Blocklist) };
    }

    public static bool Blocked(RestrictionsBody restrictions, string email)
    {
        ArgumentNullException.ThrowIfNull(restrictions);
        var at = email.LastIndexOf('@');
        var domain = email[(at + 1)..];
        return Matches(restrictions.Blocklist, email, domain)
            || restrictions.BlockEmailAliases && email.AsSpan(0, at).Contains('+')
            || restrictions.BlockDisposableEmails && DisposableDomains.Contains(domain);
    }

    public async Task<IReadOnlyList<Change>?> AdmissionAsync(string projectId, string email, bool consume, CancellationToken cancellationToken)
    {
        if (!Tenancy.IsProject(projectId))
        {
            return [];
        }

        if (Users.NormalizeEmail(email) is not { } normalized)
        {
            return null;
        }

        var (current, version) = await settings.ReadVersionedAsync(projectId, cancellationToken);
        var restrictions = current.Restrictions ?? RestrictionsBody.Default;
        if (Blocked(restrictions, normalized))
        {
            return null;
        }

        var invitation = await this.PendingAsync(projectId, normalized, cancellationToken);
        if (current.SignUpMode != "public" && invitation is null
            && !Matches(restrictions.Allowlist, normalized, normalized[(normalized.LastIndexOf('@') + 1)..]))
        {
            return null;
        }

        List<Change> changes = [version is null ? Change.Insert(current) : Change.Assert(current, version)];
        if (consume && invitation is not null)
        {
            changes.Add(Change.Update(invitation, invitation.Value with { Status = "accepted" }));
        }

        return changes;
    }

    public async Task<bool> JoinAsync(string projectId, string email, CancellationToken cancellationToken)
    {
        if (!Tenancy.IsProject(projectId) || Users.NormalizeEmail(email) is not { } normalized)
        {
            return false;
        }

        var (current, version) = await settings.ReadVersionedAsync(projectId, cancellationToken);
        if (current.SignUpMode != "waitlist" || Blocked(current.Restrictions ?? RestrictionsBody.Default, normalized))
        {
            return false;
        }

        if (await data.ReadAsync((db, token) => db.Waitlist.AnyAsync(row => row.ProjectId == projectId && row.Email == normalized, token), cancellationToken))
        {
            return true;
        }

        var entry = new WaitlistRecord(AuthIds.New(), projectId, normalized, clock.GetUtcNow());
        if (!await data.SaveAsync([Change.Insert(entry), version is null ? Change.Insert(current) : Change.Assert(current, version)], cancellationToken)
            && !await data.ReadAsync((db, token) => db.Waitlist.AnyAsync(row => row.ProjectId == projectId && row.Email == normalized, token), cancellationToken))
        {
            throw ApiException.Unavailable();
        }

        return true;
    }

    private Task<Versioned<ProjectInvitationRecord>?> PendingAsync(string projectId, string email, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return data.ReadAsync((db, token) => db.ProjectInvitations.Where(row => row.ProjectId == projectId && row.Email == email
            && row.Status == "pending" && row.ExpiresAt > now).OrderBy(row => row.Id).WithVersion().FirstOrDefaultAsync(token), cancellationToken);
    }

    private static bool Matches(IReadOnlyList<string> entries, string email, string domain) =>
        entries.Contains(email, StringComparer.Ordinal) || entries.Contains(domain, StringComparer.Ordinal);

    private static string[] NormalizeList(IReadOnlyList<string>? entries)
    {
        if (entries is null || entries.Count > 1000)
        {
            throw new ApiException(400, "invalid_request", "Lists accept at most 1000 emails or domains.");
        }

        return [.. entries.Select(entry =>
        {
            var value = entry?.Trim().ToLowerInvariant();
            if (value is null || value.Length > 254 || value.Contains('@', StringComparison.Ordinal) && Users.NormalizeEmail(value) is null
                || !value.Contains('@', StringComparison.Ordinal) && (Uri.CheckHostName(value) != UriHostNameType.Dns || !value.Contains('.', StringComparison.Ordinal)))
            {
                throw new ApiException(400, "invalid_request", "Each list entry must be an email address or domain.");
            }

            return value;
        }).Distinct(StringComparer.Ordinal)];
    }

    private static FrozenSet<string> LoadDomains()
    {
        using var stream = typeof(SignUpRestrictions).Assembly.GetManifestResourceStream("assets/disposable-email-domains.txt.gz")
            ?? throw new InvalidOperationException("The disposable domain list is missing.");
        using var decompressed = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressed);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToFrozenSet(StringComparer.Ordinal);
    }
}
