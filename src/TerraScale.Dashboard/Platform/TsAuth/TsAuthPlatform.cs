using System.Globalization;
using System.Net;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed partial class TsAuthPlatformClient(TsAuthClient client)
    {
        internal static string Proof(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value.Length > 8192 || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
                throw new ArgumentException("A step-up proof must be at most 8192 characters without whitespace or control characters.", nameof(value));
            return value;
        }

        public Task<TsAuthPlatformOrganization> CreateOrganizationAsync(
            string accessToken,
            TsAuthPlatformName request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/organizations",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformOrganization,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformPage<TsAuthPlatformOrganization>> ListOrganizationsAsync(
            string accessToken,
            int? limit = null,
            string? after = null,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Get, "/v1/terrascale/organizations" + PageQuery(limit, after),
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformPageTsAuthPlatformOrganization,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformOrganization> RenameOrganizationAsync(
            string accessToken,
            string organizationId,
            TsAuthPlatformName request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Patch, $"/v1/terrascale/organizations/{Id(organizationId)}",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformOrganization,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task DeleteOrganizationAsync(
            string accessToken,
            string organizationId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformPage<TsAuthPlatformMember>> ListMembersAsync(
            string accessToken,
            string organizationId,
            int? limit = null,
            string? after = null,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/members" + PageQuery(limit, after),
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformPageTsAuthPlatformMember,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformMember> ChangeMemberRoleAsync(
            string accessToken,
            string organizationId,
            string userId,
            TsAuthPlatformRole request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/members/{Id(userId)}/role",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformRole,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformMember,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task RemoveMemberAsync(
            string accessToken,
            string organizationId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/members/{Id(userId)}",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);
        }

        public Task LeaveOrganizationAsync(
            string accessToken,
            string organizationId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/leave",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);
        }

        public Task SignOutEverywhereAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/account/sign-out",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);
        }

        public Task<bool> DeleteAccountAsync(
            string accessToken,
            string deletionReceipt,
            string deletionAuthorization,
            CancellationToken cancellationToken = default)
        {
            return client.SendForAccountDeletionAsync(HttpMethod.Delete, "/v1/terrascale/account",
                TsAuthCredentials.Bearer(accessToken), deletionReceipt, cancellationToken, DeletionAuthorization(deletionAuthorization));
        }

        public Task<bool> AccountDeletionCompletedAsync(
            string deletionReceipt,
            CancellationToken cancellationToken = default)
        {
            return client.SendForAccountDeletionAsync(HttpMethod.Get, "/v1/terrascale/account/deletion",
            null, deletionReceipt, cancellationToken);
        }

        public Task<bool> AccountDeletionOrganizationCompletedAsync(
            string deletionReceipt,
            string organizationId,
            CancellationToken cancellationToken = default)
        {
            return client.SendForAccountDeletionAsync(HttpMethod.Get, $"/v1/terrascale/account/deletion?organization={Id(organizationId)}",
            null, deletionReceipt, cancellationToken);
        }

        public Task<TsAuthPlatformProject> CreateProjectAsync(
            string accessToken,
            string organizationId,
            TsAuthPlatformName request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/projects",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformProject,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformPage<TsAuthPlatformProject>> ListProjectsAsync(
            string accessToken,
            string organizationId,
            int? limit = null,
            string? after = null,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects" + PageQuery(limit, after),
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformPageTsAuthPlatformProject,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformProject> RenameProjectAsync(
            string accessToken,
            string organizationId,
            string projectId,
            TsAuthPlatformName request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Patch, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformProject,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task<bool> DeleteProjectAsync(
            string accessToken,
            string organizationId,
            string projectId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForDeletionAsync($"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}",
                TsAuthCredentials.Bearer(accessToken), cancellationToken);
        }

        public Task<TsAuthPlatformPage<TsAuthPlatformProjectMember>> ListProjectMembersAsync(
            string accessToken,
            string organizationId,
            string projectId,
            int? limit = null,
            string? after = null,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/members" + PageQuery(limit, after),
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformPageTsAuthPlatformProjectMember,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformProjectMember> SetProjectMemberRoleAsync(
            string accessToken,
            string organizationId,
            string projectId,
            string userId,
            TsAuthPlatformRole request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/members/{Id(userId)}",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformRole,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformProjectMember,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task RemoveProjectMemberAsync(
            string accessToken,
            string organizationId,
            string projectId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/members/{Id(userId)}",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformCreatedInvitation> CreateInvitationAsync(
            string accessToken,
            string organizationId,
            TsAuthPlatformCreateInvitation request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/invitations",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformCreateInvitation,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformCreatedInvitation,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformPage<TsAuthPlatformInvitation>> ListInvitationsAsync(
            string accessToken,
            string organizationId,
            int? limit = null,
            string? after = null,
            string? projectId = null,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/invitations" + PageQuery(limit, after, projectId),
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformPageTsAuthPlatformInvitation,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task RevokeInvitationAsync(
            string accessToken,
            string organizationId,
            string invitationId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/invitations/{Id(invitationId)}",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformInvitationLookup> LookupInvitationAsync(
            string accessToken,
            TsAuthPlatformInvitationToken request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/invitations/lookup",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformInvitationToken,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformInvitationLookup,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformAcceptedInvitation> AcceptInvitationAsync(
            string accessToken,
            TsAuthPlatformInvitationToken request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformInvitationToken,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformAcceptedInvitation,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task DeclineInvitationAsync(
            string accessToken,
            TsAuthPlatformInvitationToken request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Post, "/v1/terrascale/invitations/decline",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformInvitationToken,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformOwnershipTransfer> StartOwnershipTransferAsync(
            string accessToken,
            string organizationId,
            TsAuthPlatformStartOwnershipTransfer request,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformStartOwnershipTransfer,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformOwnershipTransfer,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformOwnershipTransfer> GetOwnershipTransferAsync(
            string accessToken,
            string organizationId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformOwnershipTransfer,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task CancelOwnershipTransferAsync(
            string accessToken,
            string organizationId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformMember> ConfirmOwnershipTransferAsync(
            string accessToken,
            string organizationId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/ownership-transfer/confirm",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformMember,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformPage<TsAuthPlatformAuditEvent>> ListAuditEventsAsync(
            string accessToken,
            string organizationId,
            int? limit = null,
            string? after = null,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/audit-events" + PageQuery(limit, after),
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformPageTsAuthPlatformAuditEvent,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        private static string Id(string value)
        {
            return TerraScaleIds.Validate(value);
        }

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

        public Task<TsAuthPlatformApiKeySecret> CreateProjectApiKeyAsync(
            string accessToken,
            string organizationId,
            string projectId,
            TsAuthPlatformCreateApiKey request,
            string stepUpProof,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys",
                request ?? throw new ArgumentNullException(nameof(request)), TsAuthIntegrationJsonContext.Default.TsAuthPlatformCreateApiKey,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformApiKeySecret,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)201, 16 * 1024, cancellationToken, Proof(stepUpProof));
        }

        public Task<TsAuthPlatformPage<TsAuthPlatformApiKey>> ListProjectApiKeysAsync(
            string accessToken,
            string organizationId,
            string projectId,
            int? limit = null,
            string? after = null,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys" + PageQuery(limit, after),
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformPageTsAuthPlatformApiKey,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformApiKey> GetProjectApiKeyAsync(
            string accessToken,
            string organizationId,
            string projectId,
            string keyId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformApiKey,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken);
        }

        public Task RevokeProjectApiKeyAsync(
            string accessToken,
            string organizationId,
            string projectId,
            string keyId,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}",
                null, TsAuthIntegrationJsonContext.Default.TsAuthPlatformName,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)204, 16 * 1024, cancellationToken);
        }

        public Task<TsAuthPlatformApiKeySecret> RotateProjectApiKeyAsync(
            string accessToken,
            string organizationId,
            string projectId,
            string keyId,
            string stepUpProof,
            TsAuthPlatformRotateApiKey? request = null,
            CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/projects/{Id(projectId)}/api-keys/{Id(keyId)}/rotate",
                request, TsAuthIntegrationJsonContext.Default.TsAuthPlatformRotateApiKey,
                TsAuthIntegrationJsonContext.Default.TsAuthPlatformApiKeySecret,
                TsAuthCredentials.Bearer(accessToken), (HttpStatusCode)200, 16 * 1024, cancellationToken, Proof(stepUpProof));
        }
    }
}
