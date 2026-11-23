using Dapper;
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
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed record WebhookEndpointRecord(string InstanceId, string Id, string ProjectId, string Url, IReadOnlyList<string> EventTypes,
    string SecretBox, string? PreviousSecretBox = null, DateTimeOffset? PreviousSecretExpiresAt = null, DateTimeOffset CreatedAt = default)
{
    public string? Version { get; init; }
}

public sealed record WebhookEndpointStateRecord(string EndpointId, string ProjectId, DateTimeOffset? FirstFailureAt = null,
    DateTimeOffset? DisabledAt = null, DateTimeOffset LeaseUntil = default, DateTimeOffset LastServedAt = default)
{
    public string? Version { get; init; }
}

public sealed record CreateWebhookEndpointRequest(string? Url, IReadOnlyList<string>? EventTypes);

public sealed record WebhookEndpointBody(string Id, string Url, IReadOnlyList<string> EventTypes, DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Secret = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PreviousSecretExpiresAt = null, DateTimeOffset? DisabledAt = null);

public sealed record WebhookEndpointPageBody(IReadOnlyList<WebhookEndpointBody> Items, string? Next);

public sealed record WebhookSecretBody(string Id, string Secret, DateTimeOffset? PreviousSecretExpiresAt);

public sealed class WebhookEndpoints(AuthData data, ProjectAuthSettings settings, AuthOptions options, TimeProvider clock)
{
    public const int MaxPerProject = 3;

    public static readonly TimeSpan DualSecretWindow = TimeSpan.FromHours(24);

    public static readonly FrozenSet<string> LifecycleEvents = new[]
    {
        "apiKey.created", "apiKey.revoked", "agent.created", "agent.revoked", "agent.key_added", "agent.key_revoked", "user.created", "user.signed_up", "user.signed_in", "user.disabled", "user.enabled", "user.deleted", "user.provider_linked", "user.provider_unlinked", "user.updated", "session.created", "session.revoked", "webhook.endpoint_disabled", "impersonation.started", "impersonation.ended",
        "organization.created", "organization.updated", "organization.deleted", "organizationMembership.created", "organizationMembership.updated",
        "organizationMembership.deleted", "organizationInvitation.created", "organizationInvitation.accepted", "organizationInvitation.revoked", "organizationDomain.created", "organizationDomain.updated", "organizationDomain.deleted",
    }.ToFrozenSet(StringComparer.Ordinal);

    public async Task<(WebhookEndpointRecord Endpoint, string Secret)> CreateAsync(string projectId, string url, IReadOnlyList<string> eventTypes, CancellationToken cancellationToken)
    {
        var (current, settingsVersion) = await settings.ReadVersionedAsync(projectId, cancellationToken);
        if ((await this.ListAsync(projectId, cancellationToken)).Count >= MaxPerProject)
        {
            throw new ApiException(409, "webhook_endpoint_limit", $"A project registers at most {MaxPerProject} webhook endpoints.");
        }

        var id = AuthIds.New();
        var secret = NewSecret();
        var endpoint = new WebhookEndpointRecord(projectId, id, projectId, url, eventTypes,
            SecretBox.Seal(options.MasterKey, Encoding.UTF8.GetBytes(secret), Context(projectId, id)), CreatedAt: clock.GetUtcNow());
        if (!await data.SaveAsync(
            [
                Change.Insert(endpoint),
                Change.Insert(new WebhookEndpointStateRecord(id, projectId)),
                ProjectAuthSettings.Write(current with { InstanceId = projectId }, settingsVersion),
            ], cancellationToken))
        {
            throw new ApiException(409, "version_conflict", "The project's webhook endpoints changed concurrently; read them again and retry.");
        }

        return (endpoint, secret);
    }

    public async Task<IReadOnlyList<WebhookEndpointBody>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        await this.ListBodiesAsync(projectId, cancellationToken);

    private async Task<IReadOnlyList<WebhookEndpointBody>> ListBodiesAsync(string projectId, CancellationToken cancellationToken)
    {
        var endpoints = (await data.ReadAsync(connection => connection.QueryAsync<WebhookEndpointRecord>(
            "SELECT * FROM webhook_endpoints WHERE project_id = @projectId AND instance_id = @projectId ORDER BY created_at, id", new { projectId }), cancellationToken)).AsList();
        var states = (await data.ReadAsync(connection => connection.QueryAsync<WebhookEndpointStateRecord>(
            "SELECT * FROM webhook_endpoint_states WHERE project_id = @projectId", new { projectId }), cancellationToken))
            .ToDictionary(state => state.EndpointId, StringComparer.Ordinal);
        return [.. endpoints.Where(endpoint => states.ContainsKey(endpoint.Id))
            .Select(endpoint => new WebhookEndpointBody(endpoint.Id, endpoint.Url, endpoint.EventTypes, endpoint.CreatedAt, null, null, states[endpoint.Id].DisabledAt))];
    }

    public async Task<bool> DeleteAsync(string projectId, string id, CancellationToken cancellationToken)
    {
        if (await this.ReadAsync(projectId, id, cancellationToken) is not { } row)
        {
            return false;
        }

        return await data.SaveAsync(Change.Delete(row), cancellationToken);
    }

    public async Task<(WebhookEndpointRecord Endpoint, string Secret)> RotateAsync(string projectId, string id,
        IReadOnlyList<Change> consumption, CancellationToken cancellationToken)
    {
        var row = await this.ReadAsync(projectId, id, cancellationToken) ?? throw new ApiException(404, "not_found", "No such resource.");
        var endpoint = row.Value;
        var secret = NewSecret();
        var rotated = endpoint with
        {
            SecretBox = SecretBox.Seal(options.MasterKey, Encoding.UTF8.GetBytes(secret), Context(projectId, id)),
            PreviousSecretBox = endpoint.SecretBox,
            PreviousSecretExpiresAt = clock.GetUtcNow() + DualSecretWindow,
        };
        if (!await data.SaveAsync([.. consumption, Change.Update(row, rotated)], cancellationToken))
        {
            throw (await this.ReadAsync(projectId, id, cancellationToken))?.Version == row.Version
                ? StepUpProofs.Required()
                : new ApiException(409, "version_conflict", "The webhook endpoint changed; read it again and retry.");
        }

        return (rotated, secret);
    }

    private async Task<Versioned<WebhookEndpointRecord>?> ReadAsync(string projectId, string id, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<WebhookEndpointRecord>(
            "SELECT * FROM webhook_endpoints WHERE id = @id AND project_id = @projectId AND instance_id = @projectId", new { id, projectId }), cancellationToken));

    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    internal static string Context(string projectId, string id) => $"webhook-endpoint:{projectId}:{id}";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CreateWebhookEndpointRequest))]
[JsonSerializable(typeof(WebhookEndpointBody))]
[JsonSerializable(typeof(WebhookEndpointPageBody))]
[JsonSerializable(typeof(WebhookSecretBody))]
[JsonSerializable(typeof(WebhookEventBody))]
[JsonSerializable(typeof(WebhookEventPage))]
[JsonSerializable(typeof(WebhookDeliveryPage))]
internal sealed partial class WebhooksJson : JsonSerializerContext;
