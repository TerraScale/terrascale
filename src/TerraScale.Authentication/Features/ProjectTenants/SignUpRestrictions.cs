using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
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
    private const int PendingWaitlistLimit = 10_000;

    private static readonly FrozenSet<string> DisposableDomains = LoadDomains();

    public static Valid<RestrictionsBody> Normalize(RestrictionsBody restrictions)
    {
        ArgumentNullException.ThrowIfNull(restrictions);
        var allowlist = NormalizeList(restrictions.Allowlist);
        if (allowlist is Validation allowlistError)
        {
            return allowlistError;
        }

        var blocklist = NormalizeList(restrictions.Blocklist);
        return blocklist switch
        {
            Validation blocklistError => blocklistError,
            _ => restrictions with
            {
                Allowlist = allowlist switch { IReadOnlyList<string> list => list, Validation => throw new UnreachableException() },
                Blocklist = blocklist switch { IReadOnlyList<string> list => list, Validation => throw new UnreachableException() }
            },
        };
    }

    public static bool Blocked(RestrictionsBody restrictions, string email)
    {
        ArgumentNullException.ThrowIfNull(restrictions);
        if (Users.NormalizeEmail(email) is not { } normalized)
        {
            return true;
        }

        email = normalized;
        var at = email.LastIndexOf('@');
        var domain = email[(at + 1)..];
        return Matches(restrictions.Blocklist, email, domain, true)
            || restrictions.BlockEmailAliases && email.AsSpan(0, at).Contains('+')
            || restrictions.BlockDisposableEmails && ParentDomains(domain).Any(DisposableDomains.Contains);
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

        List<Change> changes = version is null ? [] : [Change.Assert(current, version)];
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

        return await data.TransactAsync(async (db, token) =>
        {
            var current = await db.ProjectAuthSettings.SingleOrDefaultAsync(row => row.InstanceId == projectId, token)
                ?? new ProjectAuthSettingsRecord(projectId);
            if (current.SignUpMode != "waitlist" || Blocked(current.Restrictions ?? RestrictionsBody.Default, normalized))
            {
                return false;
            }

            if (await db.Users.AnyAsync(row => row.InstanceId == projectId && row.Email == normalized, token)
                || await db.Waitlist.AnyAsync(row => row.ProjectId == projectId && row.Email == normalized, token)
                || await db.Waitlist.CountAsync(row => row.ProjectId == projectId && row.Status == "pending", token) >= PendingWaitlistLimit)
            {
                return true;
            }

            Change.Insert(new WaitlistRecord(AuthIds.New(), projectId, normalized, clock.GetUtcNow())).Apply(db);
            return true;
        }, cancellationToken);
    }

    private Task<Versioned<ProjectInvitationRecord>?> PendingAsync(string projectId, string email, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return data.ReadAsync((db, token) => db.ProjectInvitations.Where(row => row.ProjectId == projectId && row.Email == email
            && row.Status == "pending" && row.ExpiresAt > now).OrderBy(row => row.Id).WithVersion().FirstOrDefaultAsync(token), cancellationToken);
    }

    private static bool Matches(IReadOnlyList<string> entries, string email, string domain, bool parents = false) =>
        entries.Any(entry => entry.Contains('@', StringComparison.Ordinal)
            ? Users.NormalizeEmail(entry) == email
            : Users.NormalizeDomain(entry) is { } normalized && (parents ? ParentDomains(domain).Contains(normalized, StringComparer.Ordinal) : normalized == domain));

    private static IEnumerable<string> ParentDomains(string domain)
    {
        for (var offset = 0; offset < domain.Length;)
        {
            yield return domain[offset..];
            var dot = domain.IndexOf('.', offset);
            if (dot < 0)
            {
                yield break;
            }

            offset = dot + 1;
        }
    }

    private static Valid<IReadOnlyList<string>> NormalizeList(IReadOnlyList<string>? entries)
    {
        if (entries is null || entries.Count > 1000)
        {
            return new Validation("Lists accept at most 1000 emails or domains.");
        }

        List<string> normalized = [];
        foreach (var entry in entries)
        {
            var value = entry?.Trim();
            var candidate = value is null ? null : value.Contains('@', StringComparison.Ordinal) ? Users.NormalizeEmail(value) : Users.NormalizeDomain(value);
            if (candidate is null || candidate.Length > 254)
            {
                return new Validation("Each list entry must be an email address or domain.");
            }

            normalized.Add(candidate);
        }

        return new Valid<IReadOnlyList<string>>([.. normalized.Distinct(StringComparer.Ordinal)]);
    }

    private static FrozenSet<string> LoadDomains()
    {
        using var stream = typeof(SignUpRestrictions).Assembly.GetManifestResourceStream("assets/disposable-email-domains.txt.gz")
            ?? throw new InvalidOperationException("The disposable domain list is missing.");
        using var decompressed = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressed);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(domain => Users.NormalizeDomain(domain) ?? throw new InvalidOperationException("The disposable domain list contains an invalid domain.")).ToFrozenSet(StringComparer.Ordinal);
    }
}
