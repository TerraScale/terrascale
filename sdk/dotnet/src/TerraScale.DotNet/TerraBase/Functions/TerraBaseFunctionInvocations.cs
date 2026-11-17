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
/// <param name="Trigger">What started it, for example http or cron.</param>
/// <param name="Status">ok or error.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
/// <param name="Lines">At most 50 console lines, each at most 1 KiB.</param>
/// <param name="Error">Runtime error message, or null.</param>
/// <param name="SqlCalls">SQL binding calls, including transaction controls and refused calls.</param>
/// <param name="SqlMs">Milliseconds spent in SQL, excluding waits behind another binding call.</param>
/// <param name="RowsRead">Rows returned by successful SQL binding results, including RETURNING rows.</param>
/// <param name="RowsWritten">Sum of affected-row counts of successful SQL binding results.</param>
/// <param name="ResponseBytes">UTF-8 bytes of the HTTP response body; 0 for cron runs and execution errors.</param>
/// <param name="HttpStatus">Handler's HTTP status, or null for cron runs and execution errors.</param>
public sealed record TerraBaseFunctionLog(string Id, DateTimeOffset At, string Trigger, string Status,
    long DurationMs, IReadOnlyList<string> Lines, string? Error = null, long SqlCalls = 0, long SqlMs = 0,
    long RowsRead = 0, long RowsWritten = 0, long ResponseBytes = 0, int? HttpStatus = null);

/// <summary>Aggregated invocation metrics for one window, independent of retained logs.</summary>
/// <param name="Window">Query window: 1h, 24h or 7d.</param>
/// <param name="Invocations">Invocations in the window.</param>
/// <param name="Errors">Execution failures; a handler HTTP error status still completed successfully.</param>
/// <param name="P50Ms">Nearest-rank histogram estimate of the 50th percentile, in milliseconds.</param>
/// <param name="P95Ms">Nearest-rank histogram estimate of the 95th percentile, in milliseconds.</param>
/// <param name="P99Ms">Nearest-rank histogram estimate of the 99th percentile, in milliseconds.</param>
/// <param name="MaxMs">Slowest invocation, in milliseconds.</param>
/// <param name="SqlCalls">SQL binding calls, including transaction controls and refused calls.</param>
/// <param name="RowsRead">Rows returned by successful SQL binding results.</param>
/// <param name="RowsWritten">Affected rows of successful SQL binding results.</param>
/// <param name="Buckets">UTC buckets, oldest first, including empty and the current incomplete interval.</param>
public sealed record TerraBaseFunctionStats(string Window, long Invocations, long Errors, long P50Ms, long P95Ms,
    long P99Ms, long MaxMs, long SqlCalls, long RowsRead, long RowsWritten, IReadOnlyList<TerraBaseFunctionStatsBucket> Buckets);

/// <summary>One UTC stats bucket, including empty intervals.</summary>
/// <param name="At">Bucket start time.</param>
/// <param name="Invocations">Invocations in the bucket.</param>
/// <param name="Errors">Execution failures in the bucket.</param>
/// <param name="P50Ms">Nearest-rank histogram estimate of the bucket's 50th percentile, in milliseconds.</param>
/// <param name="P95Ms">Nearest-rank histogram estimate of the bucket's 95th percentile, in milliseconds.</param>
public sealed record TerraBaseFunctionStatsBucket(DateTimeOffset At, long Invocations, long Errors, long P50Ms, long P95Ms);

public sealed partial class TerraBaseDatabase
{
    /// <summary>
    /// Invokes once with sql:read. Body is at most 1 MiB: parsed JSON becomes event.body, otherwise UTF-8
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
                    await TerraBaseException.FromResponseAsync(buffered, null, opened.Token).ConfigureAwait(false));
            }
            return result;
        }, cancellationToken);
    }

    /// <summary>Lists invocation logs, newest first, with both sql:read and sql:write. Limit 1–100, default 50; no cursor.</summary>
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
    /// Reads aggregated invocation metrics with both sql:read and sql:write. Window is 1h, 24h (the default)
    /// or 7d; buckets are UTC, oldest first, including empty intervals. Percentiles are histogram estimates.
    /// Sent once.
    /// </summary>
    public async Task<TerraBaseFunctionStats> GetStatsAsync(string functionName, string? window = null,
        CancellationToken cancellationToken = default)
    {
        if (window is not null and not ("1h" or "24h" or "7d"))
            throw new ArgumentException("Stats window must be 1h, 24h or 7d.", nameof(window));
        var stats = (await connection.SendAsync(HttpMethod.Get, FunctionPath(functionName) + "/stats" +
            TerraBaseConnection.Query(("window", window)), null,
            Json.TerraBaseFunctionStats, null, cancellationToken).ConfigureAwait(false)).Value;
        if (stats is null || stats.Buckets is null || stats.Buckets.Any(bucket => bucket is null))
            throw new TerraBaseException(0, "Function stats response is invalid.", "invalid_response", null);
        return stats;
    }

}
