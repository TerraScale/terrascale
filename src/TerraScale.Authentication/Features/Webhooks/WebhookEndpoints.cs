using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed record WebhookEndpointRecord(string InstanceId, string Id, string ProjectId, string Url, IReadOnlyList<string> EventTypes,
    string SecretBox, string? PreviousSecretBox = null, DateTimeOffset? PreviousSecretExpiresAt = null, DateTimeOffset CreatedAt = default);

internal sealed record WebhookEndpointCounter(string InstanceId, string ProjectId, int Count);

public sealed record CreateWebhookEndpointRequest(string? Url, IReadOnlyList<string>? EventTypes);

public sealed record WebhookEndpointBody(string Id, string Url, IReadOnlyList<string> EventTypes, DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Secret = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PreviousSecretExpiresAt = null);

public sealed record WebhookEndpointPageBody(IReadOnlyList<WebhookEndpointBody> Items, string? Next);

public sealed record WebhookSecretBody(string Id, string Secret, DateTimeOffset? PreviousSecretExpiresAt);

public sealed class WebhookEndpoints(AuthStore store, ProjectAuthSettings settings, AuthOptions options, TimeProvider clock)
{
    public const int MaxPerProject = 3;

    public static readonly TimeSpan DualSecretWindow = TimeSpan.FromHours(24);

    public static readonly FrozenSet<string> LifecycleEvents = new[]
    {
        "user.signed_up", "user.signed_in", "user.disabled", "user.enabled", "user.deleted", "user.provider_linked", "user.provider_unlinked",
    }.ToFrozenSet(StringComparer.Ordinal);

    public async Task<(WebhookEndpointRecord Endpoint, string Secret)> CreateAsync(string projectId, string url, IReadOnlyList<string> eventTypes, CancellationToken cancellationToken)
    {
        var (counter, counterVersion) = await this.CounterAsync(projectId, cancellationToken).ConfigureAwait(false);
        var count = counter?.Count ?? (await this.ListAsync(projectId, cancellationToken).ConfigureAwait(false)).Count;
        if (count >= MaxPerProject)
        {
            throw new ApiException(409, "webhook_endpoint_limit", $"A project registers at most {MaxPerProject} webhook endpoints.");
        }

        var id = AuthStore.NewId();
        var secret = NewSecret();
        var endpoint = new WebhookEndpointRecord(projectId, id, projectId, url, eventTypes,
            SecretBox.Seal(options.MasterKey, Encoding.UTF8.GetBytes(secret), Context(projectId, id)), CreatedAt: clock.GetUtcNow());
        var (current, settingsVersion) = await settings.ReadVersionedAsync(projectId, cancellationToken).ConfigureAwait(false);
        HashSet<string> subscribed = [.. current.SubscribedWebhookEvents ?? (IEnumerable<string>)[], .. eventTypes];
        List<StoreOperation> operations =
        [
            counterVersion is null
                ? AuthStore.Create(AuthStore.WebhookEndpointCounterTable, projectId, projectId, Counter(projectId, count + 1))
                : AuthStore.Replace(AuthStore.WebhookEndpointCounterTable, projectId, projectId, Counter(projectId, counter!.Count + 1), counterVersion),
            AuthStore.Create(AuthStore.WebhookEndpointTable, projectId, id,
                JsonSerializer.SerializeToElement(endpoint, WebhookStoreJson.Default.WebhookEndpointRecord)),
            settingsVersion is null
                ? AuthStore.Create(AuthStore.ProjectAuthSettingsTable, projectId, projectId,
                    ProjectAuthSettings.Stored(current with { InstanceId = projectId, SubscribedWebhookEvents = [.. subscribed.Order()] }))
                : AuthStore.Replace(AuthStore.ProjectAuthSettingsTable, projectId, projectId,
                    ProjectAuthSettings.Stored(current with { InstanceId = projectId, SubscribedWebhookEvents = [.. subscribed.Order()] }), settingsVersion),
        ];
        if (await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new ApiException(409, "version_conflict", "The project's webhook endpoints changed concurrently; read them again and retry.");
        }

        return (endpoint, secret);
    }

    private async Task<(WebhookEndpointCounter? Counter, string? Version)> CounterAsync(string projectId, CancellationToken cancellationToken)
    {
        var row = await store.GetAsync(AuthStore.WebhookEndpointCounterTable, projectId, projectId, cancellationToken).ConfigureAwait(false);
        return row?.Body.Deserialize(WebhookStoreJson.Default.WebhookEndpointCounter) is { ProjectId: var owner } counter && owner == projectId
            ? (counter, row.Version) : (null, null);
    }

    private static JsonElement Counter(string projectId, int count) =>
        JsonSerializer.SerializeToElement(new WebhookEndpointCounter(projectId, projectId, count), WebhookStoreJson.Default.WebhookEndpointCounter);

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
        if (row is null || row.Body.Deserialize(WebhookStoreJson.Default.WebhookEndpointRecord) is not { ProjectId: var owner } endpoint || owner != projectId)
        {
            return false;
        }

        var (counter, counterVersion) = await this.CounterAsync(projectId, cancellationToken).ConfigureAwait(false);
        var endpoints = await this.ListAsync(projectId, cancellationToken).ConfigureAwait(false);
        var remainingCount = counterVersion is null ? endpoints.Count - 1 : counter!.Count - 1;
        List<StoreOperation> operations =
        [
            AuthStore.Delete(AuthStore.WebhookEndpointTable, projectId, id, row.Version),
            counterVersion is null
                ? AuthStore.Create(AuthStore.WebhookEndpointCounterTable, projectId, projectId, Counter(projectId, remainingCount))
                : AuthStore.Replace(AuthStore.WebhookEndpointCounterTable, projectId, projectId, Counter(projectId, remainingCount), counterVersion),
        ];

        var (current, settingsVersion) = await settings.ReadVersionedAsync(projectId, cancellationToken).ConfigureAwait(false);
        HashSet<string> subscribed = [.. endpoints.Where(other => other.Id != id).SelectMany(other => other.EventTypes)];
        if (!subscribed.SetEquals(current.SubscribedWebhookEvents ?? (IEnumerable<string>)[]))
        {
            var updated = current with
            {
                InstanceId = projectId,
                SubscribedWebhookEvents = subscribed.Count == 0 ? null : [.. subscribed.Order()],
            };
            operations.Add(settingsVersion is null
                ? AuthStore.Create(AuthStore.ProjectAuthSettingsTable, projectId, projectId, ProjectAuthSettings.Stored(updated))
                : AuthStore.Replace(AuthStore.ProjectAuthSettingsTable, projectId, projectId, ProjectAuthSettings.Stored(updated), settingsVersion));
        }

        return await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null;
    }

    public async Task<(WebhookEndpointRecord Endpoint, string Secret)> RotateAsync(string projectId, string id,
        IReadOnlyList<StoreOperation> consumption, CancellationToken cancellationToken)
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
        List<StoreOperation> operations =
        [
            .. consumption,
            AuthStore.Replace(AuthStore.WebhookEndpointTable, projectId, id,
                JsonSerializer.SerializeToElement(rotated, WebhookStoreJson.Default.WebhookEndpointRecord), row.Version),
        ];
        if (await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            throw failed < operations.Count - 1
                ? StepUpProofs.Required()
                : new ApiException(409, "version_conflict", "The webhook endpoint changed; read it again and retry.");
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
[JsonSerializable(typeof(WebhookEndpointCounter))]
internal sealed partial class WebhookStoreJson : JsonSerializerContext;
