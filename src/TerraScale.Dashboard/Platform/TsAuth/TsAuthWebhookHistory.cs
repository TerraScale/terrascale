using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth;

public sealed record TsAuthWebhookEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("data")] TsAuthWebhookEventData Data);

public sealed record TsAuthWebhookEventData(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("endpoint_id")] string? EndpointId);

public sealed record TsAuthWebhookAttempt(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("latency_ms")] int LatencyMs,
    [property: JsonPropertyName("response_code")] int? ResponseCode,
    [property: JsonPropertyName("response_body")] string? ResponseBody);

public sealed record TsAuthWebhookDelivery(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("next_attempt_at")] DateTimeOffset NextAttemptAt,
    [property: JsonPropertyName("recent_attempts")] IReadOnlyList<TsAuthWebhookAttempt> RecentAttempts);

public sealed partial class TsAuthProjectsClient
{
    public Task<TsAuthPlatformPage<TsAuthWebhookEvent>> ListEventsAsync(string accessToken, string projectId, string? after, CancellationToken cancellationToken) =>
        this.SendAsync(HttpMethod.Get, accessToken, projectId, "/events" + PageQuery("q", null, null, after),
            Json.TsAuthPlatformPageTsAuthWebhookEvent, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthPlatformPage<TsAuthWebhookDelivery>> ListWebhookDeliveriesAsync(string accessToken, string projectId, string endpointId, CancellationToken cancellationToken) =>
        this.SendAsync(HttpMethod.Get, accessToken, projectId, "/webhook-endpoints/" + Id(endpointId) + "/deliveries",
            Json.TsAuthPlatformPageTsAuthWebhookDelivery, HttpStatusCode.OK, cancellationToken);

    public Task<TsAuthWebhookEvent> TestWebhookAsync(string accessToken, string projectId, string endpointId, CancellationToken cancellationToken) =>
        this.SendAsync(HttpMethod.Post, accessToken, projectId, "/webhook-endpoints/" + Id(endpointId) + "/test",
            Json.TsAuthWebhookEvent, HttpStatusCode.OK, cancellationToken);

    public Task ReplayWebhookDeliveryAsync(string accessToken, string projectId, string endpointId, string deliveryId, CancellationToken cancellationToken) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route(projectId, "/webhook-endpoints/" + Id(endpointId) + "/deliveries/" + Id(deliveryId) + "/replay"),
            null, Json.TsAuthPlatformName, TsAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);
}
