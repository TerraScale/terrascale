namespace TerraScale.Auth.Management;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Auth.Api;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.Endpoints;
using static TerraScale.Auth.Management.ManagementSupport;

internal sealed class UpdateUserEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPatch("/v1/{slug}/users/{userId}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
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
    }

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
}
