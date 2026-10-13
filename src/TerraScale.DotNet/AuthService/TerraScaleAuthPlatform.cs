using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

/// <summary>Platform management with the caller's user sign-in token. Organization/project/API-key tokens
/// cannot authenticate these routes. Pages are bounded, mutations and reads are sent once.</summary>
public sealed class TerraScaleAuthPlatformClient(TerraScaleAuthClient client)
{
    /// <summary>create_organization: POST /v1/terrascale/organizations.</summary>
    public Task<TerraScaleAuthPlatformOrganization> CreateOrganizationAsync(
        string accessToken,
        TerraScaleAuthPlatformName request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/organizations",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    /// <summary>list_organizations: GET /v1/terrascale/organizations.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformOrganization>> ListOrganizationsAsync(
        string accessToken,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, "/v1/terrascale/organizations" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>get_organization: GET /v1/terrascale/organizations/{organization}.</summary>
    public Task<TerraScaleAuthPlatformOrganization> GetOrganizationAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>rename_organization: PATCH /v1/terrascale/organizations/{organization}.</summary>
    public Task<TerraScaleAuthPlatformOrganization> RenameOrganizationAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformName request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Patch, $"/v1/terrascale/organizations/{Id(organizationId)}",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>delete_organization: DELETE /v1/terrascale/organizations/{organization}.</summary>
    public Task DeleteOrganizationAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>list_members: GET /v1/terrascale/organizations/{organization}/members.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformMember>> ListMembersAsync(
        string accessToken,
        string organizationId,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/members" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformMember,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>change_member_role: PUT /v1/terrascale/organizations/{organization}/members/{user}/role.</summary>
    public Task<TerraScaleAuthPlatformMember> ChangeMemberRoleAsync(
        string accessToken,
        string organizationId,
        string userId,
        TerraScaleAuthPlatformRole request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/members/{Id(userId)}/role",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRole,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformMember,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>remove_member: DELETE /v1/terrascale/organizations/{organization}/members/{user}.</summary>
    public Task RemoveMemberAsync(
        string accessToken,
        string organizationId,
        string userId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/members/{Id(userId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>leave_organization: POST /v1/terrascale/organizations/{organization}/leave.</summary>
    public Task LeaveOrganizationAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/leave",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>create_project: POST /v1/terrascale/organizations/{organization}/projects.</summary>
    public Task<TerraScaleAuthPlatformProject> CreateProjectAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformName request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/projects",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    /// <summary>list_projects: GET /v1/terrascale/organizations/{organization}/projects.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProject>> ListProjectsAsync(
        string accessToken,
        string organizationId,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformProject,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>get_project: GET /v1/terrascale/organizations/{organization}/projects/{project}.</summary>
    public Task<TerraScaleAuthPlatformProject> GetProjectAsync(
        string accessToken,
        string organizationId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>rename_project: PATCH /v1/terrascale/organizations/{organization}/projects/{project}.</summary>
    public Task<TerraScaleAuthPlatformProject> RenameProjectAsync(
        string accessToken,
        string organizationId,
        string projectId,
        TerraScaleAuthPlatformName request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Patch, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>delete_project: DELETE /v1/terrascale/organizations/{organization}/projects/{project}.</summary>
    public Task DeleteProjectAsync(
        string accessToken,
        string organizationId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>list_project_members: GET /v1/terrascale/organizations/{organization}/projects/{project}/members.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> ListProjectMembersAsync(
        string accessToken,
        string organizationId,
        string projectId,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/members" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformProjectMember,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>set_project_member_role: PUT /v1/terrascale/organizations/{organization}/projects/{project}/members/{user}.</summary>
    public Task<TerraScaleAuthPlatformProjectMember> SetProjectMemberRoleAsync(
        string accessToken,
        string organizationId,
        string projectId,
        string userId,
        TerraScaleAuthPlatformRole request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/members/{Id(userId)}",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRole,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProjectMember,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>remove_project_member: DELETE /v1/terrascale/organizations/{organization}/projects/{project}/members/{user}.</summary>
    public Task RemoveProjectMemberAsync(
        string accessToken,
        string organizationId,
        string projectId,
        string userId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/members/{Id(userId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>create_organization_api_key: POST /v1/terrascale/organizations/{organization}/api-keys.</summary>
    public Task<TerraScaleAuthPlatformApiKeySecret> CreateOrganizationApiKeyAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformCreateApiKey request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    /// <summary>list_organization_api_keys: GET /v1/terrascale/organizations/{organization}/api-keys.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformApiKey>> ListOrganizationApiKeysAsync(
        string accessToken,
        string organizationId,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformApiKey,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>get_organization_api_key: GET /v1/terrascale/organizations/{organization}/api-keys/{key}.</summary>
    public Task<TerraScaleAuthPlatformApiKey> GetOrganizationApiKeyAsync(
        string accessToken,
        string organizationId,
        string keyId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys/{Id(keyId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>revoke_organization_api_key: DELETE /v1/terrascale/organizations/{organization}/api-keys/{key}.</summary>
    public Task RevokeOrganizationApiKeyAsync(
        string accessToken,
        string organizationId,
        string keyId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys/{Id(keyId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>rotate_organization_api_key: POST /v1/terrascale/organizations/{organization}/api-keys/{key}/rotate.</summary>
    public Task<TerraScaleAuthPlatformApiKeySecret> RotateOrganizationApiKeyAsync(
        string accessToken,
        string organizationId,
        string keyId,
        TerraScaleAuthPlatformRotateApiKey? request = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys/{Id(keyId)}/rotate",
            request, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRotateApiKey,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>create_project_api_key: POST /v1/terrascale/organizations/{organization}/projects/{project}/api-keys.</summary>
    public Task<TerraScaleAuthPlatformApiKeySecret> CreateProjectApiKeyAsync(
        string accessToken,
        string organizationId,
        string projectId,
        TerraScaleAuthPlatformCreateApiKey request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    /// <summary>list_project_api_keys: GET /v1/terrascale/organizations/{organization}/projects/{project}/api-keys.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformApiKey>> ListProjectApiKeysAsync(
        string accessToken,
        string organizationId,
        string projectId,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformApiKey,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>get_project_api_key: GET /v1/terrascale/organizations/{organization}/projects/{project}/api-keys/{key}.</summary>
    public Task<TerraScaleAuthPlatformApiKey> GetProjectApiKeyAsync(
        string accessToken,
        string organizationId,
        string projectId,
        string keyId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>revoke_project_api_key: DELETE /v1/terrascale/organizations/{organization}/projects/{project}/api-keys/{key}.</summary>
    public Task RevokeProjectApiKeyAsync(
        string accessToken,
        string organizationId,
        string projectId,
        string keyId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>rotate_project_api_key: POST /v1/terrascale/organizations/{organization}/projects/{project}/api-keys/{key}/rotate.</summary>
    public Task<TerraScaleAuthPlatformApiKeySecret> RotateProjectApiKeyAsync(
        string accessToken,
        string organizationId,
        string projectId,
        string keyId,
        TerraScaleAuthPlatformRotateApiKey? request = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}/rotate",
            request, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRotateApiKey,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    private static string Id(string value) => TerraScaleIds.Validate(value);

    private static string PageQuery(int? limit, string? after)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Page limit must be 1 to 100.");
        var parameters = new List<string>();
        if (limit is { } size) parameters.Add("limit=" + size.ToString(CultureInfo.InvariantCulture));
        if (after is not null) parameters.Add("after=" + Uri.EscapeDataString(after));
        return parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters);
    }
}
