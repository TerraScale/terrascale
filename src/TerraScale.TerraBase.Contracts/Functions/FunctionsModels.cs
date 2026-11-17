using MessagePack;

namespace TerraScale.TerraBase.Contracts.Functions
{
    /// <summary>A deployed JavaScript function; lists omit source.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFunctionInfo(string Name, string? Source, string? Cron, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    /// <summary>One invocation's bounded console output and result.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFunctionInvocation(string Id, DateTimeOffset At, string Trigger, string Status,
        long DurationMs, IReadOnlyList<string> Lines, string? Error, int SqlCalls = 0, long SqlMs = 0,
        long RowsRead = 0, long RowsWritten = 0, long ResponseBytes = 0, int? HttpStatus = null);

    /// <summary>Aggregated invocation metrics for a window, independent of retained logs.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFunctionStats(string Window, long Invocations, long Errors, long P50Ms, long P95Ms,
        long P99Ms, long MaxMs, long SqlCalls, long RowsRead, long RowsWritten, IReadOnlyList<TerraBaseFunctionStatsBucket> Buckets);

    /// <summary>One UTC bucket, including empty intervals.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFunctionStatsBucket(DateTimeOffset At, long Invocations, long Errors, long P50Ms, long P95Ms);

    /// <summary>Body of a deploy: JavaScript defining <c>handler(event)</c> and an optional five-field UTC cron.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFunctionDeploy(string Source, string? Cron = null);

    /// <summary>An invocation's HTTP status, content type and body as the function returned them.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFunctionCall(int Status, string? ContentType, string Body);
}
