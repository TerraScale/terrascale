namespace TerraScale.Auth.Management;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Auth.Api;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

internal static class ManagementSupport
{
    // ---- Shared plumbing ------------------------------------------------------------

    /// <summary>Authentication gate; exactly one of <see cref="InstanceGate.Context"/>/<see cref="InstanceGate.Failure"/> is set.
    /// Rate limiting happens upstream in <see cref="ManagementGate"/>.</summary>
    internal sealed record InstanceGate(InstanceContext? Context, IResult? Failure);

    internal static async Task<InstanceGate> RequireInstanceAsync(
        HttpRequest request, string slug, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        return context is not null && context.Instance.Slug == slug
            ? new(context, null)
            : new(null, ApiResults.Unauthorized());
    }

    internal static async Task<(TBody? Body, IResult? Failure)> ReadBodyAsync<TBody>(HttpRequest request, CancellationToken ct) where TBody : class
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

    internal static Task AuditAsync(AuthStore store, InstanceContext context, string type, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            Type = type,
            ActorId = context.ApiKeyId,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);

    internal static async Task<User?> FindLiveUserAsync(AuthStore store, string instanceId, string userId, CancellationToken ct)
    {
        var user = await store.GetUserAsync(userId, ct);
        return user is null || user.InstanceId != instanceId || user.Deleted ? null : user;
    }

    internal static Dictionary<string, object?> UserView(User user, bool includePrivate)
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
}
