using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Management.Backups;

internal static class RestoreCatalog
{
    public const string Kind = "restore_operations";
}

internal static class RestoreKind
{
    public const string Backup = "backup";

    public const string PointInTime = "point-in-time";
}

internal static class RestoreOperationState
{
    public const string Running = "running";

    public const string Completed = "completed";

    public const string Failed = "failed";
}

internal sealed record RestoreOperationRecord(
    string Id,
    string OrganizationId,
    string ProjectId,
    string SourceDatabaseId,
    string? TargetDatabaseId,
    string Kind,
    string State,
    string? TargetName,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    string? Region,
    string? Node = null,
    string? LeasedBackupId = null);

public sealed record RestoreOperation(
    string Id, string OperationId, string Status, string Kind, string Database, string? ErrorCode = null, string? ErrorMessage = null,
    DateTimeOffset CreatedAt = default, DateTimeOffset? FinishedAt = null);

internal static class RestoreOperationRoute
{
    public static RestoreOperation ToBody(RestoreOperationRecord record) => new(
        record.Id, record.Id, record.State, record.Kind, record.TargetName ?? record.TargetDatabaseId ?? record.SourceDatabaseId,
        record.ErrorCode, record.ErrorMessage, record.CreatedAt, record.FinishedAt);

    public static IResult Accepted(HttpContext context, RestoreOperationRecord record)
    {
        context.Response.Headers.Location = context.Features.Get<LegacyManagementRoute>() is not null
            ? $"{ManagementRoute.Organizations}/{record.OrganizationId}/projects/{record.ProjectId}/databases/{record.SourceDatabaseId}/restores/{record.Id}"
            : $"/v1/projects/{record.ProjectId}/databases/{ManagementRoute.Value(context, "databaseName")}/restores/{record.Id}";
        return TypedResults.Json(ToBody(record), TerraBaseJsonContext.Default.RestoreOperation, statusCode: StatusCodes.Status202Accepted);
    }
}
