using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthPlatformClient(TerraScaleAuthClient client)
{
    internal static string Proof(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 8192 || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("A step-up proof must be at most 8192 characters without whitespace or control characters.", nameof(value));
        return value;
    }

    public Task<AuthResult<TerraScaleAuthPlatformOrganization>> CreateOrganizationAsync(
        string accessToken,
        TerraScaleAuthPlatformCreateOrganization request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/organizations",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateOrganization,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformOrganization>>> ListOrganizationsAsync(
        string accessToken,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, "/v1/terrascale/organizations" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformOrganization>> GetOrganizationAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformOrganization>> RenameOrganizationAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformName request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Patch, $"/v1/terrascale/organizations/{Id(organizationId)}",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOrganization,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public async Task<AuthOutcome> DeleteOrganizationAsync(
        string accessToken,
        string organizationId,
        string stepUpProof,
        CancellationToken cancellationToken = default)
    {
        var organization = Id(organizationId);
        var proof = Proof(stepUpProof);
        var consumed = await ConsumeStepUpAsync(accessToken, new(proof, "delete_organization", "ts-auth", null, organization), cancellationToken).ConfigureAwait(false);
        return consumed switch
        {
            Done => await client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{organization}",
                null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
                TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, 16 * 1024, cancellationToken, proof).ConfigureAwait(false),
            TerraScaleAuthStepUpGrant => TerraScaleAuthErrors.InvalidResponse("ts-auth returned an unexpected deletion grant for an organization."),
            AuthRefused refused => refused,
        };
    }

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformMember>>> ListMembersAsync(
        string accessToken,
        string organizationId,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/members" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformMember,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformMember>> ChangeMemberRoleAsync(
        string accessToken,
        string organizationId,
        string userId,
        TerraScaleAuthPlatformRole request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/members/{Id(userId)}/role",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformRole,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformMember,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthOutcome> RemoveMemberAsync(
        string accessToken,
        string organizationId,
        string userId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/members/{Id(userId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    public Task<AuthOutcome> LeaveOrganizationAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/leave",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    public Task<AuthOutcome> SignOutEverywhereAsync(
        string accessToken,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/account/sign-out",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthAccountDeletion>> DeleteAccountWithProofAsync(
        string accessToken,
        string stepUpProof,
        string? deletionReceipt = null,
        CancellationToken cancellationToken = default) =>
        client.SendAccountDeletionWithProofAsync(TerraScaleAuthCredentials.Bearer(accessToken), Proof(stepUpProof), deletionReceipt, cancellationToken);

    public Task<AuthCompletion> DeleteAccountAsync(
        string accessToken,
        string deletionReceipt,
        string deletionAuthorization,
        CancellationToken cancellationToken = default) =>
        client.SendForAccountDeletionAsync(HttpMethod.Delete, "/v1/terrascale/account",
            TerraScaleAuthCredentials.Bearer(accessToken), deletionReceipt, cancellationToken, DeletionAuthorization(deletionAuthorization));

    public Task<AuthCompletion> AccountDeletionCompletedAsync(
        string deletionReceipt,
        CancellationToken cancellationToken = default) =>
        client.SendForAccountDeletionAsync(HttpMethod.Get, "/v1/terrascale/account/deletion",
            null, deletionReceipt, cancellationToken);

    public Task<AuthCompletion> AccountDeletionOrganizationCompletedAsync(
        string deletionReceipt,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendForAccountDeletionAsync(HttpMethod.Get, $"/v1/terrascale/account/deletion?organization={Id(organizationId)}",
            null, deletionReceipt, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformProject>> CreateProjectAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformName request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/projects",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProject>>> ListProjectsAsync(
        string accessToken,
        string organizationId,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformProject,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformProject>> GetProjectAsync(
        string accessToken,
        string organizationId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformProject>> RenameProjectAsync(
        string accessToken,
        string organizationId,
        string projectId,
        TerraScaleAuthPlatformName request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Patch, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformProject,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthCompletion> DeleteProjectAsync(
        string accessToken,
        string organizationId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForDeletionAsync($"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}",
            TerraScaleAuthCredentials.Bearer(accessToken), cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>>> ListProjectMembersAsync(
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

    public Task<AuthResult<TerraScaleAuthPlatformProjectMember>> SetProjectMemberRoleAsync(
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

    public Task<AuthOutcome> RemoveProjectMemberAsync(
        string accessToken,
        string organizationId,
        string projectId,
        string userId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/members/{Id(userId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformApiKeySecret>> CreateOrganizationApiKeyAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformCreateApiKey request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformApiKey>>> ListOrganizationApiKeysAsync(
        string accessToken,
        string organizationId,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys" + PageQuery(limit, after),
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformPageTerraScaleAuthPlatformApiKey,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformApiKey>> GetOrganizationApiKeyAsync(
        string accessToken,
        string organizationId,
        string keyId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys/{Id(keyId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthOutcome> RevokeOrganizationApiKeyAsync(
        string accessToken,
        string organizationId,
        string keyId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/api-keys/{Id(keyId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformApiKeySecret>> RotateOrganizationApiKeyAsync(
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

    public Task<AuthResult<TerraScaleAuthPlatformApiKeySecret>> CreateProjectApiKeyAsync(
        string accessToken,
        string organizationId,
        string projectId,
        TerraScaleAuthPlatformCreateApiKey request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateApiKey,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKeySecret,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformApiKey>>> ListProjectApiKeysAsync(
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

    public Task<AuthResult<TerraScaleAuthPlatformApiKey>> GetProjectApiKeyAsync(
        string accessToken,
        string organizationId,
        string projectId,
        string keyId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformApiKey,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthOutcome> RevokeProjectApiKeyAsync(
        string accessToken,
        string organizationId,
        string projectId,
        string keyId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformApiKeySecret>> RotateProjectApiKeyAsync(
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

    public Task<AuthResult<TerraScaleAuthPlatformCreatedInvitation>> CreateInvitationAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformCreateInvitation request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/invitations",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreateInvitation,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformCreatedInvitation,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformInvitation>>> ListInvitationsAsync(
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

    public Task<AuthOutcome> RevokeInvitationAsync(
        string accessToken,
        string organizationId,
        string invitationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/invitations/{Id(invitationId)}",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformInvitationLookup>> LookupInvitationAsync(
        string accessToken,
        TerraScaleAuthPlatformInvitationToken request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/invitations/lookup",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationLookup,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformAcceptedInvitation>> AcceptInvitationAsync(
        string accessToken,
        TerraScaleAuthPlatformInvitationToken request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformAcceptedInvitation,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthOutcome> DeclineInvitationAsync(
        string accessToken,
        TerraScaleAuthPlatformInvitationToken request,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/invitations/decline",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformInvitationToken,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformOwnershipTransfer>> StartOwnershipTransferAsync(
        string accessToken,
        string organizationId,
        TerraScaleAuthPlatformStartOwnershipTransfer request,
        string stepUpProof,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer",
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformStartOwnershipTransfer,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOwnershipTransfer,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken, Proof(stepUpProof));

    public Task<AuthResult<TerraScaleAuthPlatformOwnershipTransfer>> GetOwnershipTransferAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformOwnershipTransfer,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthOutcome> CancelOwnershipTransferAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformMember>> ConfirmOwnershipTransferAsync(
        string accessToken,
        string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer/confirm",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformMember,
            TerraScaleAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformAuditEvent>>> ListAuditEventsAsync(
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
