using System.Net;
using System.Net.Http.Headers;

namespace TerraScale.DotNet.TerraBase;

/// <summary>An owned invocation response, readable after the HTTP response is disposed; never assumes JSON.</summary>
/// <param name="StatusCode">Original HTTP status.</param>
/// <param name="ContentType">Original media type and parameters, or null when absent.</param>
/// <param name="Body">Owned response bytes, bounded by the client's response limit.</param>
public sealed record TerraBaseFunctionResponse(HttpStatusCode StatusCode, string? ContentType, ReadOnlyMemory<byte> Body);

/// <summary>Any non-2xx invocation response, including handler failures, redirects and 401/403 auth refusals.</summary>
public sealed class TerraBaseFunctionInvocationException : TerraBaseException
{
    internal TerraBaseFunctionInvocationException(TerraBaseFunctionResponse response, TerraBaseException error)
        : base(response.StatusCode, $"Function invocation answered HTTP {(int)response.StatusCode}; inspect Response.", error.ErrorCode, error.OperationIndex) => Response = response;

    /// <summary>The original status, media type and complete bounded body, including non-JSON handler errors.</summary>
    public TerraBaseFunctionResponse Response { get; }
}

/// <summary>One invocation log, retained among the latest 100; newest entries are listed first.</summary>
/// <param name="Id">Invocation ULID.</param>
/// <param name="At">Start time.</param>
/// <param name="Trigger">http, change or cron.</param>
/// <param name="Status">ok or error.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
/// <param name="Lines">At most 50 console lines, each at most 1 KiB.</param>
/// <param name="Error">Runtime error message, or null.</param>
public sealed record TerraBaseFunctionLog(string Id, DateTimeOffset At, string Trigger, string Status,
    long DurationMs, IReadOnlyList<string> Lines, string? Error = null);

public sealed partial class TerraBaseDatabase
{
    /// <summary>
    /// Invokes once with documents:read. Body is at most 1 MiB: parsed JSON becomes event.body, otherwise UTF-8
    /// text; empty input becomes null. Query values become event.query. Every 2xx returns raw bytes, including
    /// empty 204; every other status throws TerraBaseFunctionInvocationException preserving its raw response
    /// and any JSON error code (including 500 function_error). Auth refusals also throw. Redirects must be disabled.
    /// Transport failures, deadlines, response limits and cancellation throw normally; the invocation may have run.
    /// </summary>
    public Task<TerraBaseFunctionResponse> InvokeFunctionAsync(string functionName, ReadOnlyMemory<byte> body = default,
        IReadOnlyDictionary<string, string>? query = null, CancellationToken cancellationToken = default)
    {
        if (body.Length > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(body), "Function input is at most 1 MiB.");
        var route = FunctionPath(functionName) + "/invoke" + TerraBaseConnection.Query(
            query?.Select(pair => (Uri.EscapeDataString(pair.Key), (string?)pair.Value)).ToArray() ?? []);
        var content = new ByteArrayContent(body.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return connection.ExchangeAsync(HttpMethod.Post, route, content, null, null, async opened =>
        {
            var response = opened.Response;
            var bytes = await connection.ReadBodyAsync(response, opened.Token).ConfigureAwait(false);
            var result = new TerraBaseFunctionResponse(response.StatusCode, response.Content.Headers.ContentType?.ToString(), bytes);
            if (!response.IsSuccessStatusCode)
            {
                using var buffered = new HttpResponseMessage(response.StatusCode) { Content = new ByteArrayContent(bytes) };
                throw new TerraBaseFunctionInvocationException(result,
                    await TerraBaseException.FromResponseAsync(buffered, opened.Token).ConfigureAwait(false));
            }
            return result;
        }, cancellationToken);
    }

    /// <summary>Lists invocation logs, newest first, with both documents:read and documents:write. Limit 1–100, default 50; no cursor.</summary>
    public async Task<IReadOnlyList<TerraBaseFunctionLog>> ListFunctionLogsAsync(string functionName, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Log limit must be 1–100.");
        var response = await connection.SendAsync(HttpMethod.Get, FunctionPath(functionName) + "/logs" +
            TerraBaseConnection.Query(("limit", TerraBaseConnection.Number(limit))), null,
            Json.IReadOnlyListTerraBaseFunctionLog, null, cancellationToken).ConfigureAwait(false);
        if (response.Value.Any(log => log is null || log.Lines.Any(line => line is null)))
            throw new TerraBaseException(response.Status, "Function logs contain invalid items.", "invalid_response", null);
        return response.Value;
    }

    /// <summary>
    /// Lists up to 100 trigger deliveries in ascending ID order; empty without a trigger. Requires both
    /// documents:read and documents:write. Pending attempts/errorCode form the dead-letter view; no cursor.
    /// </summary>
    public async Task<IReadOnlyList<TerraBaseChangeWebhookDelivery>> ListFunctionDeliveriesAsync(string functionName,
        CancellationToken cancellationToken = default)
    {
        var response = await connection.SendAsync(HttpMethod.Get, FunctionPath(functionName) + "/deliveries", null,
            Json.IReadOnlyListTerraBaseChangeWebhookDelivery, null, cancellationToken).ConfigureAwait(false);
        if (response.Value.Any(delivery => delivery is null))
            throw new TerraBaseException(response.Status, "Function deliveries contain invalid items.", "invalid_response", null);
        return response.Value;
    }
}
