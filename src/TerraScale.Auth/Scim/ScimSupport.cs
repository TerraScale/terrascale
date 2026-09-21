namespace TerraScale.Auth.Scim;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.DotNet;

internal static class ScimSupport
{
    internal const string ScimContentType = "application/scim+json";

    internal const int MaxResults = 100;

    internal const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";

    internal const string ListResponseSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";

    internal const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";

    internal const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- Shared plumbing ------------------------------------------------------------

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal static IResult ScimJson(object body, int statusCode = 200) =>
        Results.Json(body, contentType: ScimContentType, statusCode: statusCode);

    internal static IResult ScimError(int status, string detail, string? scimType = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["schemas"] = new[] { ErrorSchema },
            ["status"] = status.ToString(),
            ["detail"] = detail,
        };
        if (scimType is not null)
            body["scimType"] = scimType;
        // RFC 7644 s2: the provider SHALL advertise its auth scheme via WWW-Authenticate.
        return status == StatusCodes.Status401Unauthorized
            ? new WithHeader(ScimJson(body, status), "WWW-Authenticate", "Bearer")
            : ScimJson(body, status);
    }

    /// <summary>RFC 7644 s3.3: the created resource's URI SHALL ride the Location header.</summary>
    internal static IResult ScimCreated(object body, string location) =>
        new WithHeader(ScimJson(body, 201), "Location", location);

    internal sealed record WithHeader(IResult Inner, string Name, string Value) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.Headers[Name] = Value;
            await Inner.ExecuteAsync(context);
        }
    }

    /// <summary>RFC 7644 s3.4.2.4: startIndex below 1 reads as 1, negative count reads as 0,
    /// and a count above the server maximum is clamped — none of these is an error.</summary>
    internal static (int StartIndex, int PageSize) Paginate(int? startIndex, int? count)
    {
        var start = startIndex is > 0 ? startIndex.Value : 1;
        var size = count is null ? MaxResults : Math.Clamp(count.Value, 0, MaxResults);
        return (start, size);
    }

    internal static async Task<(TBody? Body, IResult? Failure)> ReadBodyAsync<TBody>(HttpRequest request, CancellationToken ct) where TBody : class
    {
        try
        {
            var body = await request.ReadFromJsonAsync<TBody>(ct);
            return body is null ? (null, ScimError(400, "a JSON body is required")) : (body, null);
        }
        catch (JsonException)
        {
            return (null, ScimError(400, "the request body is not valid JSON", "invalidSyntax"));
        }
        catch (InvalidOperationException)
        {
            // A body that is not JSON at all (e.g. text/plain) must not surface as a 500.
            return (null, ScimError(400, "the request body must be JSON", "invalidSyntax"));
        }
    }

    /// <summary>Management gate: sk_ bearer that resolves the route's instance.</summary>
    internal static async Task<InstanceContext?> ResolveInstanceAsync(HttpRequest request, string slug, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        return context is not null && context.Instance.Slug == slug ? context : null;
    }

    internal sealed record ScimGate(ScimDirectory? Directory, IResult? Failure);

    /// <summary>SCIM gate: bearer scim_ token that resolves exactly this route's directory.</summary>
    internal static async Task<ScimGate> RequireDirectoryAsync(HttpRequest request, AuthStore store, string directoryId, CancellationToken ct)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return new(null, ScimError(401, "a bearer token is required"));
        var token = authorization["Bearer ".Length..].Trim();
        if (!token.StartsWith("scim_", StringComparison.Ordinal))
            return new(null, ScimError(401, "invalid bearer token"));
        var directory = await store.FindScimDirectoryByTokenAsync(SecureTokens.Sha256Hex(token), ct);
        if (directory is null || directory.Id != directoryId)
            return new(null, ScimError(401, "invalid bearer token for this directory"));
        return new(directory, null);
    }

    internal static async Task<ScimDirectory?> FindInstanceDirectoryAsync(AuthStore store, string instanceId, string directoryId, CancellationToken ct)
    {
        var directory = await store.GetScimDirectoryAsync(directoryId, ct);
        return directory is null || directory.InstanceId != instanceId ? null : directory;
    }

    internal static async Task<User?> FindScimUserAsync(AuthStore store, ScimDirectory directory, string userId, CancellationToken ct)
    {
        var user = await store.GetUserAsync(userId, ct);
        return user is null || user.InstanceId != directory.InstanceId || user.Deleted ? null : user;
    }

    internal static async Task<ScimGroup?> FindScimGroupAsync(ScimGroups groups, string directoryId, string groupId, CancellationToken ct)
    {
        var group = await groups.GetAsync(groupId, ct);
        return group is null || group.DirectoryId != directoryId ? null : group;
    }

    internal sealed record RekeyResult(User User, string? SupersededEmailKey, IResult? Failure);

    /// <summary>
    /// Claim-first email re-key — the same pattern as the Management user update: the new
    /// claim is taken here, the superseded key is released only after the user write
    /// succeeds (<see cref="WriteScimUserAsync"/>), so a lost race or a failed write can
    /// never strand or steal a claim. Keyed on EmailKey, not the display string, so a PUT
    /// repeating the same userName on a deprovisioned user re-claims the released address.
    /// </summary>
    internal static async Task<RekeyResult> WithEmailAsync(AuthStore store, string instanceId, User current, string email, CancellationToken ct)
    {
        if (!SessionService.IsValidEmail(email))
            return new(current, null, ScimError(400, "userName must be a valid email address"));
        var normalized = SessionService.NormalizeEmail(email);
        var newKey = $"{instanceId}:{normalized}";
        if (string.Equals(newKey, current.EmailKey, StringComparison.Ordinal))
            return new(current, null, null);
        var existing = await store.FindUserByEmailAsync(instanceId, normalized, ct);
        if (existing is not null && existing.Id != current.Id)
            return new(current, null, ScimError(409, "another user with this userName already exists", "uniqueness"));
        try
        {
            await store.ClaimUniqueAsync("user-email", newKey, current.Id, ct);
        }
        catch (TerraScaleApiException)
        {
            return new(current, null, ScimError(409, "another user with this userName already exists", "uniqueness"));
        }
        return new(current with { Email = normalized, EmailVerified = false, EmailKey = newKey }, current.EmailKey, null);
    }

    internal sealed record UserWriteResult(User User, IResult? Failure);

    /// <summary>
    /// Persists a SCIM user write together with its consistency side effects, so
    /// deprovision (PATCH active=false) and delete behave the same except for the
    /// Deleted flag: the user leaves every membership and SCIM group in the instance and
    /// releases their email/username claims, keeping the display strings so the address
    /// is reusable while the user stays restorable. Reactivation re-claims a missing
    /// identifier — or conflicts when another user took it in the meantime. Claims taken
    /// here are rolled back if the user write fails.
    /// </summary>
    internal static async Task<UserWriteResult> WriteScimUserAsync(
        AuthStore store, TerraScaleClient client, ScimDirectory directory, User updated, IReadOnlyList<string> supersededEmailKeys, CancellationToken ct)
    {
        var groups = new ScimGroups(client, store.Database);
        var claimed = new List<(string Kind, string Key)>();
        try
        {
            if (updated.Banned || updated.Deleted)
            {
                if (updated.Deleted)
                    await store.DeleteUserAuthenticationArtifactsAsync(directory.InstanceId, updated.Id, ct);
                foreach (var membership in await store.ListMembershipsByUserAsync(updated.Id, ct))
                    await store.DeleteMembershipAsync(membership.Id, ct);
                foreach (var group in await groups.ListByInstanceAsync(directory.InstanceId, ct))
                    if (group.MemberUserIds.Contains(updated.Id))
                        await groups.UpsertAsync(group with
                        {
                            MemberUserIds = [.. group.MemberUserIds.Where(id => id != updated.Id)],
                            UpdatedAt = Now(),
                        }, ct);
                if (updated.EmailKey is not null)
                    await store.ReleaseUniqueAsync("user-email", updated.EmailKey, ct);
                if (updated.UsernameKey is not null)
                    await store.ReleaseUniqueAsync("user-username", updated.UsernameKey, ct);
                updated = updated with { EmailKey = null, UsernameKey = null };
            }
            else
            {
                var (restored, identifierFailure) = await ClaimMissingIdentifiersAsync(store, directory.InstanceId, updated, claimed, ct);
                if (identifierFailure is not null)
                {
                    // A later claim lost its race: undo the earlier ones, nothing was written.
                    foreach (var (kind, key) in claimed)
                        await store.ReleaseUniqueAsync(kind, key, ct);
                    return new(updated, identifierFailure);
                }
                updated = restored;
            }
            await store.UpsertUserAsync(updated, ct);
        }
        catch
        {
            foreach (var (kind, key) in claimed)
                await store.ReleaseUniqueAsync(kind, key, ct);
            throw;
        }

        foreach (var key in supersededEmailKeys.OfType<string>())
        {
            await store.ReleaseUniqueAsync("user-email", key, ct);
            // Pending verification links prove the OLD address; they must not verify the new one.
            await store.InvalidateVerificationTokensAsync(updated.Id, "verify_email", ct);
        }
        return new(updated, null);
    }

    internal sealed record ClaimResult(User User, IResult? Failure);

    /// <summary>Re-claims identifiers a deprovision released (the display strings survive
    /// on the user doc). Returns the failure result instead of throwing so a lost claim
    /// race maps to 409.</summary>
    internal static async Task<ClaimResult> ClaimMissingIdentifiersAsync(
        AuthStore store, string instanceId, User user, List<(string Kind, string Key)> claimed, CancellationToken ct)
    {
        if (user.Email is not null && user.EmailKey is null)
        {
            var key = $"{instanceId}:{user.Email}";
            try
            {
                await store.ClaimUniqueAsync("user-email", key, user.Id, ct);
            }
            catch (TerraScaleApiException)
            {
                return new(user, ScimError(409, "another user with this userName already exists", "uniqueness"));
            }
            claimed.Add(("user-email", key));
            user = user with { EmailKey = key };
        }
        if (user.Username is not null && user.UsernameKey is null)
        {
            var key = $"{instanceId}:{user.Username.ToLowerInvariant()}";
            try
            {
                await store.ClaimUniqueAsync("user-username", key, user.Id, ct);
            }
            catch (TerraScaleApiException)
            {
                return new(user, ScimError(409, "another user with this username already exists", "uniqueness"));
            }
            claimed.Add(("user-username", key));
            user = user with { UsernameKey = key };
        }
        return new(user, null);
    }

    /// <summary>Deprovisioning (deactivate) gets its own audit type; every other change is an update.</summary>
    internal static string AuditUpdateType(User before, User after) =>
        after.Banned && !before.Banned ? "directory.user_deprovisioned" : "directory.user_updated";

    internal static Task AuditAsync(AuthStore store, ScimDirectory directory, string type, Dictionary<string, object> data, CancellationToken ct)
    {
        data["directory_id"] = directory.Id;
        return store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = directory.InstanceId,
            Type = type,
            ActorId = directory.Id,
            Data = data,
            CreatedAt = Now(),
        }, ct);
    }

    internal static Task AuditManagementAsync(AuthStore store, InstanceContext context, string type, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            Type = type,
            ActorId = context.ApiKeyId,
            Data = data,
            CreatedAt = Now(),
        }, ct);

    /// <summary>Validates member references; null means the payload is invalid.</summary>
    internal static async Task<List<string>?> ParseMembersAsync(AuthStore store, ScimDirectory directory, List<ScimMember>? members, CancellationToken ct)
    {
        if (members is null or [])
            return [];
        var ids = new List<string>();
        foreach (var member in members)
        {
            if (string.IsNullOrWhiteSpace(member.Value))
                return null;
            var user = await store.GetUserAsync(member.Value, ct);
            if (user is null || user.InstanceId != directory.InstanceId || user.Deleted)
                return null;
            if (!ids.Contains(member.Value))
                ids.Add(member.Value);
        }
        return ids;
    }

    // ---- SCIM resource views ----------------------------------------------------------

    internal static Dictionary<string, object?> UserView(string directoryId, User user)
    {
        var view = new Dictionary<string, object?>
        {
            ["schemas"] = new[] { UserSchema },
            ["id"] = user.Id,
            ["userName"] = user.Email ?? user.Id,
            ["active"] = !user.Banned,
            ["meta"] = new { resourceType = "User", location = $"/scim/{directoryId}/Users/{user.Id}" },
        };
        if (ExternalId(user) is { } externalId)
            view["externalId"] = externalId;
        if (NameView(user.Name) is { } name)
            view["name"] = name;
        return view;
    }

    // The user record carries one free-text Name; SCIM's given/family split rides a
    // first-space split, so a family-only name round-trips as givenName.
    // ponytail: store explicit name parts if lossless round-trip ever matters.
    internal static Dictionary<string, object>? NameView(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var separator = name.IndexOf(' ');
        return separator < 0
            ? new Dictionary<string, object> { ["givenName"] = name }
            : new Dictionary<string, object> { ["givenName"] = name[..separator], ["familyName"] = name[(separator + 1)..] };
    }

    internal static string? JoinName(ScimName? name)
    {
        if (name is null)
            return null;
        var joined = string.Join(" ", new[] { name.GivenName?.Trim(), name.FamilyName?.Trim() }.Where(part => !string.IsNullOrEmpty(part)));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>The SCIM externalId we park in the user's private metadata; survives document round-trips as JsonElement.</summary>
    internal static string? ExternalId(User user) =>
        user.PrivateMetadata.TryGetValue("scimExternalId", out var value)
            ? value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value as string
            : null;

    internal static string? NormalizeExternalId(string? externalId) =>
        string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim();

    internal static Dictionary<string, object?> GroupView(string directoryId, ScimGroup group) => new()
    {
        ["schemas"] = new[] { GroupSchema },
        ["id"] = group.Id,
        ["displayName"] = group.DisplayName,
        ["members"] = group.MemberUserIds.Select(id => new Dictionary<string, object> { ["value"] = id }).ToList(),
        ["meta"] = new { resourceType = "Group", location = $"/scim/{directoryId}/Groups/{group.Id}" },
    };

    // ---- Request bodies ----------------------------------------------------------------

    /// <summary>RFC 7644 ListResponse — the capitalized "Resources" needs an explicit name;
    /// everything else the default camelCase policy already emits correctly.</summary>
    internal sealed record ScimListResponse(
        [property: JsonPropertyName("schemas")] string[] Schemas,
        [property: JsonPropertyName("totalResults")] int TotalResults,
        [property: JsonPropertyName("startIndex")] int StartIndex,
        [property: JsonPropertyName("itemsPerPage")] int ItemsPerPage,
        [property: JsonPropertyName("Resources")] IReadOnlyList<object> Resources);

    internal sealed record ScimName(string? GivenName, string? FamilyName);

    internal sealed record ScimUserBody(string? UserName, ScimName? Name, string? ExternalId, bool? Active);

    internal sealed record ScimMember(string? Value, string? Display);

    internal sealed record ScimGroupBody(string? DisplayName, List<ScimMember>? Members);

    internal sealed record ScimPatchOperation(string? Op, string? Path, JsonElement? Value);

    internal sealed record ScimPatchBody(List<ScimPatchOperation>? Operations);
}
