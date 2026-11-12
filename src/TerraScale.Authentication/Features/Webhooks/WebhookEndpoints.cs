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
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed record WebhookEndpointRecord(string InstanceId, string Id, string ProjectId, string Url, IReadOnlyList<string> EventTypes,
    string SecretBox, string? PreviousSecretBox = null, DateTimeOffset? PreviousSecretExpiresAt = null, DateTimeOffset CreatedAt = default, DateTimeOffset? FirstFailureAt = null, DateTimeOffset? DisabledAt = null, DateTimeOffset LeaseUntil = default, DateTimeOffset LastServedAt = default);

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
        "user.created", "user.signed_up", "user.signed_in", "user.disabled", "user.enabled", "user.deleted", "user.provider_linked", "user.provider_unlinked", "user.updated", "session.created", "session.revoked", "webhook.endpoint_disabled",
    }.ToFrozenSet(StringComparer.Ordinal);

    public async Task<(WebhookEndpointRecord Endpoint, string Secret)> CreateAsync(string projectId, string url, IReadOnlyList<string> eventTypes, CancellationToken cancellationToken)
    {
        var (current, settingsVersion) = await settings.ReadVersionedAsync(projectId, cancellationToken).ConfigureAwait(false);
        if ((await this.ListAsync(projectId, cancellationToken).ConfigureAwait(false)).Count >= MaxPerProject)
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
                ProjectAuthSettings.Write(current with { InstanceId = projectId }, settingsVersion),
            ], cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "version_conflict", "The project's webhook endpoints changed concurrently; read them again and retry.");
        }

        return (endpoint, secret);
    }

    public async Task<IReadOnlyList<WebhookEndpointRecord>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.WebhookEndpoints.Where(endpoint => endpoint.ProjectId == projectId && endpoint.InstanceId == projectId)
            .OrderBy(endpoint => endpoint.CreatedAt).ThenBy(endpoint => endpoint.Id).ToListAsync(token), cancellationToken).ConfigureAwait(false);

    public async Task<bool> DeleteAsync(string projectId, string id, CancellationToken cancellationToken)
    {
        if (await this.ReadAsync(projectId, id, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return false;
        }

        return await data.SaveAsync(Change.Delete(row), cancellationToken).ConfigureAwait(false);
    }

    public async Task<(WebhookEndpointRecord Endpoint, string Secret)> RotateAsync(string projectId, string id,
        IReadOnlyList<Change> consumption, CancellationToken cancellationToken)
    {
        var row = await this.ReadAsync(projectId, id, cancellationToken).ConfigureAwait(false) ?? throw new ApiException(404, "not_found", "No such resource.");
        var endpoint = row.Value;
        var secret = NewSecret();
        var rotated = endpoint with
        {
            SecretBox = SecretBox.Seal(options.MasterKey, Encoding.UTF8.GetBytes(secret), Context(projectId, id)),
            PreviousSecretBox = endpoint.SecretBox,
            PreviousSecretExpiresAt = clock.GetUtcNow() + DualSecretWindow,
        };
        if (!await data.SaveAsync([.. consumption, Change.Update(row, rotated)], cancellationToken).ConfigureAwait(false))
        {
            throw (await this.ReadAsync(projectId, id, cancellationToken).ConfigureAwait(false))?.Version == row.Version
                ? StepUpProofs.Required()
                : new ApiException(409, "version_conflict", "The webhook endpoint changed; read it again and retry.");
        }

        return (rotated, secret);
    }

    private Task<Versioned<WebhookEndpointRecord>?> ReadAsync(string projectId, string id, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.WebhookEndpoints.Where(endpoint => endpoint.Id == id && endpoint.ProjectId == projectId && endpoint.InstanceId == projectId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);

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
