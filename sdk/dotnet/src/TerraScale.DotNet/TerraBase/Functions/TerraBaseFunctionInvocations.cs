using System.Net;
using System.Net.Http.Headers;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseFunctionResponse(HttpStatusCode StatusCode, string? ContentType, ReadOnlyMemory<byte> Body);

public sealed class TerraBaseFunctionInvocationException : TerraBaseException
{
    internal TerraBaseFunctionInvocationException(TerraBaseFunctionResponse response, TerraBaseException error)
        : base(response.StatusCode, $"Function invocation answered HTTP {(int)response.StatusCode}; inspect Response.", error.ErrorCode, error.OperationIndex) => Response = response;

    public TerraBaseFunctionResponse Response { get; }
}

public sealed record TerraBaseFunctionLog(string Id, DateTimeOffset At, string Trigger, string Status,
    long DurationMs, IReadOnlyList<string> Lines, string? Error = null, long SqlCalls = 0, long SqlMs = 0,
    long RowsRead = 0, long RowsWritten = 0, long ResponseBytes = 0, int? HttpStatus = null);

public sealed record TerraBaseFunctionStats(string Window, long Invocations, long Errors, long P50Ms, long P95Ms,
    long P99Ms, long MaxMs, long SqlCalls, long RowsRead, long RowsWritten, IReadOnlyList<TerraBaseFunctionStatsBucket> Buckets);

public sealed record TerraBaseFunctionStatsBucket(DateTimeOffset At, long Invocations, long Errors, long P50Ms, long P95Ms);

public sealed partial class TerraBaseDatabase
{
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
