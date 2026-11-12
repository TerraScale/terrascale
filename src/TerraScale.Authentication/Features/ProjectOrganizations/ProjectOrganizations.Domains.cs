using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.Sso;
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
        await this.CommitAsync([Change.Update(org, org.Value), Change.Insert(record)], cancellationToken).ConfigureAwait(false);
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
        if (!remove && row.Value.VerifiedAt is null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{SsoConnections.Resolver}?name=_terrascale-org.{Uri.EscapeDataString(domain)}&type=TXT");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await clients.CreateClient(SsoConnections.HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var expected = "terrascale-org=" + row.Value.VerificationToken;
            if (!answer.RootElement.TryGetProperty("Answer", out var records) || records.ValueKind != JsonValueKind.Array
                || !records.EnumerateArray().Any(record => record.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var number) && number == 16
                    && record.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString()!.Replace("\"", "", StringComparison.Ordinal) == expected))
            {
                throw Conflict("domain_unverified", "The verification TXT record was not found.");
            }
        }

        var updated = row.Value with { VerifiedAt = row.Value.VerifiedAt ?? clock.GetUtcNow() };
        await this.CommitAsync([Change.Update(org, org.Value), remove ? Change.Delete(row) : Change.Update(row, updated)], cancellationToken).ConfigureAwait(false);
        return remove ? null : DomainBody(updated);
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

        var org = await this.ReadAsync(user.InstanceId, match.Value.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (org.Value.DomainJoin == "off" || await this.MemberAsync(user.InstanceId, org.Value.Id, user.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            return [];
        }

        if (org.Value.DomainJoin == "suggest")
        {
            return [new(org.Value.Id, org.Value.Slug, "member")];
        }

        await this.CommitAsync([Change.Assert(user), Change.Assert(match), Change.Update(org, AddMember(org.Value, "member")),
            Change.Insert(new ProjectOrganizationMembership(user.InstanceId, org.Value.Id, user.Id, "member", clock.GetUtcNow())),
            this.Event(org.Value, "organizationMembership.created", user.Id, "member")], cancellationToken).ConfigureAwait(false);
        return [];
    }

    public async Task JoinSuggestedAsync(UserRecord user, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var domain = user.Email[(user.Email.LastIndexOf('@') + 1)..];
        var org = await this.ReadAsync(user.InstanceId, id, cancellationToken).ConfigureAwait(false);
        var proof = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains
            .Where(row => row.ProjectId == user.InstanceId && row.OrganizationId == org.Value.Id && row.Domain == domain && row.VerifiedAt != null)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        if (org.Value.DomainJoin != "suggest")
        {
            throw PlatformDirectory.NotFound();
        }

        await this.CommitAsync([Change.Assert(user), Change.Assert(proof), Change.Update(org, AddMember(org.Value, "member")),
            Change.Insert(new ProjectOrganizationMembership(user.InstanceId, org.Value.Id, user.Id, "member", clock.GetUtcNow())),
            this.Event(org.Value, "organizationMembership.created", user.Id, "member")], cancellationToken).ConfigureAwait(false);
    }
}
