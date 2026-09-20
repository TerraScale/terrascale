namespace TerraScale.Auth.Store;

using System.Globalization;
using System.Security.Cryptography;
using TerraScale.Auth.Crypto;
using TerraScale.DotNet;

/// <summary>
/// All ts-auth persistence, over the public TerraScale document API (the same
/// surface an end user builds on). Documents are JSON records in one database.
/// The engine has no unique secondary indexes yet, so uniqueness rides on
/// atomic claim documents: a claim is a CreateOnly document write, which the
/// engine rejects on collision — race-safe without transactions.
/// </summary>
public sealed class AuthStore(
    TerraScaleClient client,
    string database,
    byte[]? auditSigningKey = null,
    IReadOnlyList<byte[]>? auditVerificationKeys = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly byte[]? signingKey = auditSigningKey?.ToArray();
    private readonly IReadOnlyList<byte[]> verificationKeys = auditVerificationKeys is null
        ? []
        : auditVerificationKeys.Select(static key => key.ToArray()).ToArray();

    public string Database { get; } = database;

    /// <summary>Verifies an event against the active key and the configured rotation overlap.</summary>
    public bool HasAuthenticAuditIntegrity(AuditEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (signingKey is null)
            return false;
        return value.HasAuthenticIntegrity(new[] { signingKey }.Concat(verificationKeys));
    }

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

    private sealed record UniqueClaim(string OwnerType, string OwnerId, string? InstanceId = null, int? Seat = null);

    /// <summary>Reconciles durable quota-seat claims against live users. This is an
    /// operator-safe repair for interrupted user creation or legacy cleanup: only
    /// claims with no matching live owner are removed.</summary>
    public async Task<QuotaSeatReconciliation> ReconcileQuotaSeatsAsync(string instanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var users = new List<User>();
        string? after = null;
        while (true)
        {
            var page = await ListUsersAsync(instanceId, 1000, after, ct);
            users.AddRange(page.Users);
            if (page.NextAfter is null)
                break;
            after = page.NextAfter;
        }

        var ownedSeats = users
            .Where(user => !user.Deleted && TryReadQuotaSeat(user, out _))
            .Select(user => (UserId: user.Id, Seat: ReadQuotaSeat(user)))
            .ToHashSet();
        var claims = await QueryAllAsync<UniqueClaim>(
            "uniques",
            [Eq("ownerType", "quota-seat"), Eq("instanceId", instanceId)],
            1000,
            ct);

        var removed = 0;
        foreach (var claim in claims)
        {
            if (claim.Seat is not int seat || ownedSeats.Contains((claim.OwnerId, seat)))
                continue;

            await ReleaseUniqueAsync("quota-seat", $"{instanceId}:{seat}", ct);
            removed++;
        }

        return new QuotaSeatReconciliation(claims.Count, removed, claims.Count - removed);
    }

    private static bool TryReadQuotaSeat(User user, out int seat)
    {
        seat = ReadQuotaSeat(user);
        return seat >= 0;
    }

    private static int ReadQuotaSeat(User user)
    {
        if (!user.PrivateMetadata.TryGetValue("quotaSeat", out var value))
            return -1;
        return value switch
        {
            int number => number,
            long number => checked((int)number),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) => number,
            _ => -1,
        };
    }

    public sealed record QuotaSeatReconciliation(int ClaimsExamined, int ClaimsRemoved, int ClaimsRetained);

    /// <summary>
    /// Claims one of the instance's bounded user seats with a create-only write.
    /// Concurrent creators converge on different seats; a caller must release the
    /// returned seat if its user write fails.
    /// </summary>
    public async Task<int> ClaimQuotaSeatAsync(string instanceId, int quota, string ownerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quota);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        for (var seat = 0; seat < quota; seat++)
        {
            try
            {
                await client.Documents.PutAsync(
                    Database,
                    "uniques",
                    UniqueClaimId("quota-seat", $"{instanceId}:{seat}"),
                    new UniqueClaim("quota-seat", ownerId, instanceId, seat),
                    new TerraScaleDocumentMutationOptions(CreateOnly: true),
                    ct);
                return seat;
            }
            catch (TerraScaleApiException exception) when (exception.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Another creator owns this seat; try the next atomically.
            }
        }
        throw new Billing.QuotaExceededException();
    }

    public Task ReleaseQuotaSeatAsync(string instanceId, int seat, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(seat);
        return ReleaseUniqueAsync("quota-seat", $"{instanceId}:{seat}", ct);
    }

    public async Task ReleaseQuotaSeatAsync(User user, CancellationToken ct = default)
    {
        if (!user.PrivateMetadata.TryGetValue("quotaSeat", out var value))
            return;
        var seat = value switch
        {
            int number => number,
            long number => checked((int)number),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) => number,
            _ => -1,
        };
        if (seat >= 0)
            await ReleaseQuotaSeatAsync(user.InstanceId, seat, ct);
    }

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

    private static TerraScaleDocumentPredicate Eq(string field, string value) =>
        new(field, TerraScaleQueryOperator.Equal, JsonSerializer.SerializeToElement(value));

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string collection, string field, string equalValue, int limit, CancellationToken ct) where T : class =>
        await QueryAsync<T>(collection, [Eq(field, equalValue)], limit, ct);

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string collection, TerraScaleDocumentPredicate[] predicates, int limit, CancellationToken ct) where T : class
    {
        var result = await client.Documents.QueryAsync(
            Database,
            collection,
            new TerraScaleDocumentQuery(Predicates: predicates, Projection: null, Limit: limit, After: null),
            cancellationToken: ct);
        return result.Documents.Select(row => row.Value.Deserialize<T>(Json)).OfType<T>().ToList();
    }

    /// <summary>
    /// Every match for the predicates, walking engine pages by the opaque query cursor
    /// to the end. A bounded page is an arbitrary subset, not "the first N" in any
    /// caller-meaningful order: ids are random tokens, so id-order is not time-order,
    /// and any listing a caller filters, orders, or takes "the latest" from must see
    /// the complete matching set first.
    /// </summary>
    private async Task<IReadOnlyList<T>> QueryAllAsync<T>(string collection, TerraScaleDocumentPredicate[] predicates, int pageSize, CancellationToken ct) where T : class
    {
        var matches = new List<T>();
        string? after = null;
        while (true)
        {
            var page = await client.Documents.QueryAsync(
                Database,
                collection,
                new TerraScaleDocumentQuery(Predicates: predicates, Projection: null, Limit: pageSize, After: after),
                cancellationToken: ct);
            matches.AddRange(page.Documents.Select(row => row.Value.Deserialize<T>(Json)).OfType<T>());
            if (!page.HasMore || page.NextAfter is null)
                return matches;
            after = page.NextAfter;
        }
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
        await DeleteIgnoringMissingAsync("passkeys", id, ct);
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

    /// <summary>
    /// Updates activity only while the session is still active, using the exact
    /// document version observed by the caller. A concurrent revoke therefore
    /// wins instead of being overwritten by a stale activity write.
    /// </summary>
    public async Task<bool> TryTouchSessionAsync(string id, string instanceId, long lastActiveAt, CancellationToken ct = default)
    {
        TerraScaleDocument document;
        try
        {
            document = await client.Documents.GetAsync(Database, "sessions", id, cancellationToken: ct);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        if (document.Data.Deserialize<Session>(Json) is not { } session
            || session.InstanceId != instanceId
            || session.Status != "active")
            return false;

        try
        {
            await client.Documents.PutAsync(
                Database,
                "sessions",
                id,
                session with { LastActiveAt = lastActiveAt },
                new TerraScaleDocumentMutationOptions(ExpectedChangeToken: document.ChangeToken),
                ct);
            return true;
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<Session?> FindSessionByTokenAsync(string tokenKey, CancellationToken ct = default)
    {
        var matches = await QueryAsync<Session>("sessions", "tokenKey", tokenKey, 2, ct);
        return matches.FirstOrDefault();
    }

    public Task<IReadOnlyList<Session>> ListSessionsByUserAsync(string userId, CancellationToken ct = default) =>
        QueryAllAsync<Session>("sessions", [Eq("userId", userId)], 100, ct);

    /// <summary>Deletes matching session, OAuth grant, MFA, and verification-token records for one user.
    /// Audit events are intentionally retained as the accountability record.</summary>
    public async Task<UserAuthenticationCleanupResult> DeleteUserAuthenticationArtifactsAsync(string instanceId, string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var sessions = await QueryAllAsync<Session>("sessions", [Eq("instanceId", instanceId), Eq("userId", userId)], 100, ct);
        var authorizationCodes = await QueryAllAsync<AuthorizationCode>("oauth_codes", [Eq("instanceId", instanceId), Eq("userId", userId)], 100, ct);
        var refreshTokens = await QueryAllAsync<RefreshToken>("refresh_tokens", [Eq("instanceId", instanceId), Eq("userId", userId)], 100, ct);
        var factors = await QueryAllAsync<MfaFactor>("mfa_factors", [Eq("instanceId", instanceId), Eq("userId", userId)], 100, ct);
        var verificationTokens = await QueryAllAsync<VerificationToken>("verification_tokens", [Eq("instanceId", instanceId), Eq("userId", userId)], 100, ct);

        foreach (var session in sessions)
            await DeleteIgnoringMissingAsync("sessions", session.Id, ct);
        foreach (var code in authorizationCodes)
            await DeleteIgnoringMissingAsync("oauth_codes", code.Id, ct);
        foreach (var token in refreshTokens)
            await DeleteIgnoringMissingAsync("refresh_tokens", token.Id, ct);
        foreach (var factor in factors)
            await DeleteIgnoringMissingAsync("mfa_factors", factor.Id, ct);
        foreach (var token in verificationTokens)
            await DeleteIgnoringMissingAsync("verification_tokens", token.Id, ct);

        return new UserAuthenticationCleanupResult(
            sessions.Count,
            authorizationCodes.Count,
            refreshTokens.Count,
            factors.Count,
            verificationTokens.Count);
    }

    public sealed record UserAuthenticationCleanupResult(
        int Sessions,
        int AuthorizationCodes,
        int RefreshTokens,
        int MfaFactors,
        int VerificationTokens);

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
        await DeleteIgnoringMissingAsync("oauth_clients", id, ct);
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
        if (organization is null)
            return;
        // Cascade: everything the org claimed leaves with it. Memberships carry
        // memberKey claims, and enrollment domains carry an instance-wide domainKey
        // claim that would otherwise reject the same domain forever with no owner
        // behind it. The listings are complete walks; the re-query rounds only guard
        // against a membership created concurrently with the delete.
        while ((await ListMembershipsByOrgAsync(id, ct)) is { Count: > 0 } memberships)
            foreach (var membership in memberships)
                await DeleteMembershipAsync(membership.Id, ct);
        while ((await ListOrgDomainsAsync(organization.InstanceId, ct)).Where(d => d.OrgId == id).ToList() is { Count: > 0 } domains)
            foreach (var domain in domains)
                await DeleteOrgDomainAsync(domain.Id, ct);
        await DeleteIgnoringMissingAsync("organizations", id, ct);
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

    /// <summary>All of a user's memberships, walking the engine pages to the end —
    /// deprovisioning must not silently spare the 101st membership.</summary>
    public async Task<IReadOnlyList<OrgMembership>> ListMembershipsByUserAsync(string userId, CancellationToken ct = default)
    {
        var memberships = new List<OrgMembership>();
        string? after = null;
        while (true)
        {
            var page = await client.Documents.QueryAsync(
                Database,
                "memberships",
                new TerraScaleDocumentQuery(
                    Predicates: [new TerraScaleDocumentPredicate("userId", TerraScaleQueryOperator.Equal, JsonSerializer.SerializeToElement(userId))],
                    Projection: null,
                    Limit: 100,
                    After: after),
                cancellationToken: ct);
            memberships.AddRange(page.Documents.Select(row => row.Value.Deserialize<OrgMembership>(Json)).OfType<OrgMembership>());
            if (!page.HasMore || page.NextAfter is null)
                return memberships;
            after = page.NextAfter;
        }
    }

    /// <summary>All of an org's memberships, walking the engine pages to the end —
    /// member-existence checks and the admin-portal admin check must not go blind
    /// past the 100th member.</summary>
    public Task<IReadOnlyList<OrgMembership>> ListMembershipsByOrgAsync(string orgId, CancellationToken ct = default) =>
        QueryAllAsync<OrgMembership>("memberships", [Eq("orgId", orgId)], 100, ct);

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

    /// <summary>The instance's tokens for one purpose, scoped server-side to both
    /// fields and walked to the end. The instanceId must be a predicate, not a
    /// caller-side hope: a purpose-only query crosses instances.</summary>
    public Task<IReadOnlyList<VerificationToken>> ListVerificationTokensAsync(string instanceId, string purpose, int limit, CancellationToken ct = default) =>
        QueryAllAsync<VerificationToken>("verification_tokens", [Eq("instanceId", instanceId), Eq("purpose", purpose)], limit, ct);

    /// <summary>A user's tokens for one purpose, server-filtered on both fields and
    /// walked to the end — the passcode resend throttle must not lose the user's
    /// latest passcode among older tokens of other purposes.</summary>
    public Task<IReadOnlyList<VerificationToken>> ListUserVerificationTokensAsync(string userId, string purpose, CancellationToken ct = default) =>
        QueryAllAsync<VerificationToken>("verification_tokens", [Eq("userId", userId), Eq("purpose", purpose)], 100, ct);

    /// <summary>Atomically increments the durable failure count on the newest live
    /// token for a user and purpose. The compare-and-set retry makes the lockout
    /// counter safe across auth processes and restartable.</summary>
    public async Task<int> RecordVerificationFailureAsync(string instanceId, string userId, string purpose, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var predicates = new[] { Eq("instanceId", instanceId), Eq("userId", userId), Eq("purpose", purpose) };
        for (var attempt = 0; attempt < 8; attempt++)
        {
            TerraScaleDocumentQueryRow? latest = null;
            VerificationToken? latestToken = null;
            string? after = null;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            while (true)
            {
                var page = await client.Documents.QueryAsync(
                    Database,
                    "verification_tokens",
                    new TerraScaleDocumentQuery(Predicates: predicates, Projection: null, Limit: 100, After: after),
                    cancellationToken: ct);
                foreach (var row in page.Documents)
                {
                    if (row.Value.Deserialize<VerificationToken>(Json) is { } token
                        && token.ExpiresAt > now
                        && !token.Consumed
                        && (latestToken is null || token.CreatedAt > latestToken.CreatedAt))
                    {
                        latest = row;
                        latestToken = token;
                    }
                }
                if (!page.HasMore || page.NextAfter is null)
                    break;
                after = page.NextAfter;
            }

            if (latest is null || latestToken is not { } current)
                return 0;
            var next = checked(current.FailedAttempts + 1);
            try
            {
                await client.Documents.PutAsync(
                    Database,
                    "verification_tokens",
                    current.Id,
                    current with { FailedAttempts = next },
                    new TerraScaleDocumentMutationOptions(ExpectedChangeToken: latest.Version.ToString(CultureInfo.InvariantCulture)),
                    ct);
                return next;
            }
            catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Another process advanced the counter; reread and retry.
            }
        }
        throw new HttpRequestException(
            "verification failure counter contention exceeded retry bound",
            inner: null,
            statusCode: System.Net.HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// Marks a user's outstanding tokens for a purpose as consumed. Used when the email
    /// changes: a verification link proves control of the OLD address and must not be
    /// able to mark the new one verified. Walked to the end — invalidation is a
    /// guarantee, not a best effort over one page.
    /// </summary>
    public async Task InvalidateVerificationTokensAsync(string userId, string purpose, CancellationToken ct = default)
    {
        foreach (var token in await ListUserVerificationTokensAsync(userId, purpose, ct))
            if (!token.Consumed)
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

    public Task<WebhookCursor?> GetWebhookCursorAsync(string instanceId, CancellationToken ct = default) =>
        GetOrNullAsync<WebhookCursor>("webhook_cursors", instanceId, ct);

    public Task UpsertWebhookCursorAsync(WebhookCursor cursor, CancellationToken ct = default) =>
        UpsertAsync("webhook_cursors", cursor.Id, cursor, ct);

    public Task<WebhookDelivery?> GetWebhookDeliveryAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<WebhookDelivery>("webhook_deliveries", id, ct);

    public Task UpsertWebhookDeliveryAsync(WebhookDelivery delivery, CancellationToken ct = default) =>
        UpsertAsync("webhook_deliveries", delivery.Id, delivery, ct);

    public Task<IReadOnlyList<WebhookDelivery>> ListWebhookDeliveriesAsync(string instanceId, CancellationToken ct = default) =>
        QueryAllAsync<WebhookDelivery>("webhook_deliveries", [Eq("instanceId", instanceId)], 1000, ct);

    /// <summary>Distinct instance ids that have webhook endpoints, so a restarted
    /// dispatcher can re-register them (its in-memory cursors start empty).</summary>
    public async Task<IReadOnlyList<string>> ListWebhookInstanceIdsAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<string>();
        string? after = null;
        while (true)
        {
            var page = await client.Documents.ListAsync(Database, "webhooks", 100, after, ct);
            if (page.Items.Count == 0)
                return ids.ToArray();
            foreach (var item in page.Items)
                if (item.Value.Deserialize<WebhookEndpoint>(Json) is { } endpoint)
                    ids.Add(endpoint.InstanceId);
            after = page.Items[^1].Id;
        }
    }

    // ---- Audit events ----------------------------------------------------------------------

    public Task<AuditEvent?> GetAuditEventAsync(string id, CancellationToken ct = default) =>
        GetOrNullAsync<AuditEvent>("audit_events", id, ct);

    public Task<AuditEvent> CreateAuditEventAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var sealedEvent = auditEvent with
        {
            IntegrityVersion = 1,
            IntegrityHash = AuditEvent.ComputeIntegrityHash(auditEvent),
            IntegritySignature = signingKey is null
                ? null
                : Convert.ToHexString(HMACSHA256.HashData(
                    signingKey,
                    System.Text.Encoding.UTF8.GetBytes($"v1:{AuditEvent.ComputeIntegrityHash(auditEvent)}"))).ToLowerInvariant(),
            IntegrityKeyFingerprint = signingKey is null
                ? null
                : AuditEvent.ComputeKeyFingerprint(signingKey),
        };
        return CreateAsync("audit_events", sealedEvent.Id, sealedEvent, ct);
    }

    /// <summary>The instance's audit events, newest first, capped at <paramref name="limit"/>.
    /// Ids are random tokens so id-order is not time-order: the complete set is walked,
    /// then ordered by CreatedAt, then truncated — never a bounded id-ordered subset.</summary>
    public async Task<IReadOnlyList<AuditEvent>> ListAuditEventsAsync(string instanceId, int limit, CancellationToken ct = default)
    {
        var events = await QueryAllAsync<AuditEvent>("audit_events", [Eq("instanceId", instanceId)], 1000, ct);
        return events.OrderByDescending(e => e.CreatedAt).Take(limit).ToList();
    }

    /// <summary>Audit events for the instance at or after a unix-seconds boundary, oldest
    /// first. The boundary is a server-side predicate (numbers compare numerically), so
    /// webhook delivery walks only what is past its cursor and still sees every match
    /// (unix-second ties on CreatedAt are the caller's to dedupe).</summary>
    public async Task<IReadOnlyList<AuditEvent>> ListAuditEventsSinceAsync(string instanceId, long sinceUnixSeconds, int limit, CancellationToken ct = default)
    {
        var predicates = new TerraScaleDocumentPredicate[]
        {
            Eq("instanceId", instanceId),
            new("createdAt", TerraScaleQueryOperator.GreaterThanOrEqual, JsonSerializer.SerializeToElement(sinceUnixSeconds)),
        };
        var events = await QueryAllAsync<AuditEvent>("audit_events", predicates, limit, ct);
        return events.OrderBy(e => e.CreatedAt).ToList();
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

    /// <summary>Deletes a directory and releases its bearer-token claim, so a rotated-away
    /// or deleted token's string can be issued again (the token hash is the claim key).</summary>
    public async Task DeleteScimDirectoryAsync(string id, CancellationToken ct = default)
    {
        var directory = await GetScimDirectoryAsync(id, ct);
        await DeleteIgnoringMissingAsync("scim_directories", id, ct);
        if (directory is not null)
            await ReleaseUniqueAsync("scim-token", directory.TokenKey, ct);
    }

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
        await DeleteIgnoringMissingAsync("org_domains", id, ct);
        if (domain is not null)
            await ReleaseUniqueAsync("org-domain", domain.DomainKey, ct);
    }

    /// <summary>The instance's enrollment domains, walked to the end — enrollment-domain
    /// lookups by name must not miss domains past the first page.</summary>
    public Task<IReadOnlyList<OrgDomain>> ListOrgDomainsAsync(string instanceId, CancellationToken ct = default) =>
        QueryAllAsync<OrgDomain>("org_domains", [Eq("instanceId", instanceId)], 100, ct);

    // ---- Signing keys -----------------------------------------------------------------------

    public Task<SigningKeyRecord> CreateSigningKeyAsync(SigningKeyRecord key, CancellationToken ct = default) =>
        CreateAsync("signing_keys", key.Id, key, ct);

    public Task UpsertSigningKeyAsync(SigningKeyRecord key, CancellationToken ct = default) =>
        UpsertAsync("signing_keys", key.Id, key, ct);

    public Task<IReadOnlyList<SigningKeyRecord>> ListSigningKeysAsync(string instanceId, CancellationToken ct = default) =>
        QueryAsync<SigningKeyRecord>("signing_keys", "instanceId", instanceId, 50, ct);
}
