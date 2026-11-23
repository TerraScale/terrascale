using System.Collections.Frozen;
using System.IO.Compression;
using System.Text.Json.Serialization;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed record RestrictionsBody(IReadOnlyList<string> Allowlist, IReadOnlyList<string> Blocklist, bool BlockDisposableEmails = false, bool BlockEmailAliases = false)
{
    public static readonly RestrictionsBody Default = new([], []);
}

public sealed record ProjectInvitationRecord(string Id, string ProjectId, string Email, string TokenHash, string? RedirectUrl, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status = "pending")
{
    public string? Version { get; init; }
}

public sealed record WaitlistRecord(string Id, string ProjectId, string Email, DateTimeOffset CreatedAt, string Status = "pending")
{
    [JsonIgnore]
    public string? Version { get; init; }
}

public sealed class SignUpRestrictions(AuthData data, ProjectAuthSettings settings, TimeProvider clock)
{
    private const int PendingWaitlistLimit = 10_000;

    private static readonly FrozenSet<string> DisposableDomains = LoadDomains();

    public static RestrictionsBody Normalize(RestrictionsBody restrictions)
    {
        ArgumentNullException.ThrowIfNull(restrictions);
        return restrictions with { Allowlist = NormalizeList(restrictions.Allowlist), Blocklist = NormalizeList(restrictions.Blocklist) };
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

        return await data.TransactAsync(async scope =>
        {
            var current = await scope.Connection.QuerySingleOrDefaultAsync<ProjectAuthSettingsRecord>(
                "SELECT * FROM project_auth_settings WHERE instance_id = @projectId", new { projectId }, scope.Transaction) ?? new ProjectAuthSettingsRecord(projectId);
            if (current.SignUpMode != "waitlist" || Blocked(current.Restrictions ?? RestrictionsBody.Default, normalized))
            {
                return false;
            }

            if (await scope.Connection.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (SELECT 1 FROM users WHERE instance_id = @projectId AND email = @normalized)
                    OR EXISTS (SELECT 1 FROM project_waitlist WHERE project_id = @projectId AND email = @normalized)
                    OR (SELECT COUNT(*) FROM project_waitlist WHERE project_id = @projectId AND status = 'pending') >= @PendingWaitlistLimit
                """, new { projectId, normalized, PendingWaitlistLimit }, scope.Transaction))
            {
                return true;
            }

            scope.Stage(Change.Insert(new WaitlistRecord(AuthIds.New(), projectId, normalized, clock.GetUtcNow())));
            return true;
        }, cancellationToken) switch
        {
            bool joined => joined,
            Conflict => throw new ApiException(409, "conflict", "The waitlist changed; retry."),
        };
    }

    private async Task<Versioned<ProjectInvitationRecord>?> PendingAsync(string projectId, string email, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return Versioned.Of(await data.ReadAsync(connection => connection.QueryFirstOrDefaultAsync<ProjectInvitationRecord>(
            "SELECT * FROM project_invitations WHERE project_id = @projectId AND email = @email AND status = 'pending' AND expires_at > @now ORDER BY id LIMIT 1",
            new { projectId, email, now }), cancellationToken));
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

    private static string[] NormalizeList(IReadOnlyList<string>? entries)
    {
        if (entries is null || entries.Count > 1000)
        {
            throw new ApiException(400, "invalid_request", "Lists accept at most 1000 emails or domains.");
        }

        return [.. entries.Select(entry =>
        {
            var value = entry?.Trim();
            var normalized = value is null ? null : value.Contains('@', StringComparison.Ordinal) ? Users.NormalizeEmail(value) : Users.NormalizeDomain(value);
            if (normalized is null || normalized.Length > 254)
            {
                throw new ApiException(400, "invalid_request", "Each list entry must be an email address or domain.");
            }

            return normalized;
        }).Distinct(StringComparer.Ordinal)];
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
