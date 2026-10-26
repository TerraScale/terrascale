using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A completed authorized request observation; Route is a template, never a URL or query text.</summary>
/// <param name="Id">Canonical observation ULID.</param>
/// <param name="Time">Request observation time.</param>
/// <param name="Region">Region that recorded the request.</param>
/// <param name="Method">HTTP method, or OTHER.</param>
/// <param name="Route">Redacted route template, without document keys or credentials.</param>
/// <param name="Status">HTTP response status.</param>
/// <param name="LatencyMs">Elapsed milliseconds.</param>
/// <param name="Operations">Committed document operations.</param>
/// <param name="IsQuery">Whether the route is a query or SQL request.</param>
/// <param name="RequestId">Correlation identifier, or null when unavailable.</param>
public sealed record TerraBaseRequestLog(string Id, DateTimeOffset Time, string Region, string Method, string Route,
    int Status, double LatencyMs, long Operations, bool IsQuery, string? RequestId = null)
{
    /// <summary>Canonical observation ULID.</summary>
    public string Id { get; init; } = TerraScaleIds.IsValid(Id) ? Id : throw new JsonException("Invalid request-log ULID.");
}

/// <summary>The newest 100 observations within 24 hours; recording is best effort, without a continuation cursor.</summary>
/// <param name="DatabaseId">Canonical database ULID.</param>
/// <param name="Items">Newest-first retained observations.</param>
/// <param name="Truncated">Whether the recent window was full, including before a status filter.</param>
/// <param name="RecordingLimited">Whether this server has omitted observations at capacity since startup.</param>
public sealed record TerraBaseRequestLogs(string DatabaseId, IReadOnlyList<TerraBaseRequestLog> Items,
    bool Truncated, bool RecordingLimited)
{
    /// <summary>Canonical database ULID.</summary>
    public string DatabaseId { get; init; } = TerraScaleIds.IsValid(DatabaseId) ? DatabaseId : throw new JsonException("Invalid request-log database ULID.");
}

public sealed partial class TerraBaseDatabase
{
    /// <summary>
    /// Reads recent best-effort request observations using databases:read project authority.
    /// The newest 100 requests within 24 hours are retained; filtering does not widen that window.
    /// Sent once with existing response bounds and deadline; no polling, cursor or durable-export guarantee.
    /// </summary>
    /// <param name="status">Optional HTTP status class: 2xx, 3xx, 4xx or 5xx; null or empty reads all classes.</param>
    /// <param name="cancellationToken">Cancels acquisition and response reading.</param>
    public Task<TerraBaseRequestLogs> ListRequestLogsAsync(string? status = null, CancellationToken cancellationToken = default)
    {
        if (status is not (null or "" or "2xx" or "3xx" or "4xx" or "5xx"))
            throw new ArgumentException("Request-log status must be 2xx, 3xx, 4xx or 5xx, or omitted.", nameof(status));
        return connection.ExchangeAsync(path + "/logs" + TerraBaseConnection.Query(("status", status)), null, null, async opened =>
        {
            var response = opened.Response;
            if (!response.IsSuccessStatusCode)
                throw await TerraBaseException.FromResponseAsync(response, opened.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw InvalidRequestLogs(response.StatusCode);
            var bytes = await connection.ReadBodyAsync(response, opened.Token).ConfigureAwait(false);
            var logs = TerraBaseConnection.ReadResponse(bytes, TerraBaseRequestLogsJsonContext.Default.TerraBaseRequestLogs, response.StatusCode);
            if (logs.Items.Count > 100 || logs.Items.Any(item => item is null || string.IsNullOrEmpty(item.Region)
                || string.IsNullOrEmpty(item.Method) || string.IsNullOrEmpty(item.Route) || item.Status is < 100 or > 599
                || !double.IsFinite(item.LatencyMs) || item.LatencyMs < 0 || item.Operations < 0))
                throw InvalidRequestLogs(response.StatusCode);
            return logs;
        }, cancellationToken);
    }

    private static TerraBaseException InvalidRequestLogs(HttpStatusCode status) =>
        new(status, "TerraBase returned invalid request-log observations.", "invalid_response", null);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = TerraBaseJsonContext.MaxResponseDepth,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(TerraBaseRequestLogs))]
internal sealed partial class TerraBaseRequestLogsJsonContext : JsonSerializerContext;
