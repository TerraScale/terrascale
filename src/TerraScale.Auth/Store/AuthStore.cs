namespace TerraScale.Auth.Store;

using TerraScale.DotNet;

/// <summary>
/// All ts-auth persistence, over the public TerraScale document API (the same
/// surface an end user builds on). Documents are JSON records in one database;
/// uniqueness rides on TerraBase unique indexes over synthetic key fields.
/// </summary>
public sealed class AuthStore(TerraScaleClient client, string database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Database { get; } = database;

    // ---- Documents API plumbing -------------------------------------------------

    private async Task<T?> GetOrNullAsync<T>(string collection, string id, CancellationToken ct) where T : class
    {
        try
        {
            var document = await client.Documents.GetAsync(Database, collection, id, cancellationToken: ct);
            return document.Value.Deserialize<T>(Json);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<T> CreateAsync<T>(string collection, string id, T entity, CancellationToken ct) where T : class
    {
        await client.Documents.PutAsync(Database, collection, id, entity!, options: new TerraScaleDocumentMutationOptions(CreateOnly: true), cancellationToken: ct);
        return entity;
    }

    private Task UpsertAsync<T>(string collection, string id, T entity, CancellationToken ct) where T : class =>
        client.Documents.PutAsync(Database, collection, id, entity!, options: new TerraScaleDocumentMutationOptions(), cancellationToken: ct);

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string collection, string field, string equalValue, int limit, CancellationToken ct) where T : class
    {
        var result = await client.Documents.QueryAsync(
            Database,
            collection,
            new TerraScaleDocumentQuery(
                Predicates: [new TerraScaleDocumentPredicate(field, TerraScaleQueryOperator.Equal, JsonSerializer.SerializeToElement(equalValue))],
                Projection: null,
                Limit: limit,
                After: null),
            cancellationToken: ct);
        return result.Documents.Select(row => row.Value.Deserialize<T>(Json)).OfType<T>().ToList();
    }

    // ---- Instances ----------------------------------------------------------------

    public Task<Instance> CreateInstanceAsync(Instance instance, CancellationToken ct = default) =>
        CreateAsync("instances", instance.Id, instance, ct);

    public Task<Instance?> GetInstanceAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<Instance>("instances", id, ct);

    public async Task<Instance?> FindInstanceBySlugAsync(string slug, CancellationToken ct = default)
    {
        var matches = await QueryAsync<Instance>("instances", "slug", slug, 2, ct);
        return matches.FirstOrDefault();
    }

    // ---- Users --------------------------------------------------------------------

    public Task<User> CreateUserAsync(User user, CancellationToken ct = default) =>
        CreateAsync("users", user.Id, user, ct);

    public Task<User?> GetUserAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<User>("users", id, ct);

    public Task UpsertUserAsync(User user, CancellationToken ct = default) =>
        UpsertAsync("users", user.Id, user, ct);

    public async Task<User?> FindUserByEmailAsync(string instanceId, string normalizedEmail, CancellationToken ct = default)
    {
        var matches = await QueryAsync<User>("users", "emailKey", $"{instanceId}:{normalizedEmail}", 2, ct);
        return matches.FirstOrDefault();
    }

    public async Task<IReadOnlyList<User>> ListUsersAsync(string instanceId, int limit, string? after, CancellationToken ct = default)
    {
        var page = await client.Documents.ListAsync(Database, "users", limit, after, ct);
        return page.Items.Select(item => item.Value.Deserialize<User>(Json)).OfType<User>().Where(u => u.InstanceId == instanceId).ToList();
    }

    // ---- Passkeys -----------------------------------------------------------------

    public Task<Passkey> CreatePasskeyAsync(Passkey passkey, CancellationToken ct = default) =>
        CreateAsync("passkeys", passkey.Id, passkey, ct);

    public Task<Passkey?> GetPasskeyAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<Passkey>("passkeys", id, ct);

    public Task UpsertPasskeyAsync(Passkey passkey, CancellationToken ct = default) =>
        UpsertAsync("passkeys", passkey.Id, passkey, ct);

    public async Task<Passkey?> FindPasskeyByCredentialIdAsync(string credentialKey, CancellationToken ct = default)
    {
        var matches = await QueryAsync<Passkey>("passkeys", "credentialKey", credentialKey, 2, ct);
        return matches.FirstOrDefault();
    }

    public Task<IReadOnlyList<Passkey>> ListPasskeysAsync(string userId, CancellationToken ct = default) =>
        QueryAsync<Passkey>("passkeys", "userId", userId, 100, ct);

    public Task DeletePasskeyAsync(string id, CancellationToken ct = default) =>
        client.Documents.DeleteAsync(Database, "passkeys", id, ct);

    // ---- Sessions -----------------------------------------------------------------

    public Task<Session> CreateSessionAsync(Session session, CancellationToken ct = default) =>
        CreateAsync("sessions", session.Id, session, ct);

    public Task<Session?> GetSessionAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<Session>("sessions", id, ct);

    public Task UpsertSessionAsync(Session session, CancellationToken ct = default) =>
        UpsertAsync("sessions", session.Id, session, ct);

    public async Task<Session?> FindSessionByTokenAsync(string tokenKey, CancellationToken ct = default)
    {
        var matches = await QueryAsync<Session>("sessions", "tokenKey", tokenKey, 2, ct);
        return matches.FirstOrDefault();
    }

    public Task<IReadOnlyList<Session>> ListSessionsByUserAsync(string userId, CancellationToken ct = default) =>
        QueryAsync<Session>("sessions", "userId", userId, 100, ct);

    // ---- OAuth clients --------------------------------------------------------------

    public Task<OAuthClient> CreateOAuthClientAsync(OAuthClient oauthClient, CancellationToken ct = default) =>
        CreateAsync("oauth_clients", oauthClient.Id, oauthClient, ct);

    public Task<OAuthClient?> GetOAuthClientAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<OAuthClient>("oauth_clients", id, ct);

    public Task UpsertOAuthClientAsync(OAuthClient oauthClient, CancellationToken ct = default) =>
        UpsertAsync("oauth_clients", oauthClient.Id, oauthClient, ct);

    public async Task<OAuthClient?> FindOAuthClientAsync(string instanceId, string clientId, CancellationToken ct = default)
    {
        var matches = await QueryAsync<OAuthClient>("oauth_clients", "clientKey", $"{instanceId}:{clientId}", 2, ct);
        return matches.FirstOrDefault();
    }

    // ---- Authorization codes ---------------------------------------------------------

    public Task<AuthorizationCode> CreateAuthorizationCodeAsync(AuthorizationCode code, CancellationToken ct = default) =>
        CreateAsync("oauth_codes", code.Id, code, ct);

    public Task<AuthorizationCode?> GetAuthorizationCodeAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<AuthorizationCode>("oauth_codes", id, ct);

    public Task UpsertAuthorizationCodeAsync(AuthorizationCode code, CancellationToken ct = default) =>
        UpsertAsync("oauth_codes", code.Id, code, ct);

    public async Task<AuthorizationCode?> FindAuthorizationCodeAsync(string codeKey, CancellationToken ct = default)
    {
        var matches = await QueryAsync<AuthorizationCode>("oauth_codes", "codeKey", codeKey, 2, ct);
        return matches.FirstOrDefault();
    }

    // ---- Refresh tokens ----------------------------------------------------------------

    public Task<RefreshToken> CreateRefreshTokenAsync(RefreshToken token, CancellationToken ct = default) =>
        CreateAsync("refresh_tokens", token.Id, token, ct);

    public Task<RefreshToken?> GetRefreshTokenAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<RefreshToken>("refresh_tokens", id, ct);

    public Task UpsertRefreshTokenAsync(RefreshToken token, CancellationToken ct = default) =>
        UpsertAsync("refresh_tokens", token.Id, token, ct);

    public async Task<RefreshToken?> FindRefreshTokenAsync(string tokenKey, CancellationToken ct = default)
    {
        var matches = await QueryAsync<RefreshToken>("refresh_tokens", "tokenKey", tokenKey, 2, ct);
        return matches.FirstOrDefault();
    }

    public Task<IReadOnlyList<RefreshToken>> ListRefreshTokensByUserAsync(string userId, CancellationToken ct = default) =>
        QueryAsync<RefreshToken>("refresh_tokens", "userId", userId, 100, ct);

    // ---- API keys -------------------------------------------------------------------

    public Task<ApiKey> CreateApiKeyAsync(ApiKey apiKey, CancellationToken ct = default) =>
        CreateAsync("api_keys", apiKey.Id, apiKey, ct);

    public Task<ApiKey?> GetApiKeyAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<ApiKey>("api_keys", id, ct);

    public Task UpsertApiKeyAsync(ApiKey apiKey, CancellationToken ct = default) =>
        UpsertAsync("api_keys", apiKey.Id, apiKey, ct);

    public async Task<ApiKey?> FindApiKeyAsync(string secretKey, CancellationToken ct = default)
    {
        var matches = await QueryAsync<ApiKey>("api_keys", "secretKey", secretKey, 2, ct);
        return matches.FirstOrDefault();
    }

    public Task<IReadOnlyList<ApiKey>> ListApiKeysAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<ApiKey>("api_keys", "instanceId", instanceId, 100, ct);

    // ---- MFA factors ------------------------------------------------------------------

    public Task<MfaFactor> CreateMfaFactorAsync(MfaFactor factor, CancellationToken ct = default) =>
        CreateAsync("mfa_factors", factor.Id, factor, ct);

    public Task<MfaFactor?> GetMfaFactorAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<MfaFactor>("mfa_factors", id, ct);

    public Task UpsertMfaFactorAsync(MfaFactor factor, CancellationToken ct = default) =>
        UpsertAsync("mfa_factors", factor.Id, factor, ct);

    public Task DeleteMfaFactorAsync(string id, CancellationToken ct = default) =>
        client.Documents.DeleteAsync(Database, "mfa_factors", id, ct);

    public Task<IReadOnlyList<MfaFactor>> ListMfaFactorsAsync(string userId, CancellationToken ct = default) =>
        QueryAsync<MfaFactor>("mfa_factors", "userId", userId, 20, ct);

    // ---- Organizations ------------------------------------------------------------------

    public Task<Organization> CreateOrganizationAsync(Organization organization, CancellationToken ct = default) =>
        CreateAsync("organizations", organization.Id, organization, ct);

    public Task<Organization?> GetOrganizationAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<Organization>("organizations", id, ct);

    public Task UpsertOrganizationAsync(Organization organization, CancellationToken ct = default) =>
        UpsertAsync("organizations", organization.Id, organization, ct);

    public Task<IReadOnlyList<Organization>> ListOrganizationsAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<Organization>("organizations", "instanceId", instanceId, 100, ct);

    // ---- Memberships -----------------------------------------------------------------

    public Task<OrgMembership> CreateMembershipAsync(OrgMembership membership, CancellationToken ct = default) =>
        CreateAsync("memberships", membership.Id, membership, ct);

    public Task<OrgMembership?> GetMembershipAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<OrgMembership>("memberships", id, ct);

    public Task DeleteMembershipAsync(string id, CancellationToken ct = default) =>
        client.Documents.DeleteAsync(Database, "memberships", id, ct);

    public Task<IReadOnlyList<OrgMembership>> ListMembershipsByUserAsync(string userId, CancellationToken ct = default) =>
        QueryAsync<OrgMembership>("memberships", "userId", userId, 100, ct);

    public Task<IReadOnlyList<OrgMembership>> ListMembershipsByOrgAsync(string orgId, CancellationToken ct = default) =>
        QueryAsync<OrgMembership>("memberships", "orgId", orgId, 100, ct);

    // ---- Invitations -------------------------------------------------------------------

    public Task<Invitation> CreateInvitationAsync(Invitation invitation, CancellationToken ct = default) =>
        CreateAsync("invitations", invitation.Id, invitation, ct);

    public Task<Invitation?> GetInvitationAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<Invitation>("invitations", id, ct);

    public Task UpsertInvitationAsync(Invitation invitation, CancellationToken ct = default) =>
        UpsertAsync("invitations", invitation.Id, invitation, ct);

    public Task<IReadOnlyList<Invitation>> ListInvitationsAsync(string orgId, CancellationToken ct = default) =>
        QueryAsync<Invitation>("invitations", "orgId", orgId, 100, ct);

    // ---- Verification tokens -------------------------------------------------------------

    public Task<VerificationToken> CreateVerificationTokenAsync(VerificationToken token, CancellationToken ct = default) =>
        CreateAsync("verification_tokens", token.Id, token, ct);

    public Task<VerificationToken?> GetVerificationTokenAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<VerificationToken>("verification_tokens", id, ct);

    public Task UpsertVerificationTokenAsync(VerificationToken token, CancellationToken ct = default) =>
        UpsertAsync("verification_tokens", token.Id, token, ct);

    public async Task<VerificationToken?> FindVerificationTokenAsync(string tokenKey, CancellationToken ct = default)
    {
        var matches = await QueryAsync<VerificationToken>("verification_tokens", "tokenKey", tokenKey, 2, ct);
        return matches.FirstOrDefault();
    }

    public Task<IReadOnlyList<VerificationToken>> ListVerificationTokensAsync(string instanceId, string purpose, int limit, CancellationToken ct = default) =>
        QueryAsync<VerificationToken>("verification_tokens", "purpose", purpose, limit, ct);

    // ---- Webhooks -------------------------------------------------------------------------

    public Task<WebhookEndpoint> CreateWebhookEndpointAsync(WebhookEndpoint endpoint, CancellationToken ct = default) =>
        CreateAsync("webhooks", endpoint.Id, endpoint, ct);

    public Task<WebhookEndpoint?> GetWebhookEndpointAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<WebhookEndpoint>("webhooks", id, ct);

    public Task UpsertWebhookEndpointAsync(WebhookEndpoint endpoint, CancellationToken ct = default) =>
        UpsertAsync("webhooks", endpoint.Id, endpoint, ct);

    public Task DeleteWebhookEndpointAsync(string id, CancellationToken ct = default) =>
        client.Documents.DeleteAsync(Database, "webhooks", id, ct);

    public Task<IReadOnlyList<WebhookEndpoint>> ListWebhookEndpointsAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<WebhookEndpoint>("webhooks", "instanceId", instanceId, 100, ct);

    // ---- Audit events ----------------------------------------------------------------------

    public Task<AuditEvent> CreateAuditEventAsync(AuditEvent auditEvent, CancellationToken ct = default) =>
        CreateAsync("audit_events", auditEvent.Id, auditEvent, ct);

    public Task<IReadOnlyList<AuditEvent>> ListAuditEventsAsync(string instanceId, int limit, CancellationToken ct = default) =>
        QueryAsync<AuditEvent>("audit_events", "instanceId", instanceId, limit, ct);

    /// <summary>Audit events for the instance at or after a unix-seconds boundary (HLC-order caveat: ties on CreatedAt).</summary>
    public async Task<IReadOnlyList<AuditEvent>> ListAuditEventsSinceAsync(string instanceId, long sinceUnixSeconds, int limit, CancellationToken ct = default)
    {
        var recent = await QueryAsync<AuditEvent>("audit_events", "instanceId", instanceId, limit, ct);
        return recent.Where(e => e.CreatedAt >= sinceUnixSeconds).OrderBy(e => e.CreatedAt).ToList();
    }

    // ---- Signing keys -----------------------------------------------------------------------

    public Task<SigningKeyRecord> CreateSigningKeyAsync(SigningKeyRecord key, CancellationToken ct = default) =>
        CreateAsync("signing_keys", key.Id, key, ct);

    public Task UpsertSigningKeyAsync(SigningKeyRecord key, CancellationToken ct = default) =>
        UpsertAsync("signing_keys", key.Id, key, ct);

    public Task<IReadOnlyList<SigningKeyRecord>> ListSigningKeysAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<SigningKeyRecord>("signing_keys", "instanceId", instanceId, 50, ct);
}
