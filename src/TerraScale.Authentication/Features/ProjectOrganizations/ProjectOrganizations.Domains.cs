using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed partial class ProjectOrganizationDirectory
{
    public static ProjectOrganizationDomainBody DomainBody(ProjectOrganizationDomain domain) =>
        new(domain.Domain, "_terrascale-org." + domain.Domain, "terrascale-org=" + domain.VerificationToken, domain.VerifiedAt);

    public async Task<DomainAdded> AddDomainAsync(string project, string id, string? domain, CancellationToken cancellationToken)
    {
        domain = domain?.Trim().ToLowerInvariant();
        if (domain is not { Length: >= 4 and <= 253 } || !domain.Contains('.', StringComparison.Ordinal)
            || domain.Split('.').Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-')
            || Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
        {
            return new Validation("domain must be a DNS domain without a scheme or path.");
        }

        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return NoSuchOrganization();
        }

        var count = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains
            .CountAsync(row => row.ProjectId == project && row.OrganizationId == org.Value.Id, token), cancellationToken).ConfigureAwait(false);
        if (count >= 100)
        {
            return new Conflict("An organization has at most 100 domains.", "domain_limit");
        }

        var record = new ProjectOrganizationDomain(project, org.Value.Id, domain, Tokens.RandomToken(40), null);
        return await data.SaveAsync([Change.Update(org, org.Value), Change.Insert(record), this.Event(org.Value, "organizationDomain.created", domain)], cancellationToken).ConfigureAwait(false)
            ? new DomainAdded(DomainBody(record))
            : Refused();
    }

    public async Task<DomainsListed> DomainsAsync(string project, string id, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return NoSuchOrganization();
        }

        var rows = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains.Where(row => row.ProjectId == project && row.OrganizationId == org.Value.Id)
            .OrderBy(row => row.Domain).Take(100).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return new ProjectOrganizationDomainPage([.. rows.Select(DomainBody)]);
    }

    public async Task<DomainVerified> VerifyDomainAsync(string project, string id, string domain, CancellationToken cancellationToken)
    {
        var found = await this.DomainAsync(project, id, domain, cancellationToken).ConfigureAwait(false);
        if (found is not OrganizationDomainPair pair)
        {
            return NoSuchOrganization();
        }

        if (!await this.PublishedAsync(pair.Row.Value, cancellationToken).ConfigureAwait(false))
        {
            return new Conflict("The verification TXT record was not found.", "domain_unverified");
        }

        var updated = pair.Row.Value with { VerifiedAt = pair.Row.Value.VerifiedAt ?? clock.GetUtcNow(), CheckedAt = clock.GetUtcNow(), FailedChecks = 0 };
        return await data.SaveAsync([Change.Update(pair.Org, pair.Org.Value), Change.Update(pair.Row, updated),
            this.Event(pair.Org.Value, "organizationDomain.updated", domain)], cancellationToken).ConfigureAwait(false)
            ? DomainBody(updated)
            : Refused();
    }

    public async Task<DomainDeleted> DeleteDomainAsync(string project, string id, string domain, CancellationToken cancellationToken)
    {
        var found = await this.DomainAsync(project, id, domain, cancellationToken).ConfigureAwait(false);
        if (found is not OrganizationDomainPair pair)
        {
            return NoSuchOrganization();
        }

        return await data.SaveAsync([Change.Update(pair.Org, pair.Org.Value), Change.Delete(pair.Row),
            this.Event(pair.Org.Value, "organizationDomain.deleted", domain)], cancellationToken).ConfigureAwait(false)
            ? new Done()
            : Refused();
    }

    private async Task<Found<OrganizationDomainPair>> DomainAsync(string project, string id, string domain, CancellationToken cancellationToken)
    {
        var org = await this.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return NoSuchOrganization();
        }

        var row = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains
            .Where(row => row.ProjectId == project && row.OrganizationId == org.Value.Id && row.Domain == domain)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        return row is null ? NoSuchOrganization() : new OrganizationDomainPair(org, row);
    }

    public async Task<IReadOnlyList<OrganizationClaims>> AutoJoinAsync(UserRecord user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
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
            this.Event(org.Value, "organizationMembership.created", user.Id, "member"),
            .. await this.ClearDismissalAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken).ConfigureAwait(false)
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
                    this.Event(refreshed.Value, "organizationMembership.created", user.Id, "member"),
                    .. await this.ClearDismissalAsync(user.InstanceId, refreshed.Value.Id, user.Id, cancellationToken).ConfigureAwait(false)], cancellationToken).ConfigureAwait(false);
            }
        }

        return [];
    }

    public async Task<SuggestionHandled> JoinSuggestedAsync(UserRecord user, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var suggestion = await this.SuggestionAsync(user, id, cancellationToken).ConfigureAwait(false);
        if (suggestion is not OrganizationDomainSuggestion join)
        {
            return NoSuchOrganization();
        }

        if (RefusesMembership(join.Org.Value, "member") is { } refused)
        {
            return refused;
        }

        return await data.SaveAsync([Change.Assert(user), Change.Assert(join.Proof), Change.Update(join.Org, AddMember(join.Org.Value, "member")),
            Change.Insert(new ProjectOrganizationMembership(user.InstanceId, join.Org.Value.Id, user.Id, "member", clock.GetUtcNow())),
            this.Event(join.Org.Value, "organizationMembership.created", user.Id, "member"),
            .. await this.ClearDismissalAsync(user.InstanceId, join.Org.Value.Id, user.Id, cancellationToken).ConfigureAwait(false)], cancellationToken).ConfigureAwait(false)
            ? new Done()
            : Refused();
    }

    public async Task<SuggestionHandled> DismissSuggestionAsync(UserRecord user, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var suggestion = await this.SuggestionAsync(user, id, cancellationToken).ConfigureAwait(false);
        if (suggestion is not OrganizationDomainSuggestion dismiss)
        {
            return NoSuchOrganization();
        }

        var dismissed = await data.ReadAsync((db, token) => db.ProjectOrganizationDismissals
            .AnyAsync(row => row.ProjectId == user.InstanceId && row.OrganizationId == dismiss.Org.Value.Id && row.UserId == user.Id, token),
            cancellationToken).ConfigureAwait(false);
        if (dismissed)
        {
            return new Done();
        }

        return await data.SaveAsync([Change.Assert(user), Change.Assert(dismiss.Proof), Change.Update(dismiss.Org, dismiss.Org.Value),
            Change.Insert(new ProjectOrganizationDismissal(user.InstanceId, dismiss.Org.Value.Id, user.Id))], cancellationToken).ConfigureAwait(false)
            ? new Done()
            : Refused();
    }

    private async Task<Found<OrganizationDomainSuggestion>> SuggestionAsync(UserRecord user, string id, CancellationToken cancellationToken)
    {
        var domain = user.Email[(user.Email.LastIndexOf('@') + 1)..];
        var org = await this.ReadAsync(user.InstanceId, id, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return NoSuchOrganization();
        }

        var proof = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains
            .Where(row => row.ProjectId == user.InstanceId && row.OrganizationId == org.Value.Id && row.Domain == domain && row.VerifiedAt != null)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        return proof is null || org.Value.DomainJoin != "suggest" ? NoSuchOrganization() : new OrganizationDomainSuggestion(org, proof);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Organization auto-join skipped for project {ProjectId}, organization {OrganizationId}: {Reason}.")]
    private static partial void AutoJoinSkipped(ILogger logger, string projectId, string organizationId, string reason);
}

public readonly union DomainAdded(ProjectOrganizationDomainBody, NotFound, Validation, Conflict);

public readonly union DomainsListed(ProjectOrganizationDomainPage, NotFound);

public readonly union DomainVerified(ProjectOrganizationDomainBody, NotFound, Conflict);

public readonly union DomainDeleted(Done, NotFound, Conflict);

public sealed record OrganizationDomainPair(Versioned<ProjectOrganizationRecord> Org, Versioned<ProjectOrganizationDomain> Row);

public sealed record OrganizationDomainSuggestion(Versioned<ProjectOrganizationRecord> Org, Versioned<ProjectOrganizationDomain> Proof);

public readonly union SuggestionHandled(Done, NotFound, Conflict);
