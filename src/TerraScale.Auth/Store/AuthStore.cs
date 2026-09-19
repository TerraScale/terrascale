namespace TerraScale.Auth.Store;

using System.Globalization;
using TerraScale.Auth.Crypto;
using TerraScale.DotNet;

/// <summary>
/// All ts-auth persistence, over the public TerraScale document API (the same
/// surface an end user builds on). Documents are JSON records in one database.
/// The engine has no unique secondary indexes yet, so uniqueness rides on
/// atomic claim documents: a claim is a CreateOnly document write, which the
/// engine rejects on collision — race-safe without transactions.
/// </summary>
public sealed class AuthStore(TerraScaleClient client, string database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Database { get; } = database;

    // ---- Atomic uniqueness claims ---------------------------------------------------

    /// <summary>Atomically claims a unique key; throws <see cref="TerraScaleApiException"/> (conflict) when taken.</summary>
    public Task ClaimUniqueAsync(string kind, string key, string ownerId, CancellationToken ct = default) =>
        client.Documents.PutAsync(
            Database,
            "uniques",
            UniqueClaimId(kind, key),
            new UniqueClaim(OwnerType: kind, OwnerId: ownerId),
            new TerraScaleDocumentMutationOptions(CreateOnly: true),
            ct);

    /// <summary>Releases a unique key claim; missing claims are ignored.</summary>
    public async Task ReleaseUniqueAsync(string kind, string key, CancellationToken ct = default)
    {
        try
        {
            await client.Documents.DeleteAsync(Database, "uniques", UniqueClaimId(kind, key), ct);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    private static string UniqueClaimId(string kind, string key) => $"{kind}:{SecureTokens.Sha256Hex(key)}";

    private sealed record UniqueClaim(string OwnerType, string OwnerId);

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

    /// <summary>Deletes a document; a concurrent delete of the same id is a no-op, not a 500.</summary>
    private async Task DeleteIgnoringMissingAsync(string collection, string id, CancellationToken ct)
    {
        try
        {
            await client.Documents.DeleteAsync(Database, collection, id, ct);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

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

    public async Task<Instance> CreateInstanceAsync(Instance instance, CancellationToken ct = default)
    {
        await ClaimUniqueAsync("instance-slug", instance.Slug, instance.Id, ct);
        try
        {
            return await CreateAsync("instances", instance.Id, instance, ct);
        }
        catch
        {
            await ReleaseUniqueAsync("instance-slug", instance.Slug, ct);
            throw;
        }
    }

    public Task<Instance?> GetInstanceAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<Instance>("instances", id, ct);

    public Task UpsertInstanceAsync(Instance instance, CancellationToken ct = default) =>
        UpsertAsync("instances", instance.Id, instance, ct);

    public async Task<Instance?> FindInstanceBySlugAsync(string slug, CancellationToken ct = default)
    {
        var matches = await QueryAsync<Instance>("instances", "slug", slug, 2, ct);
        return matches.FirstOrDefault();
    }

    // ---- Users --------------------------------------------------------------------

    public async Task<User> CreateUserAsync(User user, CancellationToken ct = default)
    {
        // Random high-entropy keys (sessions, codes, api keys) tolerate check-then-act;
        // user-chosen identifiers (email, username) need the atomic claim. Claims are
        // taken inside the try and only released once actually held — a conflicting
        // later claim must not strand the earlier one, and a failed claim must not
        // release a key owned by someone else.
        var emailClaimed = false;
        var usernameClaimed = false;
        try
        {
            if (user.EmailKey is not null)
            {
                await ClaimUniqueAsync("user-email", user.EmailKey, user.Id, ct);
                emailClaimed = true;
            }
            if (user.UsernameKey is not null)
            {
                await ClaimUniqueAsync("user-username", user.UsernameKey, user.Id, ct);
                usernameClaimed = true;
            }
            return await CreateAsync("users", user.Id, user, ct);
        }
        catch
        {
            if (emailClaimed)
                await ReleaseUniqueAsync("user-email", user.EmailKey!, ct);
            if (usernameClaimed)
                await ReleaseUniqueAsync("user-username", user.UsernameKey!, ct);
            throw;
        }
    }

    public Task<User?> GetUserAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<User>("users", id, ct);

    public Task UpsertUserAsync(User user, CancellationToken ct = default) =>
        UpsertAsync("users", user.Id, user, ct);

    public async Task<User?> FindUserByEmailAsync(string instanceId, string normalizedEmail, CancellationToken ct = default)
    {
        var matches = await QueryAsync<User>("users", "emailKey", $"{instanceId}:{normalizedEmail}", 2, ct);
        return matches.FirstOrDefault();
    }

    /// <summary>
    /// One raw engine page of the shared users collection filtered to one instance,
    /// plus the cursor the walk must continue from: the RAW page's last document id,
    /// not the last match — a page holding zero of this instance's users must not
    /// end the scan while more documents follow.
    /// </summary>
    public async Task<(IReadOnlyList<User> Users, string? NextAfter)> ListUsersAsync(string instanceId, int limit, string? after, CancellationToken ct = default)
    {
        var page = await client.Documents.ListAsync(Database, "users", limit, after, ct);
        var users = page.Items
            .Select(item => item.Value.Deserialize<User>(Json))
            .OfType<User>()
            .Where(u => u.InstanceId == instanceId)
            .ToList();
        return (users, page.Items.Count == 0 ? null : page.Items[^1].Id);
    }

    // ---- Passkeys -----------------------------------------------------------------

    public async Task<Passkey> CreatePasskeyAsync(Passkey passkey, CancellationToken ct = default)
    {
        await ClaimUniqueAsync("passkey", passkey.CredentialKey, passkey.UserId, ct);
        try
        {
            return await CreateAsync("passkeys", passkey.Id, passkey, ct);
        }
        catch
        {
            await ReleaseUniqueAsync("passkey", passkey.CredentialKey, ct);
            throw;
        }
    }

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

    public async Task DeletePasskeyAsync(string id, CancellationToken ct = default)
    {
        var passkey = await GetPasskeyAsync(id, ct);
        await client.Documents.DeleteAsync(Database, "passkeys", id, ct);
        if (passkey is not null)
            await ReleaseUniqueAsync("passkey", passkey.CredentialKey, ct);
    }

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

    public async Task<OAuthClient> CreateOAuthClientAsync(OAuthClient oauthClient, CancellationToken ct = default)
    {
        await ClaimUniqueAsync("oauth-client", oauthClient.ClientKey, oauthClient.Id, ct);
        try
        {
            return await CreateAsync("oauth_clients", oauthClient.Id, oauthClient, ct);
        }
        catch
        {
            await ReleaseUniqueAsync("oauth-client", oauthClient.ClientKey, ct);
            throw;
        }
    }

    public async Task DeleteOAuthClientAsync(string id, CancellationToken ct = default)
    {
        var oauthClient = await GetOAuthClientAsync(id, ct);
        await client.Documents.DeleteAsync(Database, "oauth_clients", id, ct);
        if (oauthClient is not null)
            await ReleaseUniqueAsync("oauth-client", oauthClient.ClientKey, ct);
    }

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

    public async Task<Organization> CreateOrganizationAsync(Organization organization, CancellationToken ct = default)
    {
        await ClaimUniqueAsync("org-slug", organization.SlugKey, organization.Id, ct);
        try
        {
            return await CreateAsync("organizations", organization.Id, organization, ct);
        }
        catch
        {
            await ReleaseUniqueAsync("org-slug", organization.SlugKey, ct);
            throw;
        }
    }

    public async Task DeleteOrganizationAsync(string id, CancellationToken ct = default)
    {
        var organization = await GetOrganizationAsync(id, ct);
        await DeleteIgnoringMissingAsync("organizations", id, ct);
        if (organization is not null)
            await ReleaseUniqueAsync("org-slug", organization.SlugKey, ct);
    }

    public Task<Organization?> GetOrganizationAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<Organization>("organizations", id, ct);

    public Task UpsertOrganizationAsync(Organization organization, CancellationToken ct = default) =>
        UpsertAsync("organizations", organization.Id, organization, ct);

    public Task<IReadOnlyList<Organization>> ListOrganizationsAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<Organization>("organizations", "instanceId", instanceId, 100, ct);

    // ---- Memberships -----------------------------------------------------------------

    public async Task<OrgMembership> CreateMembershipAsync(OrgMembership membership, CancellationToken ct = default)
    {
        await ClaimUniqueAsync("membership", membership.MemberKey, membership.Id, ct);
        try
        {
            return await CreateAsync("memberships", membership.Id, membership, ct);
        }
        catch
        {
            await ReleaseUniqueAsync("membership", membership.MemberKey, ct);
            throw;
        }
    }

    public Task<OrgMembership?> GetMembershipAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<OrgMembership>("memberships", id, ct);

    /// <summary>Updates a membership in one atomic write. The memberKey never changes, so the claim stays held — unlike delete + re-create, there is no window where the membership or its claim is released.</summary>
    public Task UpsertMembershipAsync(OrgMembership membership, CancellationToken ct = default) =>
        UpsertAsync("memberships", membership.Id, membership, ct);

    public async Task DeleteMembershipAsync(string id, CancellationToken ct = default)
    {
        var membership = await GetMembershipAsync(id, ct);
        await DeleteIgnoringMissingAsync("memberships", id, ct);
        if (membership is not null)
            await ReleaseUniqueAsync("membership", membership.MemberKey, ct);
    }

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

    /// <summary>
    /// Atomic invitation status transition (accept: pending→accepted, revoke: pending→revoked).
    /// The write carries an exact-version If-Match, so a concurrent transition of the same
    /// invitation — a double accept, or an accept racing a revoke — loses with 412 and
    /// reads as null.
    /// </summary>
    public async Task<Invitation?> TryTransitionInvitationAsync(string id, string fromStatus, string toStatus, CancellationToken ct = default)
    {
        TerraScaleDocument document;
        try
        {
            document = await client.Documents.GetAsync(Database, "invitations", id, cancellationToken: ct);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (document.Data.Deserialize<Invitation>(Json) is not { } invitation || invitation.Status != fromStatus)
            return null;
        var transitioned = invitation with { Status = toStatus };
        try
        {
            await client.Documents.PutAsync(
                Database,
                "invitations",
                id,
                transitioned,
                new TerraScaleDocumentMutationOptions(ExpectedChangeToken: document.ChangeToken),
                ct);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
        return transitioned;
    }

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

    /// <summary>
    /// Atomic single-use consume: the Consumed write carries an exact-version
    /// If-Match, so of two concurrent consumes of one token only the first
    /// wins (the loser's precondition fails with 412 and reads as invalid).
    /// </summary>
    public async Task<VerificationToken?> ConsumeVerificationTokenAsync(string tokenKey, Func<VerificationToken, bool> eligible, CancellationToken ct = default)
    {
        var result = await client.Documents.QueryAsync(
            Database,
            "verification_tokens",
            new TerraScaleDocumentQuery(
                Predicates: [new TerraScaleDocumentPredicate("tokenKey", TerraScaleQueryOperator.Equal, JsonSerializer.SerializeToElement(tokenKey))],
                Projection: null,
                Limit: 2,
                After: null),
            ct);
        var row = result.Documents.FirstOrDefault();
        if (row is null || row.Value.Deserialize<VerificationToken>(Json) is not { } record || !eligible(record))
            return null;
        try
        {
            await client.Documents.PutAsync(
                Database,
                "verification_tokens",
                record.Id,
                record with { Consumed = true },
                new TerraScaleDocumentMutationOptions(ExpectedChangeToken: row.Version.ToString(CultureInfo.InvariantCulture)),
                ct);
            return record;
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<VerificationToken>> ListVerificationTokensAsync(string instanceId, string purpose, int limit, CancellationToken ct = default) =>
        QueryAsync<VerificationToken>("verification_tokens", "purpose", purpose, limit, ct);

    /// <summary>A user's tokens for one purpose. Per-user accurate: the engine
    /// returns purpose-wide listings id-ordered and bounded, so they cannot answer
    /// "does this user have a recent token" once other users' tokens exist.</summary>
    public async Task<IReadOnlyList<VerificationToken>> ListUserVerificationTokensAsync(string userId, string purpose, CancellationToken ct = default) =>
        (await QueryAsync<VerificationToken>("verification_tokens", "userId", userId, 100, ct))
        .Where(t => t.Purpose == purpose)
        .ToList();

    /// <summary>
    /// Marks a user's outstanding tokens for a purpose as consumed. Used when the email
    /// changes: a verification link proves control of the OLD address and must not be
    /// able to mark the new one verified.
    /// </summary>
    public async Task InvalidateVerificationTokensAsync(string userId, string purpose, CancellationToken ct = default)
    {
        foreach (var token in await QueryAsync<VerificationToken>("verification_tokens", "userId", userId, 100, ct))
            if (token.Purpose == purpose && !token.Consumed)
                await UpsertVerificationTokenAsync(token with { Consumed = true }, ct);
    }

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

    // ---- Connections (social OIDC + SAML SSO) ------------------------------------------------

    public async Task<Connection> CreateConnectionAsync(Connection connection, CancellationToken ct = default) =>
        await CreateAsync("connections", connection.Id, connection, ct);

    public Task<Connection?> GetConnectionAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<Connection>("connections", id, ct);

    public Task UpsertConnectionAsync(Connection connection, CancellationToken ct = default) =>
        UpsertAsync("connections", connection.Id, connection, ct);

    public Task DeleteConnectionAsync(string id, CancellationToken ct = default) =>
        client.Documents.DeleteAsync(Database, "connections", id, ct);

    public Task<IReadOnlyList<Connection>> ListConnectionsAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<Connection>("connections", "instanceId", instanceId, 100, ct);

    // ---- SCIM directories ---------------------------------------------------------------------

    public async Task<ScimDirectory> CreateScimDirectoryAsync(ScimDirectory directory, CancellationToken ct = default)
    {
        await ClaimUniqueAsync("scim-token", directory.TokenKey, directory.Id, ct);
        try
        {
            return await CreateAsync("scim_directories", directory.Id, directory, ct);
        }
        catch
        {
            await ReleaseUniqueAsync("scim-token", directory.TokenKey, ct);
            throw;
        }
    }

    public Task<ScimDirectory?> GetScimDirectoryAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<ScimDirectory>("scim_directories", id, ct);

    public Task DeleteScimDirectoryAsync(string id, CancellationToken ct = default) =>
        client.Documents.DeleteAsync(Database, "scim_directories", id, ct);

    public async Task<ScimDirectory?> FindScimDirectoryByTokenAsync(string tokenKey, CancellationToken ct = default)
    {
        var matches = await QueryAsync<ScimDirectory>("scim_directories", "tokenKey", tokenKey, 2, ct);
        return matches.FirstOrDefault();
    }

    public Task<IReadOnlyList<ScimDirectory>> ListScimDirectoriesAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<ScimDirectory>("scim_directories", "instanceId", instanceId, 100, ct);

    // ---- Subscriptions (Stripe) ------------------------------------------------------------------

    public Task<Subscription> CreateSubscriptionAsync(Subscription subscription, CancellationToken ct = default) =>
        CreateAsync("subscriptions", subscription.Id, subscription, ct);

    public Task<Subscription?> GetSubscriptionAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<Subscription>("subscriptions", id, ct);

    public Task UpsertSubscriptionAsync(Subscription subscription, CancellationToken ct = default) =>
        UpsertAsync("subscriptions", subscription.Id, subscription, ct);

    public async Task<Subscription?> FindSubscriptionByCustomerAsync(string stripeCustomerId, CancellationToken ct = default)
    {
        var matches = await QueryAsync<Subscription>("subscriptions", "stripeCustomerId", stripeCustomerId, 2, ct);
        return matches.FirstOrDefault();
    }

    public Task<IReadOnlyList<Subscription>> ListSubscriptionsAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<Subscription>("subscriptions", "instanceId", instanceId, 100, ct);

    // ---- JWT templates -----------------------------------------------------------------------------

    public Task<JwtTemplate> CreateJwtTemplateAsync(JwtTemplate template, CancellationToken ct = default) =>
        CreateAsync("jwt_templates", template.Id, template, ct);

    public Task<JwtTemplate?> GetJwtTemplateAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<JwtTemplate>("jwt_templates", id, ct);

    public Task UpsertJwtTemplateAsync(JwtTemplate template, CancellationToken ct = default) =>
        UpsertAsync("jwt_templates", template.Id, template, ct);

    public Task DeleteJwtTemplateAsync(string id, CancellationToken ct = default) =>
        client.Documents.DeleteAsync(Database, "jwt_templates", id, ct);

    public Task<IReadOnlyList<JwtTemplate>> ListJwtTemplatesAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<JwtTemplate>("jwt_templates", "instanceId", instanceId, 100, ct);

    // ---- Organization enrollment domains ---------------------------------------------------------------

    public async Task<OrgDomain> CreateOrgDomainAsync(OrgDomain domain, CancellationToken ct = default)
    {
        await ClaimUniqueAsync("org-domain", domain.DomainKey, domain.Id, ct);
        try
        {
            return await CreateAsync("org_domains", domain.Id, domain, ct);
        }
        catch
        {
            await ReleaseUniqueAsync("org-domain", domain.DomainKey, ct);
            throw;
        }
    }

    public Task<OrgDomain?> GetOrgDomainAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<OrgDomain>("org_domains", id, ct);

    public async Task DeleteOrgDomainAsync(string id, CancellationToken ct = default)
    {
        var domain = await GetOrgDomainAsync(id, ct);
        await client.Documents.DeleteAsync(Database, "org_domains", id, ct);
        if (domain is not null)
            await ReleaseUniqueAsync("org-domain", domain.DomainKey, ct);
    }

    public Task<IReadOnlyList<OrgDomain>> ListOrgDomainsAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<OrgDomain>("org_domains", "instanceId", instanceId, 100, ct);

    // ---- Signing keys -----------------------------------------------------------------------

    public Task<SigningKeyRecord> CreateSigningKeyAsync(SigningKeyRecord key, CancellationToken ct = default) =>
        CreateAsync("signing_keys", key.Id, key, ct);

    public Task UpsertSigningKeyAsync(SigningKeyRecord key, CancellationToken ct = default) =>
        UpsertAsync("signing_keys", key.Id, key, ct);

    public Task<IReadOnlyList<SigningKeyRecord>> ListSigningKeysAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<SigningKeyRecord>("signing_keys", "instanceId", instanceId, 50, ct);
}
