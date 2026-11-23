using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed record TerraBaseFilePolicy(bool Owner, bool Public, IReadOnlyList<string> Roles);

public sealed record TerraBaseFileRules(string? Owner, TerraBaseFilePolicy Read, TerraBaseFilePolicy Write);

public sealed record TerraBaseFileBucket(string Id, string Name, string Region, TerraBaseFileRules Rules);

public sealed record TerraBaseStoredFile(string Id, string Name, string ContentType, long Size, string Sha256, JsonElement Metadata, string Status,
    DateTimeOffset UploadExpiresAt);

public sealed record TerraBaseFileUrl(string Id, string Url, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers)
{
    public override string ToString() => string.Concat("File capability for ", Id, " (URL redacted)");
}

public sealed record TerraBaseFileUpload(string Name, string ContentType, long Size, string Sha256, JsonElement Metadata);

public sealed record TerraBaseFileBucketRequest(string Name, TerraBaseFileRules Rules);

public sealed record TerraBaseFileRulesRequest(TerraBaseFileRules Rules);

public sealed record TerraBaseFileBucketPage(IReadOnlyList<TerraBaseFileBucket> Items, string? Next);

public sealed record TerraBaseStoredFilePage(IReadOnlyList<TerraBaseStoredFile> Items, string? Next);

public sealed record TerraBaseBucketCreated(TerraBaseFileBucket Bucket);

public sealed record TerraBaseUploadCreated(TerraBaseFileUrl Upload);

public sealed record TerraBaseFunctionInfo(string Name, string? Source, string? Cron, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record TerraBaseFunctionInvocation(string Id, DateTimeOffset At, string Trigger, string Status, long DurationMs, IReadOnlyList<string> Lines, string? Error,
    int SqlCalls = 0, long SqlMs = 0, long RowsRead = 0, long RowsWritten = 0, long ResponseBytes = 0, int? HttpStatus = null);

public sealed record TerraBaseFunctionStatsBucket(DateTimeOffset At, long Invocations, long Errors, long P50Ms, long P95Ms);

public sealed record TerraBaseFunctionStats(string Window, long Invocations, long Errors, long P50Ms, long P95Ms, long P99Ms, long MaxMs, long SqlCalls, long RowsRead,
    long RowsWritten, IReadOnlyList<TerraBaseFunctionStatsBucket> Buckets);

public sealed record TerraBaseFunctionDeploy(string Source, string? Cron = null);

public sealed record TerraBaseFunctionCall(int Status, string? ContentType, string Body);

public sealed record TerraBaseFunctionSecret(string Name, DateTimeOffset UpdatedAt);

public sealed record TerraBaseFunctionSecretValue(string Value);

public sealed record TerraBaseFunctionList(IReadOnlyList<TerraBaseFunctionInfo> Items);

public sealed record TerraBaseFunctionLogList(IReadOnlyList<TerraBaseFunctionInvocation> Items);

public sealed record TerraBaseFunctionSecretList(IReadOnlyList<TerraBaseFunctionSecret> Items);
