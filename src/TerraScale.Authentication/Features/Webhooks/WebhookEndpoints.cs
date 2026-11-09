using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.Webhooks;

/// <summary>
/// A registered end-user lifecycle webhook endpoint (table <c>webhook_endpoints</c>). The signing secret is sealed
/// under the master key and bound to the project and endpoint; the previous secret stays valid for its dual window.
/// </summary>
/// <param name="InstanceId">The project.</param>
/// <param name="Id">The endpoint's ULID.</param>
/// <param name="ProjectId">The project the endpoint belongs to.</param>
/// <param name="Url">The https URL deliveries are posted to.</param>
/// <param name="EventTypes">The lifecycle events subscribed to.</param>
/// <param name="SecretBox">The sealed signing secret.</param>
/// <param name="PreviousSecretBox">The sealed previous secret during rotation, or null.</param>
/// <param name="PreviousSecretExpiresAt">When the previous secret stops verifying, or null.</param>
/// <param name="CreatedAt">Registration time.</param>
public sealed record WebhookEndpointRecord(string InstanceId, string Id, string ProjectId, string Url, IReadOnlyList<string> EventTypes,
    string SecretBox, string? PreviousSecretBox = null, DateTimeOffset? PreviousSecretExpiresAt = null, DateTimeOffset CreatedAt = default);

/// <summary><c>POST /v1/{project}/webhook-endpoints</c>.</summary>
public sealed record CreateWebhookEndpointRequest(string? Url, IReadOnlyList<string>? EventTypes);

/// <summary>A registered webhook endpoint; <c>secret</c> only in the answer that created or rotated it.</summary>
public sealed record WebhookEndpointBody(string Id, string Url, IReadOnlyList<string> EventTypes, DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Secret = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PreviousSecretExpiresAt = null);

/// <summary>The project's webhook endpoints, in one page; never with secrets.</summary>
public sealed record WebhookEndpointPageBody(IReadOnlyList<WebhookEndpointBody> Items, string? Next);

/// <summary>The answer of a secret rotation; the new secret is never answered again.</summary>
public sealed record WebhookSecretBody(string Id, string Secret, DateTimeOffset? PreviousSecretExpiresAt);

/// <summary>Registration and rotation of the project's lifecycle webhook endpoints.</summary>
public sealed class WebhookEndpoints(AuthStore store, AuthOptions options, TimeProvider clock)
{
    /// <summary>The most endpoints one project may register.</summary>
    public const int MaxPerProject = 3;

    /// <summary>The dual-secret window after a rotation.</summary>
    public static readonly TimeSpan DualSecretWindow = TimeSpan.FromHours(24);

    /// <summary>Every deliverable lifecycle event.</summary>
    public static readonly FrozenSet<string> LifecycleEvents = new[]
    {
        "user.signed_up", "user.signed_in", "user.disabled", "user.enabled", "user.deleted", "user.provider_linked", "user.provider_unlinked",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Registers an endpoint and answers its signing secret exactly once.</summary>
    public async Task<(WebhookEndpointRecord Endpoint, string Secret)> CreateAsync(string projectId, string url, IReadOnlyList<string> eventTypes, CancellationToken cancellationToken)
    {
        if (await store.CountAsync(AuthStore.WebhookEndpointTable, projectId, cancellationToken).ConfigureAwait(false) >= MaxPerProject)
        {
            throw new ApiException(409, "webhook_endpoint_limit", $"A project registers at most {MaxPerProject} webhook endpoints.");
        }

        var id = AuthStore.NewId();
        var secret = NewSecret();
        var endpoint = new WebhookEndpointRecord(projectId, id, projectId, url, eventTypes,
            SecretBox.Seal(options.MasterKey, Encoding.UTF8.GetBytes(secret), Context(projectId, id)), CreatedAt: clock.GetUtcNow());
        await store.CreateAsync(AuthStore.WebhookEndpointTable, projectId, id, JsonSerializer.SerializeToElement(endpoint, WebhookStoreJson.Default.WebhookEndpointRecord), cancellationToken).ConfigureAwait(false);
        return (endpoint, secret);
    }

    /// <summary>The project's endpoints, oldest first.</summary>
    public async Task<IReadOnlyList<WebhookEndpointRecord>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        List<WebhookEndpointRecord> endpoints = [];
        await foreach (var row in store.QueryAsync(AuthStore.WebhookEndpointTable, projectId, "/instanceId", projectId, cancellationToken).ConfigureAwait(false))
        {
            if (row.Body.Deserialize(WebhookStoreJson.Default.WebhookEndpointRecord) is { InstanceId: var instanceId } endpoint && instanceId == projectId)
            {
                endpoints.Add(endpoint);
            }
        }

        return [.. endpoints.OrderBy(endpoint => endpoint.CreatedAt)];
    }

    /// <summary>Deletes the endpoint; false when the project has no such endpoint.</summary>
    public async Task<bool> DeleteAsync(string projectId, string id, CancellationToken cancellationToken)
    {
        var row = await store.GetAsync(AuthStore.WebhookEndpointTable, projectId, id, cancellationToken).ConfigureAwait(false);
        return row is not null && row.Body.Deserialize(WebhookStoreJson.Default.WebhookEndpointRecord) is { ProjectId: var owner } && owner == projectId
            && await store.CompareAndDeleteAsync(AuthStore.WebhookEndpointTable, projectId, id, row.Version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rotates the signing secret and answers the new one exactly once; the old one verifies for the dual window.</summary>
    public async Task<(WebhookEndpointRecord Endpoint, string Secret)> RotateAsync(string projectId, string id, CancellationToken cancellationToken)
    {
        var row = await store.GetAsync(AuthStore.WebhookEndpointTable, projectId, id, cancellationToken).ConfigureAwait(false);
        if (row is null || row.Body.Deserialize(WebhookStoreJson.Default.WebhookEndpointRecord) is not { ProjectId: var owner } endpoint || owner != projectId)
        {
            throw new ApiException(404, "not_found", "No such resource.");
        }

        var secret = NewSecret();
        var rotated = endpoint with
        {
            SecretBox = SecretBox.Seal(options.MasterKey, Encoding.UTF8.GetBytes(secret), Context(projectId, id)),
            PreviousSecretBox = endpoint.SecretBox,
            PreviousSecretExpiresAt = clock.GetUtcNow() + DualSecretWindow,
        };
        if (!await store.CompareAndPutAsync(AuthStore.WebhookEndpointTable, projectId, id,
            JsonSerializer.SerializeToElement(rotated, WebhookStoreJson.Default.WebhookEndpointRecord), row.Version, cancellationToken).ConfigureAwait(false))
        {
            throw ApiException.Unavailable();
        }

        return (rotated, secret);
    }

    /// <summary>A fresh signing secret: <c>whsec_</c> plus base64 of 32 random bytes.</summary>
    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string Context(string projectId, string id) => $"webhook-endpoint:{projectId}:{id}";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CreateWebhookEndpointRequest))]
[JsonSerializable(typeof(WebhookEndpointBody))]
[JsonSerializable(typeof(WebhookEndpointPageBody))]
[JsonSerializable(typeof(WebhookSecretBody))]
internal sealed partial class WebhooksJson : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebhookEndpointRecord))]
internal sealed partial class WebhookStoreJson : JsonSerializerContext;
