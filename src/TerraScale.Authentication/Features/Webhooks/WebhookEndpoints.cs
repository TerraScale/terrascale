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

public sealed record WebhookEndpointRecord(string InstanceId, string Id, string ProjectId, string Url, IReadOnlyList<string> EventTypes,
    string SecretBox, string? PreviousSecretBox = null, DateTimeOffset? PreviousSecretExpiresAt = null, DateTimeOffset CreatedAt = default);

public sealed record CreateWebhookEndpointRequest(string? Url, IReadOnlyList<string>? EventTypes);

public sealed record WebhookEndpointBody(string Id, string Url, IReadOnlyList<string> EventTypes, DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Secret = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PreviousSecretExpiresAt = null);

public sealed record WebhookEndpointPageBody(IReadOnlyList<WebhookEndpointBody> Items, string? Next);

public sealed record WebhookSecretBody(string Id, string Secret, DateTimeOffset? PreviousSecretExpiresAt);

public sealed class WebhookEndpoints(AuthStore store, AuthOptions options, TimeProvider clock)
{
    public const int MaxPerProject = 3;

    public static readonly TimeSpan DualSecretWindow = TimeSpan.FromHours(24);

    public static readonly FrozenSet<string> LifecycleEvents = new[]
    {
        "user.signed_up", "user.signed_in", "user.disabled", "user.enabled", "user.deleted", "user.provider_linked", "user.provider_unlinked",
    }.ToFrozenSet(StringComparer.Ordinal);

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

    public async Task<bool> DeleteAsync(string projectId, string id, CancellationToken cancellationToken)
    {
        var row = await store.GetAsync(AuthStore.WebhookEndpointTable, projectId, id, cancellationToken).ConfigureAwait(false);
        return row is not null && row.Body.Deserialize(WebhookStoreJson.Default.WebhookEndpointRecord) is { ProjectId: var owner } && owner == projectId
            && await store.CompareAndDeleteAsync(AuthStore.WebhookEndpointTable, projectId, id, row.Version, cancellationToken).ConfigureAwait(false);
    }

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
