using Microsoft.EntityFrameworkCore;
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

        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        var count = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains
            .CountAsync(row => row.ProjectId == project && row.OrganizationId == org.Value.Id, token), cancellationToken).ConfigureAwait(false);
        if (count >= 100)
        {
            throw Conflict("domain_limit", "An organization has at most 100 domains.");
        }

        var record = new ProjectOrganizationDomain(project, org.Value.Id, domain, Tokens.RandomToken(40), null);
        await this.CommitAsync([Change.Update(org, org.Value), Change.Insert(record), this.Event(org.Value, "organizationDomain.created", domain)], cancellationToken).ConfigureAwait(false);
        return DomainBody(record);
    }

    public async Task<ProjectOrganizationDomainPage> DomainsAsync(string project, string id, CancellationToken cancellationToken)
    {
        var org = (await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false)).Value.Id;
        var rows = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains.Where(row => row.ProjectId == project && row.OrganizationId == org)
            .OrderBy(row => row.Domain).Take(100).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new([.. rows.Select(DomainBody)]);
    }

    public async Task<ProjectOrganizationDomainBody?> ChangeDomainAsync(string project, string id, string domain, bool remove, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        var row = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains
            .Where(row => row.ProjectId == project && row.OrganizationId == org.Value.Id && row.Domain == domain)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        if (!remove && !await this.PublishedAsync(row.Value, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict("domain_unverified", "The verification TXT record was not found.");
        }

        var updated = row.Value with { VerifiedAt = row.Value.VerifiedAt ?? clock.GetUtcNow(), CheckedAt = clock.GetUtcNow(), FailedChecks = 0 };
        await this.CommitAsync([Change.Update(org, org.Value), remove ? Change.Delete(row) : Change.Update(row, updated),
            this.Event(org.Value, remove ? "organizationDomain.deleted" : "organizationDomain.updated", domain)], cancellationToken).ConfigureAwait(false);
        return remove ? null : DomainBody(updated);
    }

    public async Task<IReadOnlyList<OrganizationClaims>> AutoJoinAsync(UserRecord user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.EmailVerified)
        {
            return [];
        }

        var domain = user.Email[(user.Email.LastIndexOf('@') + 1)..];
        var match = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains
            .Where(row => row.ProjectId == user.InstanceId && row.Domain == domain && row.VerifiedAt != null)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return [];
        }

        var org = await data.ReadAsync((db, token) => db.ProjectOrganizations
            .Where(row => row.ProjectId == user.InstanceId && row.Id == match.Value.OrganizationId).WithVersion().SingleOrDefaultAsync(token),
            cancellationToken).ConfigureAwait(false);
        if (org is null || org.Value.DomainJoin == "off" || await this.MemberAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken).ConfigureAwait(false) is not null)
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
            var dismissed = await data.ReadAsync((db, token) => db.ProjectOrganizationDismissals
                .AnyAsync(row => row.ProjectId == user.InstanceId && row.OrganizationId == org.Value.Id && row.UserId == user.Id, token),
                cancellationToken).ConfigureAwait(false);
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
            this.Event(org.Value, "organizationMembership.created", user.Id, "member")
        ];
        if (!await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
        {
            AutoJoinSkipped(logger, user.InstanceId, org.Value.Id, "version_conflict");
            var refreshed = await data.ReadAsync((db, token) => db.ProjectOrganizations
                .Where(row => row.ProjectId == user.InstanceId && row.Id == org.Value.Id).WithVersion().SingleOrDefaultAsync(token),
                cancellationToken).ConfigureAwait(false);
            if (refreshed is null)
            {
                return [];
            }

            var proof = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains
                .Where(row => row.ProjectId == user.InstanceId && row.OrganizationId == refreshed.Value.Id && row.Domain == domain && row.VerifiedAt != null)
                .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
            if (proof is not null && refreshed.Value.DomainJoin == "auto" && refreshed.Value.Admins > 0
                && (refreshed.Value.MaxMembers is null || refreshed.Value.Members < refreshed.Value.MaxMembers)
                && await this.MemberAsync(user.InstanceId, refreshed.Value.Id, user.Id, cancellationToken).ConfigureAwait(false) is null)
            {
                await data.SaveAsync([Change.Assert(user), Change.Assert(proof), Change.Update(refreshed, AddMember(refreshed.Value, "member")),
                    Change.Insert(new ProjectOrganizationMembership(user.InstanceId, refreshed.Value.Id, user.Id, "member", clock.GetUtcNow())),
                    this.Event(refreshed.Value, "organizationMembership.created", user.Id, "member")], cancellationToken).ConfigureAwait(false);
            }
        }

        return [];
    }

    public async Task JoinSuggestedAsync(UserRecord user, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var (org, proof) = await this.SuggestionAsync(user, id, cancellationToken).ConfigureAwait(false);
        await this.CommitAsync([Change.Assert(user), Change.Assert(proof), Change.Update(org, AddMember(org.Value, "member")),
            Change.Insert(new ProjectOrganizationMembership(user.InstanceId, org.Value.Id, user.Id, "member", clock.GetUtcNow())),
            this.Event(org.Value, "organizationMembership.created", user.Id, "member")], cancellationToken).ConfigureAwait(false);
    }

    public async Task DismissSuggestionAsync(UserRecord user, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var (org, proof) = await this.SuggestionAsync(user, id, cancellationToken).ConfigureAwait(false);
        var dismissed = await data.ReadAsync((db, token) => db.ProjectOrganizationDismissals
            .AnyAsync(row => row.ProjectId == user.InstanceId && row.OrganizationId == org.Value.Id && row.UserId == user.Id, token),
            cancellationToken).ConfigureAwait(false);
        if (!dismissed)
        {
            await this.CommitAsync([Change.Assert(user), Change.Assert(proof), Change.Update(org, org.Value),
                Change.Insert(new ProjectOrganizationDismissal(user.InstanceId, org.Value.Id, user.Id))], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(Versioned<ProjectOrganizationRecord> Org, Versioned<ProjectOrganizationDomain> Proof)> SuggestionAsync(
        UserRecord user, string id, CancellationToken cancellationToken)
    {
        if (!user.EmailVerified)
        {
            throw new ApiException(403, "email_unverified", "Verify your email before joining an organization.");
        }

        var domain = user.Email[(user.Email.LastIndexOf('@') + 1)..];
        var org = await this.ReadAsync(user.InstanceId, id, cancellationToken).ConfigureAwait(false);
        var proof = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains
            .Where(row => row.ProjectId == user.InstanceId && row.OrganizationId == org.Value.Id && row.Domain == domain && row.VerifiedAt != null)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        return org.Value.DomainJoin == "suggest" ? (org, proof) : throw PlatformDirectory.NotFound();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Organization auto-join skipped for project {ProjectId}, organization {OrganizationId}: {Reason}.")]
    private static partial void AutoJoinSkipped(ILogger logger, string projectId, string organizationId, string reason);
}
