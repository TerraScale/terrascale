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

    /// <summary>Body of a deploy: JavaScript defining <c>handler(event)</c> and an optional five-field UTC cron.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFunctionDeploy(string Source, string? Cron = null);

    /// <summary>An invocation's HTTP status, content type and body as the function returned them.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseFunctionCall(int Status, string? ContentType, string Body);
}
