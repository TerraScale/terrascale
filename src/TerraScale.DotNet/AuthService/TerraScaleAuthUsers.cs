using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// User management: CRUD, impersonation, per-user sessions, organizations,
/// MFA factors, and passkeys. The response casing is per-family on the wire:
/// users and sessions use snake_case, MFA factors and passkeys camelCase.
/// </summary>
public sealed class TerraScaleAuthUsersClient(TerraScaleAuthClient client)
{
    /// <summary>Lists instance users in ascending id order, cursor-paginated.</summary>
    public Task<TerraScaleAuthUserPage> ListAsync(
        int? limit = null,
        string? startingAfter = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and 100.");
        }

        return client.GetJsonAsync(
            client.ManagementRoute("/users") + TerraScaleAuthPaths.Query(limit, startingAfter),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthUserPage,
            cancellationToken);
    }

    /// <summary>
    /// Enumerates every instance user across pages, ordered by id. Pages are
    /// fetched sequentially with <paramref name="pageSize"/> (server default 20, maximum 100).
    /// </summary>
    public async IAsyncEnumerable<TerraScaleAuthUser> EnumerateAsync(
        int? pageSize = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The server's default page size when the caller does not pick one;
        // only used to detect a terminal short page.
        var requestedPageSize = pageSize ?? 20;
        string? startingAfter = null;
        while (true)
        {
            var page = await ListAsync(pageSize, startingAfter, cancellationToken).ConfigureAwait(false);
            // A {"data":null} body decodes a null page (pinned by the edge
            // suite); treat it as the empty page that ends the walk.
            if (page.Data is null)
            {
                yield break;
            }

            foreach (var user in page.Data)
            {
                yield return user;
            }

            if (page.Data.Count == 0)
            {
                yield break;
            }

            var nextCursor = page.Data[^1].Id;
            // A page shorter than requested is terminal; the listing is
            // id-ascending, so a cursor that does not advance (repeated or
            // regressed) means the server is looping — both stop the walk.
            if (page.Data.Count < requestedPageSize ||
                (startingAfter is not null && string.CompareOrdinal(nextCursor, startingAfter) <= 0))
            {
                yield break;
            }

            startingAfter = nextCursor;
        }
    }

    /// <summary>Gets one live user including private metadata and ban reason.</summary>
    public Task<TerraScaleAuthUser> GetAsync(string userId, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId)),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthUser,
            cancellationToken);

    /// <summary>
    /// Creates a user. Supplying an email or username claims it uniquely; a
    /// taken identifier fails with <c>email_taken</c>/<c>identifier_taken</c>.
    /// </summary>
    public Task<TerraScaleAuthUser> CreateAsync(
        string? email = null,
        string? username = null,
        string? password = null,
        string? name = null,
        CancellationToken cancellationToken = default) =>
        client.SendJsonAsync<TerraScaleAuthCreateUser, TerraScaleAuthUser>(
            HttpMethod.Post,
            client.ManagementRoute("/users"),
            new TerraScaleAuthCreateUser(email, username, password, name),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateUser,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthUser,
            cancellationToken);

    /// <summary>
    /// Updates one user. Only the fields configured on <paramref name="patch"/>
    /// are sent; the service distinguishes absent (unchanged) from explicit
    /// null (unset) per field.
    /// </summary>
    public Task<TerraScaleAuthUser> UpdateAsync(
        string userId,
        Action<TerraScaleAuthUserPatch> patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var builder = new TerraScaleAuthUserPatch();
        patch(builder);
        return client.SendPreparedJsonAsync<TerraScaleAuthUser>(
            HttpMethod.Patch,
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId)),
            builder.ToPayload(),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthUser,
            cancellationToken);
    }

    /// <summary>
    /// Soft-deletes one user, releasing every uniqueness claim, session,
    /// passkey, and membership, and freeing the plan's quota seat.
    /// </summary>
    public Task DeleteAsync(string userId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(
            HttpMethod.Delete,
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId)),
            cancellationToken);

    /// <summary>
    /// Mints a real session token for the user. The returned token is the
    /// cookie value (<c>tsauth_session</c>); only its hash is stored.
    /// </summary>
    public Task<TerraScaleAuthImpersonation> ImpersonateAsync(string userId, CancellationToken cancellationToken = default) =>
        client.PostJsonAsync(
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId) + "/impersonate"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthImpersonation,
            cancellationToken);

    /// <summary>Lists the user's active and revoked sessions in this instance.</summary>
    public Task<TerraScaleAuthSessionPage> ListSessionsAsync(string userId, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId) + "/sessions"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthSessionPage,
            cancellationToken);

    /// <summary>Revokes every active session of one user.</summary>
    public Task RevokeAllSessionsAsync(string userId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(
            HttpMethod.Post,
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId) + "/sessions/revoke-all"),
            cancellationToken);

    /// <summary>Lists the user's MFA factors (TOTP and recovery).</summary>
    public Task<TerraScaleAuthMfaFactorPage> ListMfaFactorsAsync(string userId, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId) + "/mfa-factors"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthMfaFactorPage,
            cancellationToken);

    /// <summary>Deletes one MFA factor (e.g. to unblock a user who lost their authenticator).</summary>
    public Task DeleteMfaFactorAsync(string userId, string factorId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(
            HttpMethod.Delete,
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId) + "/mfa-factors/" + TerraScaleAuthPaths.Escape(factorId)),
            cancellationToken);

    /// <summary>
    /// Regenerates a recovery factor's codes and returns the ten new plaintext
    /// codes; the old codes stop working immediately.
    /// </summary>
    public Task<TerraScaleAuthRecoveryCodes> RegenerateRecoveryCodesAsync(string userId, string factorId, CancellationToken cancellationToken = default) =>
        client.SendJsonAsync<TerraScaleAuthEmptyBody, TerraScaleAuthRecoveryCodes>(
            HttpMethod.Post,
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId) + "/mfa-factors/" + TerraScaleAuthPaths.Escape(factorId) + "/regenerate-recovery"),
            TerraScaleAuthEmptyBody.Instance,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthEmptyBody,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthRecoveryCodes,
            cancellationToken);

    /// <summary>Lists the user's registered passkeys.</summary>
    public Task<TerraScaleAuthPasskeyPage> ListPasskeysAsync(string userId, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId) + "/passkeys"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthPasskeyPage,
            cancellationToken);

    /// <summary>Deletes one registered passkey.</summary>
    public Task DeletePasskeyAsync(string userId, string passkeyId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(
            HttpMethod.Delete,
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId) + "/passkeys/" + TerraScaleAuthPaths.Escape(passkeyId)),
            cancellationToken);

    /// <summary>Lists the user's organization memberships with the full organization records.</summary>
    public Task<IReadOnlyList<TerraScaleAuthUserOrganization>> ListOrganizationsAsync(string userId, CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/users/" + TerraScaleAuthPaths.Escape(userId) + "/organizations"),
            TerraScaleAuthJsonContext.Default.IReadOnlyListTerraScaleAuthUserOrganization,
            cancellationToken);
}

/// <summary>Session revocation by session id.</summary>
public sealed class TerraScaleAuthSessionsClient(TerraScaleAuthClient client)
{
    /// <summary>Revokes one session; its cookie token stops authenticating immediately.</summary>
    public Task RevokeAsync(string sessionId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(
            HttpMethod.Post,
            client.ManagementRoute("/sessions/" + TerraScaleAuthPaths.Escape(sessionId) + "/revoke"),
            cancellationToken);
}

/// <summary>
/// Tri-state user update builder. Calling a method sets the field; passing
/// null to a nullable method explicitly unsets it; never calling the method
/// leaves the field unchanged. An empty patch is rejected.
/// </summary>
public sealed class TerraScaleAuthUserPatch
{
    private readonly JsonObject body = new();

    /// <summary>Sets or unsets the display name.</summary>
    public TerraScaleAuthUserPatch Name(string? name) => Set("name", name);

    /// <summary>Sets or unsets the username (its uniqueness claim moves with it).</summary>
    public TerraScaleAuthUserPatch Username(string? username) => Set("username", username);

    /// <summary>Sets or unsets the password (minimum 8 characters when set).</summary>
    public TerraScaleAuthUserPatch Password(string? password) => Set("password", password);

    /// <summary>
    /// Re-keys the email address. Verification resets and pending verification
    /// emails are invalidated; a taken address fails with <c>email_taken</c>.
    /// The email cannot be unset, only replaced.
    /// </summary>
    public TerraScaleAuthUserPatch Email(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return Set("email", email);
    }

    /// <summary>Sets or clears the ban flag. Combine with <see cref="BanReason"/> to record why.</summary>
    public TerraScaleAuthUserPatch Banned(bool banned) => Set("banned", banned);

    /// <summary>Sets or unsets the ban reason (cleared automatically when unbanning).</summary>
    public TerraScaleAuthUserPatch BanReason(string? reason) => Set("ban_reason", reason);

    /// <summary>Replaces public metadata wholesale (null clears it).</summary>
    public TerraScaleAuthUserPatch PublicMetadata(IReadOnlyDictionary<string, JsonElement>? metadata) =>
        Set("public_metadata", metadata);

    /// <summary>Replaces private metadata wholesale (null clears it).</summary>
    public TerraScaleAuthUserPatch PrivateMetadata(IReadOnlyDictionary<string, JsonElement>? metadata) =>
        Set("private_metadata", metadata);

    internal byte[] ToPayload()
    {
        if (body.Count == 0)
        {
            throw new ArgumentException("The user patch is empty; configure at least one field.", nameof(body));
        }

        return JsonSerializer.SerializeToUtf8Bytes(body, TerraScaleAuthJsonContext.Default.JsonObject);
    }

    private TerraScaleAuthUserPatch Set(string field, string? value)
    {
        body[field] = value is null ? null : JsonValue.Create(value);
        return this;
    }

    private TerraScaleAuthUserPatch Set(string field, bool value)
    {
        body[field] = JsonValue.Create(value);
        return this;
    }

    private TerraScaleAuthUserPatch Set(string field, IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is null)
        {
            body[field] = null;
            return this;
        }

        var node = new JsonObject();
        foreach (var (key, value) in metadata)
        {
            node[key] = value.Deserialize(TerraScaleAuthJsonContext.Default.JsonNode);
        }

        body[field] = node;
        return this;
    }
}

/// <summary>A ts-auth user record. List responses omit <c>ban_reason</c> and <c>private_metadata</c>.</summary>
public sealed record TerraScaleAuthUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("email_verified")] bool EmailVerified,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("banned")] bool Banned,
    [property: JsonPropertyName("ban_reason")] string? BanReason,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("last_sign_in_at")] long? LastSignInAt,
    [property: JsonPropertyName("public_metadata")] IReadOnlyDictionary<string, JsonElement>? PublicMetadata,
    [property: JsonPropertyName("private_metadata")] IReadOnlyDictionary<string, JsonElement>? PrivateMetadata);

/// <summary>One page of the cursor-paginated user list.</summary>
public sealed record TerraScaleAuthUserPage(
    [property: JsonPropertyName("data")] IReadOnlyList<TerraScaleAuthUser> Data);

/// <summary>A server-side session.</summary>
public sealed record TerraScaleAuthSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("expires_at")] long ExpiresAt,
    [property: JsonPropertyName("last_active_at")] long? LastActiveAt,
    [property: JsonPropertyName("user_agent")] string? UserAgent,
    [property: JsonPropertyName("ip_address")] string? IpAddress);

/// <summary>One page of a user's sessions.</summary>
public sealed record TerraScaleAuthSessionPage(
    [property: JsonPropertyName("data")] IReadOnlyList<TerraScaleAuthSession> Data);

/// <summary>An impersonation grant: the session token and its expiry.</summary>
public sealed record TerraScaleAuthImpersonation(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires_at")] long ExpiresAt)
{
    /// <inheritdoc cref="object.ToString" />
    public override string ToString() =>
        $"{typeof(TerraScaleAuthImpersonation).Name} {{ Token = [REDACTED], ExpiresAt = {ExpiresAt} }}";
}

internal sealed record TerraScaleAuthCreateUser(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record TerraScaleAuthEmptyBody
{
    public static readonly TerraScaleAuthEmptyBody Instance = new();
}

/// <summary>An MFA factor (wire is camelCase in this family).</summary>
public sealed record TerraScaleAuthMfaFactor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("confirmed")] bool Confirmed,
    [property: JsonPropertyName("createdAt")] long CreatedAt);

/// <summary>One page of a user's MFA factors.</summary>
public sealed record TerraScaleAuthMfaFactorPage(
    [property: JsonPropertyName("data")] IReadOnlyList<TerraScaleAuthMfaFactor> Data);

/// <summary>Regenerated plaintext recovery codes.</summary>
public sealed record TerraScaleAuthRecoveryCodes(
    [property: JsonPropertyName("data")] IReadOnlyList<string> Data)
{
    /// <inheritdoc cref="object.ToString" />
    public override string ToString() =>
        $"{typeof(TerraScaleAuthRecoveryCodes).Name} {{ RecoveryCodes = {Data.Count} }}";
}

/// <summary>A registered passkey (wire is camelCase in this family).</summary>
public sealed record TerraScaleAuthPasskey(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("nickname")] string? Nickname,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("lastUsedAt")] long? LastUsedAt);

/// <summary>A user's registered passkeys (root key is <c>passkeys</c>, not <c>data</c>).</summary>
public sealed record TerraScaleAuthPasskeyPage(
    [property: JsonPropertyName("passkeys")] IReadOnlyList<TerraScaleAuthPasskey> Passkeys);

/// <summary>A user's membership in one organization, with the organization record.</summary>
public sealed record TerraScaleAuthUserOrganization(
    [property: JsonPropertyName("id")] string MembershipId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("organization")] TerraScaleAuthOrganization? Organization);

/// <summary>An organization record.</summary>
public sealed record TerraScaleAuthOrganization(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("created_at")] long CreatedAt);
