using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed partial class ProjectOrganizationDirectory
{
    public static ProjectOrganizationDomainBody DomainBody(ProjectOrganizationDomain domain) =>
        new(domain.Domain, "_terrascale-org." + domain.Domain, "terrascale-org=" + domain.VerificationToken, domain.VerifiedAt);

    public async Task<ProjectOrganizationDomainBody> AddDomainAsync(string project, string id, string? domain, CancellationToken cancellationToken)
    {
        domain = domain?.Trim().ToLowerInvariant();
        if (domain is not { Length: >= 4 and <= 253 } || !domain.Contains('.', StringComparison.Ordinal)
            || domain.Split('.').Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-')
            || Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
        {
            throw Invalid("domain must be a DNS domain without a scheme or path.");
        }

        var org = await this.ReadAsync(project, id, cancellationToken);
        var organizationId = org.Value.Id;
        var count = await data.ReadAsync(connection => connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM project_organization_domains WHERE project_id = @project AND organization_id = @organizationId", new { project, organizationId }),
            cancellationToken);
        if (count >= 100)
        {
            throw Conflict("domain_limit", "An organization has at most 100 domains.");
        }

        var record = new ProjectOrganizationDomain(project, org.Value.Id, domain, Tokens.RandomToken(40), null);
        await this.CommitAsync([Change.Update(org, org.Value), Change.Insert(record), this.Event(org.Value, "organizationDomain.created", domain)], cancellationToken);
        return DomainBody(record);
    }

    public async Task<ProjectOrganizationDomainPage> DomainsAsync(string project, string id, CancellationToken cancellationToken)
    {
        var org = (await this.ReadAsync(project, id, cancellationToken)).Value.Id;
        var rows = await data.ReadAsync(connection => connection.QueryAsync<ProjectOrganizationDomain>(
            "SELECT * FROM project_organization_domains WHERE project_id = @project AND organization_id = @org ORDER BY domain LIMIT 100", new { project, org }),
            cancellationToken);
        return new([.. rows.Select(DomainBody)]);
    }

    public async Task<ProjectOrganizationDomainBody?> ChangeDomainAsync(string project, string id, string domain, bool remove, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken);
        var organizationId = org.Value.Id;
        var row = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectOrganizationDomain>(
            "SELECT * FROM project_organization_domains WHERE project_id = @project AND organization_id = @organizationId AND domain = @domain",
            new { project, organizationId, domain }), cancellationToken)) ?? throw PlatformDirectory.NotFound();
        if (!remove && !await this.PublishedAsync(row.Value, cancellationToken))
        {
            throw Conflict("domain_unverified", "The verification TXT record was not found.");
        }

        var updated = row.Value with { VerifiedAt = row.Value.VerifiedAt ?? clock.GetUtcNow(), CheckedAt = clock.GetUtcNow(), FailedChecks = 0 };
        await this.CommitAsync([Change.Update(org, org.Value), remove ? Change.Delete(row) : Change.Update(row, updated),
            this.Event(org.Value, remove ? "organizationDomain.deleted" : "organizationDomain.updated", domain)], cancellationToken);
        return remove ? null : DomainBody(updated);
    }

    public async Task<IReadOnlyList<OrganizationClaims>> AutoJoinAsync(UserRecord user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var domain = user.Email[(user.Email.LastIndexOf('@') + 1)..];
        var project = user.InstanceId;
        var match = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectOrganizationDomain>(
            "SELECT * FROM project_organization_domains WHERE project_id = @project AND domain = @domain AND verified_at IS NOT NULL", new { project, domain }),
            cancellationToken));
        if (match is null)
        {
            return [];
        }

        var org = await this.OrganizationByIdAsync(project, match.Value.OrganizationId, cancellationToken);
        if (org is null || org.Value.DomainJoin == "off" || await this.MemberAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken) is not null)
        {
            return [];
        }

        if (org.Value.Admins == 0)
        {
            AutoJoinSkipped(logger, user.InstanceId, org.Value.Id, "organization_requires_admin");
            return [];
        }

        if (org.Value.DomainJoin == "suggest")
        {
            var dismissed = await this.DismissedAsync(project, org.Value.Id, user.Id, cancellationToken);
            return dismissed ? [] : [new(org.Value.Id, org.Value.Slug, "member")];
        }

        if (org.Value.MaxMembers is { } maximum && org.Value.Members >= maximum)
        {
            AutoJoinSkipped(logger, user.InstanceId, org.Value.Id, "organization_full");
            return [];
        }

        List<Change> changes =
        [
            Change.Assert(user), Change.Assert(match), Change.Update(org, AddMember(org.Value, "member")),
            Change.Insert(new ProjectOrganizationMembership(user.InstanceId, org.Value.Id, user.Id, "member", clock.GetUtcNow())),
            this.Event(org.Value, "organizationMembership.created", user.Id, "member"),
            .. await this.ClearDismissalAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken)
        ];
        if (!await data.SaveAsync(changes, cancellationToken))
        {
            AutoJoinSkipped(logger, user.InstanceId, org.Value.Id, "version_conflict");
            var refreshed = await this.OrganizationByIdAsync(project, org.Value.Id, cancellationToken);
            if (refreshed is null)
            {
                return [];
            }

            var proof = await this.DomainProofAsync(project, refreshed.Value.Id, domain, cancellationToken);
            if (proof is not null && refreshed.Value.DomainJoin == "auto" && refreshed.Value.Admins > 0
                && (refreshed.Value.MaxMembers is null || refreshed.Value.Members < refreshed.Value.MaxMembers)
                && await this.MemberAsync(user.InstanceId, refreshed.Value.Id, user.Id, cancellationToken) is null)
            {
                await data.SaveAsync([Change.Assert(user), Change.Assert(proof), Change.Update(refreshed, AddMember(refreshed.Value, "member")),
                    Change.Insert(new ProjectOrganizationMembership(user.InstanceId, refreshed.Value.Id, user.Id, "member", clock.GetUtcNow())),
                    this.Event(refreshed.Value, "organizationMembership.created", user.Id, "member"),
                    .. await this.ClearDismissalAsync(user.InstanceId, refreshed.Value.Id, user.Id, cancellationToken)], cancellationToken);
            }
        }

        return [];
    }

    public async Task JoinSuggestedAsync(UserRecord user, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var (org, proof) = await this.SuggestionAsync(user, id, cancellationToken);
        await this.CommitAsync([Change.Assert(user), Change.Assert(proof), Change.Update(org, AddMember(org.Value, "member")),
            Change.Insert(new ProjectOrganizationMembership(user.InstanceId, org.Value.Id, user.Id, "member", clock.GetUtcNow())),
            this.Event(org.Value, "organizationMembership.created", user.Id, "member"),
            .. await this.ClearDismissalAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken)], cancellationToken);
    }

    public async Task DismissSuggestionAsync(UserRecord user, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var (org, proof) = await this.SuggestionAsync(user, id, cancellationToken);
        var dismissed = await this.DismissedAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken);
        if (!dismissed)
        {
            await this.CommitAsync([Change.Assert(user), Change.Assert(proof), Change.Update(org, org.Value),
                Change.Insert(new ProjectOrganizationDismissal(user.InstanceId, org.Value.Id, user.Id))], cancellationToken);
        }
    }

    private async Task<(Versioned<ProjectOrganizationRecord> Org, Versioned<ProjectOrganizationDomain> Proof)> SuggestionAsync(
        UserRecord user, string id, CancellationToken cancellationToken)
    {
        var domain = user.Email[(user.Email.LastIndexOf('@') + 1)..];
        var org = await this.ReadAsync(user.InstanceId, id, cancellationToken);
        var proof = await this.DomainProofAsync(user.InstanceId, org.Value.Id, domain, cancellationToken) ?? throw PlatformDirectory.NotFound();
        return org.Value.DomainJoin == "suggest" ? (org, proof) : throw PlatformDirectory.NotFound();
    }

    private async Task<Versioned<ProjectOrganizationRecord>?> OrganizationByIdAsync(string project, string org, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectOrganizationRecord>(
            "SELECT * FROM project_organizations WHERE project_id = @project AND id = @org", new { project, org }), cancellationToken));

    private async Task<Versioned<ProjectOrganizationDomain>?> DomainProofAsync(string project, string org, string domain, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectOrganizationDomain>(
            "SELECT * FROM project_organization_domains WHERE project_id = @project AND organization_id = @org AND domain = @domain AND verified_at IS NOT NULL",
            new { project, org, domain }), cancellationToken));

    private Task<bool> DismissedAsync(string project, string org, string userId, CancellationToken cancellationToken) =>
        data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM project_organization_dismissals WHERE project_id = @project AND organization_id = @org AND user_id = @userId)",
            new { project, org, userId }), cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Organization auto-join skipped for project {ProjectId}, organization {OrganizationId}: {Reason}.")]
    private static partial void AutoJoinSkipped(ILogger logger, string projectId, string organizationId, string reason);
}
