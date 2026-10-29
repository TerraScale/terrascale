using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

/// <summary>Platform management with user sign-in tokens, receipt-only account status, or service Basic credentials for deletion outbox operations.
/// Organization/project/API-key tokens cannot authenticate user routes. Pages are bounded, mutations and reads are sent once.</summary>
public sealed partial class TerraScaleAuthPlatformClient(TerraScaleAuthClient client)
{
    internal static string Proof(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 8192 || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("A step-up proof must be at most 8192 characters without whitespace or control characters.", nameof(value));
        return value;
    }

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

    /// <summary>
    /// sign_out_everywhere: POST /v1/terrascale/account/sign-out. Ends every hosted session, refresh token and sign-in
    /// access token of the caller's account on every device, including <paramref name="accessToken"/>; API keys stay valid.
    /// Sent once; repeating it is harmless but needs a new sign-in.
    /// </summary>
    public Task SignOutEverywhereAsync(
        string accessToken,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/account/sign-out",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>
    /// delete_account: DELETE /v1/terrascale/account with a fresh delete_account/ts-auth proof. Completed is true only
    /// for 204, false for 202 (pending; ts-auth finishes it in the background).
    /// The proof is single-use. Optionally retain and send a canonical 32-byte base64url deletion receipt before
    /// this first request. Retain the returned OperationGrant; <see cref="DeleteAccountAsync"/> continuation requires
    /// both the receipt and that grant with a valid sign-in. A lost/rejected response provides no grant.
    /// After token revocation, use <see cref="AccountDeletionCompletedAsync"/> with only the receipt for status.
    /// Each call is sent once, including on a lost or ambiguous response; never replay the consumed proof. 409 <c>last_owner</c>, <c>not_empty</c>, <c>closure_conflict</c> or <c>binding_conflict</c>: before
    /// anything was removed the account and its organizations stay, though project closures already started finish on
    /// their own; once removals began, the deletion is paused until the cause is resolved.
    /// </summary>
    public Task<TerraScaleAuthAccountDeletion> DeleteAccountWithProofAsync(
        string accessToken,
        string stepUpProof,
        string? deletionReceipt = null,
        CancellationToken cancellationToken = default) =>
        client.SendAccountDeletionWithProofAsync(TerraScaleAuthCredentials.Bearer(accessToken), Proof(stepUpProof), deletionReceipt, cancellationToken);

    /// <summary>
    /// delete_account: DELETE /v1/terrascale/account with the exact receipt authorized by ConsumeStepUpAsync or DeleteAccountWithProofAsync.
    /// Returns true only for 204 (completed), false only for 202 (pending). Authentication failures are errors.
    /// Generate the receipt from 32 cryptographically random bytes encoded as 43 unpadded base64url characters;
    /// retain it securely before sending, then use <see cref="AccountDeletionCompletedAsync"/> after sign-in revocation.
    /// Requires both the receipt and operation grant in X-TerraScale-Deletion-Authorization; never replays the original proof. Each call is sent once.
    /// </summary>
    public Task<bool> DeleteAccountAsync(
        string accessToken,
        string deletionReceipt,
        string deletionAuthorization,
        CancellationToken cancellationToken = default) =>
        client.SendForAccountDeletionAsync(HttpMethod.Delete, "/v1/terrascale/account",
            TerraScaleAuthCredentials.Bearer(accessToken), deletionReceipt, cancellationToken, DeletionAuthorization(deletionAuthorization));

    /// <summary>
    /// account_deletion_status: GET /v1/terrascale/account/deletion using only X-TerraScale-Deletion-Receipt, without a bearer or API key.
    /// Returns true only for 204 (completed), false only for 202 (pending). Unknown receipts (404), authentication
    /// failures (401), and other unexpected responses throw <see cref="TerraScaleAuthException"/>.
    /// This read neither creates a deletion nor exposes user data. Use an HttpClient without default Authorization.
    /// Each call is sent once.
    /// </summary>
    public Task<bool> AccountDeletionCompletedAsync(
        string deletionReceipt,
        CancellationToken cancellationToken = default) =>
        client.SendForAccountDeletionAsync(HttpMethod.Get, "/v1/terrascale/account/deletion",
            null, deletionReceipt, cancellationToken);

    /// <summary>
    /// account_deletion_status: GET /v1/terrascale/account/deletion?organization={organization} using only the receipt.
    /// Returns true only for 204: the account deletion completed and this candidate organization's atomic deletion fence
    /// exists. False (202) can mean incomplete deletion or a retained shared organization; it does not promise eventual
    /// removal. Unknown receipts or noncandidate organizations (404) throw <see cref="TerraScaleAuthException"/>.
    /// Remove a local organization only after its explicit true result. No bearer or API key is sent; default
    /// HttpClient Authorization is rejected. Each call is sent once.
    /// </summary>
    public Task<bool> AccountDeletionOrganizationCompletedAsync(
        string deletionReceipt,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendForAccountDeletionAsync(HttpMethod.Get, $"/v1/terrascale/account/deletion?organization={Id(organizationId)}",
            null, deletionReceipt, cancellationToken);

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

    /// <summary>
    /// delete_project: DELETE /v1/terrascale/organizations/{organization}/projects/{project}. True once the project is
    /// removed (204: TerraBase accepted its closure); false while the closure is pending (202; ts-auth finishes it in the
    /// background, and calling again resumes it). 409 <c>not_empty</c>, <c>closure_conflict</c> or <c>binding_conflict</c>
    /// changes nothing.
    /// </summary>
    public Task<bool> DeleteProjectAsync(
        string accessToken,
        string organizationId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForDeletionAsync($"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}",
            TerraScaleAuthCredentials.Bearer(accessToken), cancellationToken);

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

    /// <summary>create_organization_api_key: POST /v1/terrascale/organizations/{organization}/api-keys. Requires a fresh purpose-bound ts-auth proof.</summary>
    public Task<TerraScaleAuthPlatformApiKeySecret> CreateOrganizationApiKeyAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformCreateApiKey request,
        string stepUpProof,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken, Proof(stepUpProof));

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

    /// <summary>rotate_organization_api_key: POST /v1/terrascale/organizations/{organization}/api-keys/{key}/rotate. Requires a fresh purpose-bound ts-auth proof.</summary>
    public Task<TerraScaleAuthPlatformApiKeySecret> RotateOrganizationApiKeyAsync(
        string accessToken,
        string organizationId,
        string keyId,
        string stepUpProof,
        TerraScaleAuthPlatformRotateApiKey? request = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys/{Id(keyId)}/rotate",
            request, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRotateApiKey,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken, Proof(stepUpProof));

    /// <summary>create_project_api_key: POST /v1/terrascale/organizations/{organization}/projects/{project}/api-keys. Requires a fresh purpose-bound ts-auth proof.</summary>
    public Task<TerraScaleAuthPlatformApiKeySecret> CreateProjectApiKeyAsync(
        string accessToken,
        string organizationId,
        string projectId,
        TerraScaleAuthPlatformCreateApiKey request,
        string stepUpProof,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken, Proof(stepUpProof));

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

    /// <summary>rotate_project_api_key: POST /v1/terrascale/organizations/{organization}/projects/{project}/api-keys/{key}/rotate. Requires a fresh purpose-bound ts-auth proof.</summary>
    public Task<TerraScaleAuthPlatformApiKeySecret> RotateProjectApiKeyAsync(
        string accessToken,
        string organizationId,
        string projectId,
        string keyId,
        string stepUpProof,
        TerraScaleAuthPlatformRotateApiKey? request = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}/rotate",
            request, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRotateApiKey,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken, Proof(stepUpProof));

    /// <summary>create_invitation: POST /v1/terrascale/organizations/{organization}/invitations.</summary>
    public Task<TerraScaleAuthPlatformCreatedInvitation> CreateInvitationAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformCreateInvitation request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/invitations",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateInvitation,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreatedInvitation,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    /// <summary>list_invitations: GET /v1/terrascale/organizations/{organization}/invitations.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformInvitation>> ListInvitationsAsync(
        string accessToken,
        string organizationId,
        int? limit = null,
        string? after = null,
        string? projectId = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/invitations" + PageQuery(limit, after, projectId),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformInvitation,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>revoke_invitation: DELETE /v1/terrascale/organizations/{organization}/invitations/{invitation}.</summary>
    public Task RevokeInvitationAsync(
        string accessToken,
        string organizationId,
        string invitationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/invitations/{Id(invitationId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>lookup_invitation: POST /v1/terrascale/invitations/lookup.</summary>
    public Task<TerraScaleAuthPlatformInvitationLookup> LookupInvitationAsync(
        string accessToken,
        TerraScaleAuthPlatformInvitationToken request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/invitations/lookup",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationLookup,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>accept_invitation: POST /v1/terrascale/invitations/accept.</summary>
    public Task<TerraScaleAuthPlatformAcceptedInvitation> AcceptInvitationAsync(
        string accessToken,
        TerraScaleAuthPlatformInvitationToken request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAcceptedInvitation,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>decline_invitation: POST /v1/terrascale/invitations/decline.</summary>
    public Task DeclineInvitationAsync(
        string accessToken,
        TerraScaleAuthPlatformInvitationToken request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/invitations/decline",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>start_ownership_transfer: POST /v1/terrascale/organizations/{organization}/ownership-transfer.</summary>
    public Task<TerraScaleAuthPlatformOwnershipTransfer> StartOwnershipTransferAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformStartOwnershipTransfer request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformStartOwnershipTransfer,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOwnershipTransfer,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    /// <summary>get_ownership_transfer: GET /v1/terrascale/organizations/{organization}/ownership-transfer.</summary>
    public Task<TerraScaleAuthPlatformOwnershipTransfer> GetOwnershipTransferAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOwnershipTransfer,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>cancel_ownership_transfer: DELETE /v1/terrascale/organizations/{organization}/ownership-transfer.</summary>
    public Task CancelOwnershipTransferAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    /// <summary>confirm_ownership_transfer: POST /v1/terrascale/organizations/{organization}/ownership-transfer/confirm.</summary>
    public Task<TerraScaleAuthPlatformMember> ConfirmOwnershipTransferAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer/confirm",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformMember,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    /// <summary>list_audit_events: GET /v1/terrascale/organizations/{organization}/audit-events.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformAuditEvent>> ListAuditEventsAsync(
        string accessToken,
        string organizationId,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/audit-events" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformAuditEvent,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    private static string Id(string value) => TerraScaleIds.Validate(value);

    private static string PageQuery(int? limit, string? after, string? projectId = null)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Page limit must be 1 to 100.");
        if (after is not null) ArgumentException.ThrowIfNullOrEmpty(after);
        var parameters = new List<string>();
        if (limit is { } size) parameters.Add("limit=" + size.ToString(CultureInfo.InvariantCulture));
        if (after is not null) parameters.Add("after=" + Uri.EscapeDataString(after));
        if (projectId is not null) parameters.Add("project_id=" + Id(projectId));
        return parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters);
    }
}
