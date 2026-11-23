using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseRequestLog(string Id, DateTimeOffset Time, string Region, string Method, string Route,
    int Status, double LatencyMs, long Operations, bool IsQuery, string? RequestId = null, long Statements = 0, long StatementErrors = 0)
{
    public string Id { get; init; } = TerraScaleIds.IsValid(Id) ? Id : throw new JsonException("Invalid request-log ULID.");
}

public sealed record TerraBaseRequestLogs(string DatabaseId, IReadOnlyList<TerraBaseRequestLog> Items,
    bool Truncated, bool RecordingLimited)
{
    public string DatabaseId { get; init; } = TerraScaleIds.IsValid(DatabaseId) ? DatabaseId : throw new JsonException("Invalid request-log database ULID.");
}

public sealed partial class TerraBaseDatabase
{
    public Task<TerraBaseRequestLogs> ListRequestLogsAsync(string? status = null, CancellationToken cancellationToken = default)
    {
        if (status is not (null or "" or "2xx" or "3xx" or "4xx" or "5xx"))
            throw new ArgumentException("Request-log status must be 2xx, 3xx, 4xx or 5xx, or omitted.", nameof(status));
        return connection.ExchangeAsync(path + "/logs" + TerraBaseConnection.Query(("status", status)), null, null, async opened =>
        {
            var response = opened.Response;
            if (!response.IsSuccessStatusCode)
                throw await TerraBaseException.FromResponseAsync(response, null, opened.Token).ConfigureAwait(false);
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
