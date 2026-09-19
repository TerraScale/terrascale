namespace TerraScale.Auth.Management;

using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>
/// Management feature endpoints: the developer API (Clerk Backend API
/// equivalent) over users, sessions, and API keys, keyed by bearer <c>sk_</c>
/// secrets and rate limited per key.
/// </summary>
public static class ManagementEndpoints
{
    private const int RateWindowSeconds = 300;

    /// <summary>One limiter for the whole process; every management endpoint passes through it.</summary>
    private static readonly RateLimiter Limiter = new(RateWindowSeconds);

    /// <summary>Maps Management endpoints.</summary>
    public static void Map(WebApplication app)
    {
        // ---- Users -----------------------------------------------------------------

        app.MapGet("/v1/{slug}/users", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store,
            CancellationToken ct, int? limit, [FromQuery(Name = "starting_after")] string? startingAfter) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
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
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
            return user is null
                ? ApiResults.NotFound("user_not_found", "no user with this id in this instance")
                : Results.Json(UserView(user, includePrivate: true));
        });

        app.MapPost("/v1/{slug}/users", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, SessionService sessions, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
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
            if (!await Billing.Quota.AllowsSignUpAsync(store, context.Instance.Id, ct))
                return ApiResults.Error(402, "quota_exceeded", "the instance has reached its plan's user quota; upgrade to add more users");

            User created;
            try
            {
                created = await sessions.CreateUserAsync(context.Instance.Id, body.Email, body.Username, body.Password, body.Name, ct);
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
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var (body, bodyFailure) = await ReadBodyAsync<UpdateUserBody>(request, ct);
            if (body is null)
                return bodyFailure ?? ApiResults.BadRequest("invalid_request", "a JSON body is required");

            var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
            if (user is null)
                return ApiResults.NotFound("user_not_found", "no user with this id in this instance");

            var updated = user with { UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            if (body.Name is not null)
                updated = updated with { Name = body.Name };
            if (body.PublicMetadata is not null)
                updated = updated with { PublicMetadata = body.PublicMetadata };
            if (body.PrivateMetadata is not null)
                updated = updated with { PrivateMetadata = body.PrivateMetadata };
            if (body.BanReason is not null)
                updated = updated with { BanReason = body.BanReason };
            if (body.Banned is not null)
                updated = updated with { Banned = body.Banned.Value, BanReason = body.Banned.Value ? updated.BanReason : null };

            if (body.Email is not null)
            {
                if (!SessionService.IsValidEmail(body.Email))
                    return ApiResults.BadRequest("invalid_email", "email is not a valid address");
                var normalized = SessionService.NormalizeEmail(body.Email);
                if (!string.Equals(normalized, user.Email, StringComparison.Ordinal))
                {
                    var newEmailKey = $"{context.Instance.Id}:{normalized}";
                    var existing = await store.FindUserByEmailAsync(context.Instance.Id, normalized, ct);
                    if (existing is not null && existing.Id != user.Id)
                        return ApiResults.Conflict("email_taken", "an account with this email already exists");
                    // Claim-first keeps the re-key atomic against concurrent creates.
                    try
                    {
                        await store.ClaimUniqueAsync("user-email", newEmailKey, user.Id, ct);
                    }
                    catch (TerraScale.DotNet.TerraScaleApiException)
                    {
                        return ApiResults.Conflict("email_taken", "an account with this email already exists");
                    }
                    // Email change invalidates verification and re-keys the uniqueness claim.
                    updated = updated with { Email = normalized, EmailVerified = false, EmailKey = newEmailKey };
                    if (user.EmailKey is not null)
                        await store.ReleaseUniqueAsync("user-email", user.EmailKey, ct);
                }
            }

            await store.UpsertUserAsync(updated, ct);
            await AuditAsync(store, context, "user.updated", new() { ["user_id"] = updated.Id }, ct);
            return Results.Json(UserView(updated, includePrivate: true));
        });

        app.MapDelete("/v1/{slug}/users/{userId}", async (
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var user = await FindLiveUserAsync(store, context.Instance.Id, userId, ct);
            if (user is null)
                return ApiResults.NotFound("user_not_found", "no user with this id in this instance");

            // Soft delete: the identifier claims are released so the keys free up for reuse.
            if (user.EmailKey is not null)
                await store.ReleaseUniqueAsync("user-email", user.EmailKey, ct);
            if (user.UsernameKey is not null)
                await store.ReleaseUniqueAsync("user-username", user.UsernameKey, ct);
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
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, SessionService sessions, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
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
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
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
            string slug, string sessionId, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, SessionService sessions, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
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
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, SessionService sessions, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
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
            string slug, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
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
            string slug, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
            if (gate.Failure is { } failure)
                return failure;
            var context = gate.Context!;

            var keys = (await store.ListApiKeysAsync(context.Instance.Id, ct))
                .Select(ApiKeyView)
                .ToList();
            return Results.Json(new { data = keys });
        });

        app.MapDelete("/v1/{slug}/api-keys/{keyId}", async (
            string slug, string keyId, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct) =>
        {
            var gate = await RequireInstanceAsync(request, slug, resolver, options, ct);
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

    /// <summary>Authentication + rate-limit gate; exactly one of <see cref="InstanceGate.Context"/>/<see cref="InstanceGate.Failure"/> is set.</summary>
    private sealed record InstanceGate(InstanceContext? Context, IResult? Failure);

    private static async Task<InstanceGate> RequireInstanceAsync(
        HttpRequest request, string slug, InstanceResolver resolver, AuthOptions options, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        if (context is null || context.Instance.Slug != slug)
            return new(null, ApiResults.Unauthorized());

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Limiter.Allow(context.ApiKeyId!, options.RateLimitPerWindow, now, out var remaining)
            ? new(context, null)
            : new(null, new RateLimitedResult(Limiter.RetryAfterSeconds(now), remaining));
    }

    /// <summary>The 429 response: error envelope plus Retry-After.</summary>
    private sealed class RateLimitedResult(int retryAfterSeconds, int remaining) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            httpContext.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString(CultureInfo.InvariantCulture);
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = new { code = "rate_limited", message = $"Management API rate limit exceeded; retry after {retryAfterSeconds} seconds." },
            });
        }
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
    /// the store filters in memory, so walk pages by document-id cursor.
    /// ponytail: a page with zero of this instance's users ends the scan early —
    /// correct until 1000 foreign users interleave; move to an indexed query then.
    /// </summary>
    private static async Task<List<User>> ListInstanceUsersAsync(AuthStore store, string instanceId, CancellationToken ct)
    {
        var users = new List<User>();
        string? after = null;
        while (true)
        {
            var page = await store.ListUsersAsync(instanceId, 1000, after, ct);
            users.AddRange(page);
            if (page.Count == 0)
                return users;
            after = page[^1].Id;
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

    private sealed record UpdateUserBody(
        string? Name,
        [property: JsonPropertyName("public_metadata")] Dictionary<string, object>? PublicMetadata,
        [property: JsonPropertyName("private_metadata")] Dictionary<string, object>? PrivateMetadata,
        bool? Banned,
        [property: JsonPropertyName("ban_reason")] string? BanReason,
        string? Email);

    private sealed record CreateApiKeyBody(string? Name, string[]? Scopes);
}
