namespace TerraScale.Auth.Management;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Auth.Api;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>
/// Management feature endpoints: the developer API (Clerk Backend API
/// equivalent) over users, sessions, and API keys, keyed by bearer <c>sk_</c>
/// secrets. Rate limiting and API-key scope enforcement are applied process-wide
/// by <see cref="ManagementGate"/>, not per module.
/// </summary>
public static class ManagementEndpoints
{
    /// <summary>Maps Management endpoints.</summary>
    public static void Map(WebApplication app)
    {
        // ---- Users -----------------------------------------------------------------

        app.MapGet("/v1/{slug}/users", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store,
            CancellationToken ct, int? limit, [FromQuery(Name = "starting_after")] string? startingAfter) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            if (limit is < 1 or > 100)
                return ApiResults.BadRequest("invalid_request", "limit must be between 1 and 100");

            var page = (await ListInstanceUsersAsync(store, context.Instance.Id, ct))
                .Where(u => !u.Deleted)
                .OrderBy(u => u.Id, StringComparer.Ordinal)
                .Where(u => startingAfter is null || string.CompareOrdinal(u.Id, startingAfter) > 0)
                .Take(limit ?? 20)
                .Select(u => UserView(u, includePrivate: false))
                .ToList();
            return Results.Json(new { data = page });
        });

        app.MapGet("/v1/{slug}/users/{userId}", async (
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
            return user is null
                ? ApiResults.NotFound("user_not_found", "no user with this id in this instance")
                : Results.Json(UserView(user, includePrivate: true));
        });

        app.MapPost("/v1/{slug}/users", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, SessionService sessions, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var (body, bodyFailure) = await ReadBodyAsync<CreateUserBody>(request, ct);
            if (body is null)
                return bodyFailure ?? ApiResults.BadRequest("invalid_request", "a JSON body is required");

            if (body.Email is not null)
            {
                if (!SessionService.IsValidEmail(body.Email))
                    return ApiResults.BadRequest("invalid_email", "email is not a valid address");
                if (await store.FindUserByEmailAsync(context.Instance.Id, SessionService.NormalizeEmail(body.Email), ct) is not null)
                    return ApiResults.Conflict("email_taken", "an account with this email already exists");
            }
            User created;
            try
            {
                created = await sessions.CreateUserAsync(context.Instance.Id, body.Email, body.Username, body.Password, body.Name, ct);
            }
            catch (QuotaExceededException)
            {
                return ApiResults.Error(402, "quota_exceeded", "the instance has reached its plan's user quota; upgrade to add more users");
            }
            catch (TerraScale.DotNet.TerraScaleApiException)
            {
                // ponytail: unique-index race between the email pre-check and the create; the engine
                // does not say which key conflicted yet, so both email and username map here.
                return ApiResults.Conflict("identifier_taken", "email or username already exists");
            }

            await AuditAsync(store, context, "user.created", new() { ["user_id"] = created.Id }, ct);
            return Results.Json(UserView(created, includePrivate: true), statusCode: 201);
        });

        app.MapPatch("/v1/{slug}/users/{userId}", async (
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var (body, bodyFailure) = await ReadBodyAsync<UpdateUserBody>(request, ct);
            if (body is null)
                return bodyFailure ?? ApiResults.BadRequest("invalid_request", "a JSON body is required");

            // PATCH tri-state: absent leaves a field alone, explicit null unsets it, a value sets it.
            if (PatchShapeFailure(body) is { } shapeFailure)
                return shapeFailure;

            var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
            if (user is null)
                return ApiResults.NotFound("user_not_found", "no user with this id in this instance");

            var updated = user with
            {
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Name = PatchString(body.Name, user.Name),
                BanReason = PatchString(body.BanReason, user.BanReason),
                PublicMetadata = PatchMetadata(body.PublicMetadata, user.PublicMetadata),
                PrivateMetadata = PatchMetadata(body.PrivateMetadata, user.PrivateMetadata),
            };
            if (body.Banned is { } banned)
                updated = updated with { Banned = banned, BanReason = banned ? updated.BanReason : null };
            if (body.Password.ValueKind != JsonValueKind.Undefined)
                updated = updated with { PasswordHash = body.Password.ValueKind == JsonValueKind.Null ? null : PasswordHasher.Hash(body.Password.GetString()!) };

            // Identifier re-keys are claim-first; the new claims are held before the user
            // write and released again if that write fails, so a crash cannot strand them.
            string? claimedEmailKey = null;
            string? claimedUsernameKey = null;
            var usernameKeyChanged = false;
            try
            {
                if (body.Email.ValueKind == JsonValueKind.String)
                {
                    var raw = body.Email.GetString()!;
                    if (!SessionService.IsValidEmail(raw))
                        return ApiResults.BadRequest("invalid_email", "email is not a valid address");
                    var normalized = SessionService.NormalizeEmail(raw);
                    if (!string.Equals(normalized, user.Email, StringComparison.Ordinal))
                    {
                        var newEmailKey = $"{context.Instance.Id}:{normalized}";
                        var existing = await store.FindUserByEmailAsync(context.Instance.Id, normalized, ct);
                        if (existing is not null && existing.Id != user.Id)
                            return ApiResults.Conflict("email_taken", "an account with this email already exists");
                        try
                        {
                            await store.ClaimUniqueAsync("user-email", newEmailKey, user.Id, ct);
                            claimedEmailKey = newEmailKey;
                        }
                        catch (TerraScale.DotNet.TerraScaleApiException)
                        {
                            return ApiResults.Conflict("email_taken", "an account with this email already exists");
                        }
                        // An email change invalidates verification: it re-proves nothing about the new address.
                        updated = updated with { Email = normalized, EmailVerified = false, EmailKey = newEmailKey };
                    }
                }

                if (body.Username.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                {
                    var username = body.Username.ValueKind == JsonValueKind.Null || body.Username.GetString()!.Trim() is { Length: 0 }
                        ? null
                        : body.Username.GetString()!.Trim();
                    var newUsernameKey = username is null ? null : $"{context.Instance.Id}:{username.ToLowerInvariant()}";
                    if (newUsernameKey != user.UsernameKey)
                    {
                        if (newUsernameKey is not null)
                        {
                            try
                            {
                                await store.ClaimUniqueAsync("user-username", newUsernameKey, user.Id, ct);
                                claimedUsernameKey = newUsernameKey;
                            }
                            catch (TerraScale.DotNet.TerraScaleApiException)
                            {
                                return ApiResults.Conflict("username_taken", "an account with this username already exists");
                            }
                        }
                        updated = updated with { Username = username, UsernameKey = newUsernameKey };
                        usernameKeyChanged = true;
                    }
                    else
                    {
                        // Same normalized username: display-only change (e.g. casing), the claim stays.
                        updated = updated with { Username = username };
                    }
                }

                await store.UpsertUserAsync(updated, ct);
            }
            catch
            {
                if (claimedEmailKey is not null)
                    await store.ReleaseUniqueAsync("user-email", claimedEmailKey, ct);
                if (claimedUsernameKey is not null)
                    await store.ReleaseUniqueAsync("user-username", claimedUsernameKey, ct);
                throw;
            }

            if (claimedEmailKey is not null)
            {
                // Pending verification links prove the OLD address; they must not verify the new one.
                await store.InvalidateVerificationTokensAsync(user.Id, "verify_email", ct);
                if (user.EmailKey is not null)
                    await store.ReleaseUniqueAsync("user-email", user.EmailKey, ct);
            }
            // A username change or unset retires the old claim, whichever direction the key moved.
            if (usernameKeyChanged && user.UsernameKey is not null)
                await store.ReleaseUniqueAsync("user-username", user.UsernameKey, ct);

            await AuditAsync(store, context, "user.updated", new() { ["user_id"] = updated.Id }, ct);
            return Results.Json(UserView(updated, includePrivate: true));
        });

        app.MapDelete("/v1/{slug}/users/{userId}", async (
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
            if (user is null)
                return ApiResults.NotFound("user_not_found", "no user with this id in this instance");

            // Soft delete: every uniqueness claim the user holds is released so the keys
            // free up for reuse — email, username, each passkey credential, and every
            // membership (SCIM deprovisioning behaves the same way).
            if (user.EmailKey is not null)
                await store.ReleaseUniqueAsync("user-email", user.EmailKey, ct);
            if (user.UsernameKey is not null)
                await store.ReleaseUniqueAsync("user-username", user.UsernameKey, ct);
            foreach (var passkey in await store.ListPasskeysAsync(user.Id, ct))
                await store.DeletePasskeyAsync(passkey.Id, ct);
            foreach (var membership in await store.ListMembershipsByUserAsync(user.Id, ct))
                if (membership.InstanceId == context.Instance.Id)
                    await store.DeleteMembershipAsync(membership.Id, ct);
            await store.DeleteUserAuthenticationArtifactsAsync(context.Instance.Id, user.Id, ct);
            await store.ReleaseQuotaSeatAsync(user, ct);
            await store.UpsertUserAsync(user with
            {
                Deleted = true,
                EmailKey = null,
                UsernameKey = null,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);
            await AuditAsync(store, context, "user.deleted", new() { ["user_id"] = user.Id }, ct);
            return Results.NoContent();
        });

        app.MapPost("/v1/{slug}/users/{userId}/impersonate", async (
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, SessionService sessions, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
            if (user is null)
                return ApiResults.NotFound("user_not_found", "no user with this id in this instance");
            if (user.Banned)
                return ApiResults.Forbidden("this user is banned");

            var userAgent = request.Headers.UserAgent.ToString();
            var (session, token) = await sessions.CreateSessionAsync(
                context.Instance.Id,
                user.Id,
                userAgent.Length == 0 ? null : userAgent,
                request.HttpContext.Connection.RemoteIpAddress?.ToString(),
                ct);
            await AuditAsync(store, context, "user.impersonated", new() { ["user_id"] = user.Id, ["api_key_id"] = context.ApiKeyId! }, ct);
            return Results.Json(new { token, expires_at = session.ExpiresAt }, statusCode: 201);
        });

        // ---- Sessions -----------------------------------------------------------------

        app.MapGet("/v1/{slug}/users/{userId}/sessions", async (
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
            if (user is null)
                return ApiResults.NotFound("user_not_found", "no user with this id in this instance");

            var sessions = (await store.ListSessionsByUserAsync(user.Id, ct))
                .Where(s => s.InstanceId == context.Instance.Id)
                .Select(SessionView)
                .ToList();
            return Results.Json(new { data = sessions });
        });

        app.MapPost("/v1/{slug}/sessions/{sessionId}/revoke", async (
            string slug, string sessionId, HttpRequest request, InstanceResolver resolver, AuthStore store, SessionService sessions, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var session = await store.GetSessionAsync(sessionId, ct);
            if (session is null || session.InstanceId != context.Instance.Id)
                return ApiResults.NotFound("session_not_found", "no session with this id in this instance");

            await sessions.RevokeAsync(context.Instance.Id, sessionId, ct);
            return Results.NoContent();
        });

        app.MapPost("/v1/{slug}/users/{userId}/sessions/revoke-all", async (
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, SessionService sessions, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
            if (user is null)
                return ApiResults.NotFound("user_not_found", "no user with this id in this instance");

            foreach (var session in await store.ListSessionsByUserAsync(user.Id, ct))
                if (session.InstanceId == context.Instance.Id && session.Status == "active")
                    await sessions.RevokeAsync(context.Instance.Id, session.Id, ct);
            return Results.NoContent();
        });

        // ---- API keys -----------------------------------------------------------------

        app.MapPost("/v1/{slug}/api-keys", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var (body, bodyFailure) = await ReadBodyAsync<CreateApiKeyBody>(request, ct);
            if (body is null)
                return bodyFailure ?? ApiResults.BadRequest("invalid_request", "a JSON body is required");
            if (string.IsNullOrWhiteSpace(body.Name))
                return ApiResults.BadRequest("invalid_request", "name is required");

            // The bearer credential is the full "sk_id_secret" string; only its sha256 is stored,
            // matching what InstanceResolver looks up.
            var (id, _, combined) = SecureTokens.NewSecret("sk");
            var key = new ApiKey
            {
                Id = id,
                InstanceId = context.Instance.Id,
                Name = body.Name.Trim(),
                SecretKey = SecureTokens.Sha256Hex(combined),
                Scopes = body.Scopes is { Length: > 0 } ? body.Scopes : ["*"],
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            await store.CreateApiKeyAsync(key, ct);
            await AuditAsync(store, context, "api_key.created", new() { ["api_key_id"] = key.Id }, ct);
            return Results.Json(new { id = key.Id, name = key.Name, api_key = combined, scopes = key.Scopes }, statusCode: 201);
        });

        app.MapGet("/v1/{slug}/api-keys", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var keys = (await store.ListApiKeysAsync(context.Instance.Id, ct))
                .Select(ApiKeyView)
                .ToList();
            return Results.Json(new { data = keys });
        });

        app.MapDelete("/v1/{slug}/api-keys/{keyId}", async (
            string slug, string keyId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var key = await store.GetApiKeyAsync(keyId, ct);
            if (key is null || key.InstanceId != context.Instance.Id)
                return ApiResults.NotFound("api_key_not_found", "no api key with this id in this instance");

            await store.UpsertApiKeyAsync(key with { Revoked = true }, ct);
            await AuditAsync(store, context, "api_key.revoked", new() { ["api_key_id"] = key.Id }, ct);
            return Results.NoContent();
        });
    }

    // ---- Shared plumbing ------------------------------------------------------------

    /// <summary>Authentication gate; exactly one of <see cref="InstanceGate.Context"/>/<see cref="InstanceGate.Failure"/> is set.
    /// Rate limiting happens upstream in <see cref="ManagementGate"/>.</summary>
    private sealed record InstanceGate(InstanceContext? Context, IResult? Failure);

    private static async Task<InstanceGate> RequireInstanceAsync(
        HttpRequest request, string slug, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        return context is not null && context.Instance.Slug == slug
            ? new(context, null)
            : new(null, ApiResults.Unauthorized());
    }

    private static async Task<(TBody? Body, IResult? Failure)> ReadBodyAsync<TBody>(HttpRequest request, CancellationToken ct) where TBody : class
    {
        try
        {
            var body = await request.ReadFromJsonAsync<TBody>(ct);
            return body is null
                ? (null, ApiResults.BadRequest("invalid_request", "a JSON body is required"))
                : (body, null);
        }
        catch (JsonException)
        {
            return (null, ApiResults.BadRequest("invalid_json", "the request body is not valid JSON"));
        }
    }

    private static Task AuditAsync(AuthStore store, InstanceContext context, string type, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            Type = type,
            ActorId = context.ApiKeyId,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);

    private static async Task<User?> FindLiveUserAsync(AuthStore store, string instanceId, string userId, CancellationToken ct)
    {
        var user = await store.GetUserAsync(userId, ct);
        return user is null || user.InstanceId != instanceId || user.Deleted ? null : user;
    }

    /// <summary>
    /// All users of one instance. The engine lists the shared users collection and
    /// the store filters in memory, so the walk advances by the raw page's last
    /// document id and only ends when the engine returns an empty page.
    /// </summary>
    private static async Task<List<User>> ListInstanceUsersAsync(AuthStore store, string instanceId, CancellationToken ct)
    {
        var users = new List<User>();
        string? after = null;
        while (true)
        {
            var (page, next) = await store.ListUsersAsync(instanceId, 1000, after, ct);
            users.AddRange(page);
            if (next is null)
                return users;
            after = next;
        }
    }

    private static Dictionary<string, object?> UserView(User user, bool includePrivate)
    {
        var view = new Dictionary<string, object?>
        {
            ["id"] = user.Id,
            ["email"] = user.Email,
            ["email_verified"] = user.EmailVerified,
            ["username"] = user.Username,
            ["name"] = user.Name,
            ["banned"] = user.Banned,
            ["created_at"] = user.CreatedAt,
            ["last_sign_in_at"] = user.LastSignInAt,
            ["public_metadata"] = user.PublicMetadata,
        };
        if (includePrivate)
        {
            view["ban_reason"] = user.BanReason;
            view["private_metadata"] = user.PrivateMetadata;
        }
        return view;
    }

    private static Dictionary<string, object?> SessionView(Session session) => new()
    {
        ["id"] = session.Id,
        ["status"] = session.Status,
        ["created_at"] = session.CreatedAt,
        ["expires_at"] = session.ExpiresAt,
        ["last_active_at"] = session.LastActiveAt,
        ["user_agent"] = session.UserAgent,
        ["ip_address"] = session.IpAddress,
    };

    private static Dictionary<string, object?> ApiKeyView(ApiKey key) => new()
    {
        ["id"] = key.Id,
        ["name"] = key.Name,
        ["scopes"] = key.Scopes,
        ["created_at"] = key.CreatedAt,
        ["last_used_at"] = key.LastUsedAt,
        ["revoked"] = key.Revoked,
    };

    // ---- Request bodies ------------------------------------------------------------

    private sealed record CreateUserBody(string? Email, string? Username, string? Password, string? Name);

    /// <summary>
    /// PATCH tri-state body. Non-nullable <see cref="JsonElement"/> properties are the
    /// only System.Text.Json shape that separates the three cases: absent property is
    /// <see cref="JsonValueKind.Undefined"/>, explicit JSON null is
    /// <see cref="JsonValueKind.Null"/>, and a value carries its own kind. A nullable
    /// JsonElement would collapse null and absent.
    /// </summary>
    private sealed record UpdateUserBody(
        JsonElement Name,
        JsonElement Username,
        JsonElement Password,
        [property: JsonPropertyName("public_metadata")] JsonElement PublicMetadata,
        [property: JsonPropertyName("private_metadata")] JsonElement PrivateMetadata,
        bool? Banned,
        [property: JsonPropertyName("ban_reason")] JsonElement BanReason,
        JsonElement Email);

    /// <summary>Scopes are Management API route families ("users", "webhooks", ...),
    /// enforced by <see cref="ManagementGate"/>; omitted scopes mean "*" (full access).</summary>
    private sealed record CreateApiKeyBody(string? Name, string[]? Scopes);

    /// <summary>Rejects PATCH bodies whose fields carry JSON kinds the field cannot hold.</summary>
    private static IResult? PatchShapeFailure(UpdateUserBody body)
    {
        foreach (var (field, element) in new (string Field, JsonElement Element)[]
        {
            ("name", body.Name), ("username", body.Username), ("password", body.Password), ("ban_reason", body.BanReason), ("email", body.Email),
        })
            if (element.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String or JsonValueKind.Null))
                return ApiResults.BadRequest("invalid_request", $"{field} must be a string or null");
        foreach (var (field, element) in new (string Field, JsonElement Element)[]
        {
            ("public_metadata", body.PublicMetadata), ("private_metadata", body.PrivateMetadata),
        })
            if (element.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Null))
                return ApiResults.BadRequest("invalid_request", $"{field} must be an object or null");
        if (body.Email.ValueKind == JsonValueKind.Null)
            return ApiResults.BadRequest("invalid_request", "email cannot be removed; supply a new address instead");
        if (body.Password.ValueKind == JsonValueKind.String && body.Password.GetString()!.Length < 8)
            return ApiResults.BadRequest("invalid_password", "password must be at least 8 characters");
        return null;
    }

    /// <summary>PATCH tri-state for a string field: absent keeps <paramref name="current"/>, explicit null unsets, a value sets.</summary>
    private static string? PatchString(JsonElement property, string? current) =>
        property.ValueKind == JsonValueKind.Undefined ? current
        : property.ValueKind == JsonValueKind.Null ? null
        : property.GetString();

    /// <summary>PATCH tri-state for metadata: absent keeps, explicit null clears, an object replaces wholesale (no deep merge).</summary>
    private static Dictionary<string, object> PatchMetadata(JsonElement property, Dictionary<string, object> current) =>
        property.ValueKind == JsonValueKind.Undefined ? current
        : property.ValueKind == JsonValueKind.Null ? []
        : property.Deserialize<Dictionary<string, object>>() ?? [];
}
