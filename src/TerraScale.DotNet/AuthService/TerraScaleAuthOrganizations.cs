using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Organizations: records, memberships, invitations, and per-instance
/// enrollment domains. Domain uniqueness is per instance, not per organization.
/// </summary>
public sealed class TerraScaleAuthOrganizationsClient(TerraScaleAuthClient client)
{
    /// <summary>
    /// Creates an organization. The slug defaults to one derived from the name
    /// and is normalized to lowercase; a taken slug fails with <c>organization_exists</c>.
    /// </summary>
    public Task<TerraScaleAuthOrganization> CreateAsync(
        string name,
        string? slug = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return client.SendJsonAsync<TerraScaleAuthCreateOrganization, TerraScaleAuthOrganization>(
            HttpMethod.Post,
            client.ManagementRoute("/organizations"),
            new TerraScaleAuthCreateOrganization(name, slug),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateOrganization,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthOrganization,
            cancellationToken);
    }

    /// <summary>Lists every organization in the instance.</summary>
    public Task<IReadOnlyList<TerraScaleAuthOrganization>> ListAsync(CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/organizations"),
            TerraScaleAuthJsonContext.Default.IReadOnlyListTerraScaleAuthOrganization,
            cancellationToken);

    /// <summary>Gets one organization.</summary>
    public Task<TerraScaleAuthOrganization> GetAsync(string organizationId, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            OrganizationRoute(organizationId),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthOrganization,
            cancellationToken);

    /// <summary>Renames one organization.</summary>
    public Task<TerraScaleAuthOrganization> RenameAsync(string organizationId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return client.SendJsonAsync<TerraScaleAuthRenameOrganization, TerraScaleAuthOrganization>(
            HttpMethod.Patch,
            OrganizationRoute(organizationId),
            new TerraScaleAuthRenameOrganization(name),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthRenameOrganization,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthOrganization,
            cancellationToken);
    }

    /// <summary>Deletes one organization and releases its enrolled domains.</summary>
    public Task DeleteAsync(string organizationId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(HttpMethod.Delete, OrganizationRoute(organizationId), cancellationToken);

    /// <summary>Adds a user to an organization with a role (1–64 characters).</summary>
    public Task<TerraScaleAuthMembership> AddMemberAsync(string organizationId, string userId, string role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return client.SendJsonAsync<TerraScaleAuthCreateMembership, TerraScaleAuthMembership>(
            HttpMethod.Post,
            OrganizationRoute(organizationId) + "/memberships",
            new TerraScaleAuthCreateMembership(userId, role),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateMembership,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthMembership,
            cancellationToken);
    }

    /// <summary>Lists an organization's memberships with the embedded user view.</summary>
    public Task<IReadOnlyList<TerraScaleAuthMembership>> ListMembershipsAsync(string organizationId, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            OrganizationRoute(organizationId) + "/memberships",
            TerraScaleAuthJsonContext.Default.IReadOnlyListTerraScaleAuthMembership,
            cancellationToken);

    /// <summary>Changes one member's role.</summary>
    public Task<TerraScaleAuthMembership> UpdateMemberRoleAsync(string organizationId, string membershipId, string role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return client.SendJsonAsync<TerraScaleAuthUpdateMembership, TerraScaleAuthMembership>(
            HttpMethod.Patch,
            OrganizationRoute(organizationId) + "/memberships/" + TerraScaleAuthPaths.Escape(membershipId),
            new TerraScaleAuthUpdateMembership(role),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthUpdateMembership,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthMembership,
            cancellationToken);
    }

    /// <summary>Removes one membership.</summary>
    public Task RemoveMemberAsync(string organizationId, string membershipId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(
            HttpMethod.Delete,
            OrganizationRoute(organizationId) + "/memberships/" + TerraScaleAuthPaths.Escape(membershipId),
            cancellationToken);

    /// <summary>Invites an email address to an organization; the invitation expires in seven days.</summary>
    public Task<TerraScaleAuthInvitation> CreateInvitationAsync(string organizationId, string email, string role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return client.SendJsonAsync<TerraScaleAuthCreateInvitation, TerraScaleAuthInvitation>(
            HttpMethod.Post,
            OrganizationRoute(organizationId) + "/invitations",
            new TerraScaleAuthCreateInvitation(email, role),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateInvitation,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthInvitation,
            cancellationToken);
    }

    /// <summary>Lists an organization's invitations.</summary>
    public Task<IReadOnlyList<TerraScaleAuthInvitation>> ListInvitationsAsync(string organizationId, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            OrganizationRoute(organizationId) + "/invitations",
            TerraScaleAuthJsonContext.Default.IReadOnlyListTerraScaleAuthInvitation,
            cancellationToken);

    /// <summary>Revokes one pending invitation.</summary>
    public Task<TerraScaleAuthInvitation> RevokeInvitationAsync(string organizationId, string invitationId, CancellationToken cancellationToken = default) =>
        client.PostJsonAsync(
            OrganizationRoute(organizationId) + "/invitations/" + TerraScaleAuthPaths.Escape(invitationId) + "/revoke",
            TerraScaleAuthJsonContext.Default.TerraScaleAuthInvitation,
            cancellationToken);

    /// <summary>Enrolls a hostname for just-in-time provisioning. The domain is unique per instance.</summary>
    public Task<TerraScaleAuthOrgDomain> AddDomainAsync(string organizationId, string domain, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return client.SendJsonAsync<TerraScaleAuthAddDomain, TerraScaleAuthOrgDomain>(
            HttpMethod.Post,
            OrganizationRoute(organizationId) + "/domains",
            new TerraScaleAuthAddDomain(domain),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthAddDomain,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthOrgDomain,
            cancellationToken);
    }

    /// <summary>Lists the domains enrolled for one organization.</summary>
    public Task<IReadOnlyList<TerraScaleAuthOrgDomain>> ListDomainsAsync(string organizationId, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            OrganizationRoute(organizationId) + "/domains",
            TerraScaleAuthJsonContext.Default.IReadOnlyListTerraScaleAuthOrgDomain,
            cancellationToken);

    /// <summary>Removes one enrolled domain; it becomes available to other organizations.</summary>
    public Task RemoveDomainAsync(string organizationId, string domainId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(
            HttpMethod.Delete,
            OrganizationRoute(organizationId) + "/domains/" + TerraScaleAuthPaths.Escape(domainId),
            cancellationToken);

    /// <summary>
    /// Enrolls the user whose email matches an enrolled domain into the owning
    /// organization (role <c>member</c>). Idempotent; the user's email must end
    /// with <c>@{domain}</c>.
    /// </summary>
    public Task<TerraScaleAuthDomainEnrollment> EnrollDomainUserAsync(string domain, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return client.SendJsonAsync<TerraScaleAuthEnrollDomainUser, TerraScaleAuthDomainEnrollment>(
            HttpMethod.Post,
            client.ManagementRoute("/domains/" + TerraScaleAuthPaths.Escape(domain) + "/enroll"),
            new TerraScaleAuthEnrollDomainUser(userId),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthEnrollDomainUser,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthDomainEnrollment,
            cancellationToken);
    }

    private string OrganizationRoute(string organizationId) =>
        client.ManagementRoute("/organizations/" + TerraScaleAuthPaths.Escape(organizationId));
}

/// <summary>An organization record.</summary>
public sealed record TerraScaleAuthOrganization(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("created_at")] long CreatedAt);

/// <summary>A user's membership in an organization, with the embedded user view.</summary>
public sealed record TerraScaleAuthMembership(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("user")] TerraScaleAuthMembershipUser? User);

/// <summary>The public user view embedded in membership listings.</summary>
public sealed record TerraScaleAuthMembershipUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("name")] string? Name);

/// <summary>An organization invitation.</summary>
public sealed record TerraScaleAuthInvitation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expires_at")] long ExpiresAt,
    [property: JsonPropertyName("created_at")] long CreatedAt);

/// <summary>A hostname enrolled for just-in-time provisioning.</summary>
public sealed record TerraScaleAuthOrgDomain(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("created_at")] long CreatedAt);

/// <summary>The result of a domain enrollment: the owning organization id.</summary>
public sealed record TerraScaleAuthDomainEnrollment(
    [property: JsonPropertyName("org_id")] string OrganizationId);

internal sealed record TerraScaleAuthCreateOrganization(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string? Slug);

internal sealed record TerraScaleAuthRenameOrganization(
    [property: JsonPropertyName("name")] string Name);

internal sealed record TerraScaleAuthCreateMembership(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("role")] string Role);

internal sealed record TerraScaleAuthUpdateMembership(
    [property: JsonPropertyName("role")] string Role);

internal sealed record TerraScaleAuthCreateInvitation(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role);

internal sealed record TerraScaleAuthAddDomain(
    [property: JsonPropertyName("domain")] string Domain);

internal sealed record TerraScaleAuthEnrollDomainUser(
    [property: JsonPropertyName("user_id")] string UserId);
