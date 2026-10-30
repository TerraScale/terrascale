using MessagePack;

namespace TerraScale.TerraBase.Contracts.Functions
{
    /// <summary>A deployed JavaScript function; lists omit source.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFunctionInfo(string Name, string? Source, string? Cron, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    /// <summary>One invocation's bounded console output and result.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFunctionInvocation(string Id, DateTimeOffset At, string Trigger, string Status,
        long DurationMs, IReadOnlyList<string> Lines, string? Error);
}
