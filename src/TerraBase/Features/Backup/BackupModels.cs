using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Features.Backup;

public sealed record RestoreOperationBody(
    string OperationId,
    string SourceDatabaseId,
    string TargetDatabaseId,
    string State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? FailureMessage)
{
    public static RestoreOperationBody From(RestoreOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new(operation.OperationId.ToString(), operation.SourceDatabaseId.ToString(), operation.TargetDatabaseId.ToString(), Name(operation.State),
            operation.StartedAtUtc, operation.UpdatedAtUtc, operation.FailureMessage);
    }

    private static string Name(RestoreOperationState state) => state switch
    {
        RestoreOperationState.Pending => "pending",
        RestoreOperationState.Running => "running",
        RestoreOperationState.Succeeded => "succeeded",
        RestoreOperationState.Failed => "failed",
        _ => throw new UnreachableException(),
    };
}

public sealed record BackupStatusBody(
    string DatabaseId,
    string ArchivedThrough,
    DateTimeOffset EarliestRestorePointUtc,
    DateTimeOffset LatestRestorePointUtc,
    DateTimeOffset LastSnapshotAtUtc,
    long ArchivedBytes,
    int PendingSegments)
{
    public static BackupStatusBody From(BackupStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new(status.DatabaseId.ToString(), status.ArchivedThrough.Encode(), status.EarliestRestorePointUtc, status.LatestRestorePointUtc, status.LastSnapshotAtUtc,
            status.ArchivedBytes, status.PendingSegments);
    }
}

public sealed record RestoreTargetInput(DateTimeOffset? PointInTime, string? Bookmark);

public sealed record BackupInput(
    string? Project,
    string? Database,
    string? Operation,
    string? Backup,
    string? Name,
    long? TtlSeconds,
    RestoreTargetInput? Target,
    DateTimeOffset? PointInTime,
    string? Bookmark,
    int? RetentionDays,
    string? FullBackupCron);

public sealed record RestoreWindowBody(string DatabaseId, DateTimeOffset EarliestRestorePointUtc, DateTimeOffset LatestRestorePointUtc, bool Feasible);

public sealed record BackupPolicyBody(
    string DatabaseId,
    int RetentionDays,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FullBackupCron,
    DateTimeOffset UpdatedAt);

public sealed record NoTarget;

public readonly union TargetChoice(NoTarget, DateTimeOffset, CausalBookmark, Validation);

internal static class RestoreTargets
{
    public static TargetChoice Parse(DateTimeOffset? pointInTime, string? bookmark)
    {
        if (pointInTime is not null && bookmark is { Length: > 0 })
        {
            return new Validation("send exactly one of pointInTime or bookmark", "invalid_target");
        }

        if (pointInTime is { } time)
        {
            return time;
        }

        if (bookmark is { Length: > 0 } encoded)
        {
            return CausalBookmark.TryDecode(encoded, out var parsed)
                ? parsed
                : new Validation("bookmark must be one bookmark returned by TerraScale", "invalid_bookmark");
        }

        return new NoTarget();
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RestoreOperationBody))]
[JsonSerializable(typeof(BackupStatusBody))]
[JsonSerializable(typeof(BackupInput))]
[JsonSerializable(typeof(RestoreWindowBody))]
[JsonSerializable(typeof(BackupPolicyBody))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
