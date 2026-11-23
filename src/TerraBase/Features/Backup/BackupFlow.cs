using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Features.Backup;

internal sealed class BackupFlow(DatabaseAccess access, ManagementService management, IBackupContinuity continuity, SystemDatabase system, TimeProvider time)
{
    public const string Database = "/v1/projects/{project}/databases/{database}";

    public async ValueTask<Result<ResolvedDatabase, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> ResolveAsync(
        Caller caller, BackupInput request, string scope, CancellationToken cancellationToken) =>
        await access.ManagedAsync(caller, request.Project, request.Database, scope, cancellationToken) switch
        {
            ResolvedDatabase resolved => resolved,
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<BackupStatusBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> StatusAsync(
        Caller caller, BackupInput request, bool archiveNow, CancellationToken cancellationToken) =>
        await ResolveAsync(caller, request, archiveNow ? Scopes.DatabasesWrite : Scopes.DatabasesRead, cancellationToken) switch
        {
            ResolvedDatabase resolved => await (archiveNow
                ? continuity.ArchiveNowAsync(resolved.Database.Id, cancellationToken)
                : continuity.GetStatusAsync(resolved.Database.Id, cancellationToken)) switch
            {
                BackupStatus status => BackupStatusBody.From(status),
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<Accepted<RestoreOperationBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> RestoreAsync(
        Caller caller, BackupInput request, TargetChoice target, CancellationToken cancellationToken) =>
        await ResolveAsync(caller, request, Scopes.DatabasesWrite, cancellationToken) switch
        {
            ResolvedDatabase resolved => target switch
            {
                DateTimeOffset at => await RestoreIntoAsync(resolved, request, new RestoreTarget(at), cancellationToken),
                CausalBookmark bookmark => await RestoreIntoAsync(resolved, request, new RestoreTarget(bookmark), cancellationToken),
                NoTarget => new Validation("target names pointInTime or bookmark", "invalid_target"),
                Validation invalid => invalid,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<Accepted<RestoreOperationBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> RewindAsync(
        Caller caller, BackupInput request, CancellationToken cancellationToken) =>
        await ResolveAsync(caller, request, Scopes.DatabasesWrite, cancellationToken) switch
        {
            ResolvedDatabase resolved => Target(request) switch
            {
                DateTimeOffset at => Operation(resolved, await continuity.RewindAsync(resolved.Database.Id, new RestoreTarget(at), cancellationToken)),
                CausalBookmark bookmark => Operation(resolved, await continuity.RewindAsync(resolved.Database.Id, new RestoreTarget(bookmark), cancellationToken)),
                NoTarget => new Validation("send pointInTime or bookmark", "invalid_target"),
                Validation invalid => invalid,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<RestoreOperationBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> OperationAsync(
        Caller caller, BackupInput request, CancellationToken cancellationToken) =>
        await ResolveAsync(caller, request, Scopes.DatabasesRead, cancellationToken) switch
        {
            ResolvedDatabase resolved => ManagementService.Id(request.Operation) is { } operationId
                ? await continuity.GetOperationAsync(operationId, cancellationToken) switch
                {
                    RestoreOperation operation when operation.SourceDatabaseId == resolved.Database.Id || operation.TargetDatabaseId == resolved.Database.Id =>
                        RestoreOperationBody.From(operation),
                    RestoreOperation or NotFound => new NotFound("the restore operation does not exist", "unknown_operation"),
                }
                : new NotFound("the restore operation does not exist", "unknown_operation"),
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<RestoreWindowBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> WindowAsync(
        Caller caller, BackupInput request, CancellationToken cancellationToken) =>
        await ResolveAsync(caller, request, Scopes.DatabasesRead, cancellationToken) switch
        {
            ResolvedDatabase resolved => await continuity.GetStatusAsync(resolved.Database.Id, cancellationToken) switch
            {
                BackupStatus status => new RestoreWindowBody(resolved.Database.Id.ToString(), status.EarliestRestorePointUtc, status.LatestRestorePointUtc,
                    request.PointInTime is not { } at || (at >= status.EarliestRestorePointUtc && at <= status.LatestRestorePointUtc)),
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<BackupPolicyBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> PolicyAsync(
        Caller caller, BackupInput request, bool write, CancellationToken cancellationToken)
    {
        if (write && request.RetentionDays is not (>= 1 and <= 3650))
        {
            return new Validation("retentionDays is a whole number from 1 to 3650", "invalid_retention");
        }

        return await ResolveAsync(caller, request, write ? Scopes.DatabasesWrite : Scopes.DatabasesRead, cancellationToken) switch
        {
            ResolvedDatabase resolved when write => await system.WriteAsync(SqlBuild.Statement(
                "INSERT INTO backup_policies (database_id, retention_days, updated_at) VALUES (?, ?, ?) ON CONFLICT (database_id) DO UPDATE SET retention_days = excluded.retention_days, updated_at = excluded.updated_at",
                SqlBuild.Id(resolved.Database.Id), SqlBuild.Integer(request.RetentionDays ?? 7), SqlBuild.Time(time.GetUtcNow())), ConsistencyMode.Strong, cancellationToken) switch
            {
                SqlWriteResult => new BackupPolicyBody(resolved.Database.Id.ToString(), request.RetentionDays ?? 7, time.GetUtcNow()),
                Conflict conflict => conflict,
                Unavailable unavailable => unavailable,
            },
            ResolvedDatabase resolved => await system.QueryAsync(SqlBuild.Statement(
                "SELECT retention_days, updated_at FROM backup_policies WHERE database_id = ?", SqlBuild.Id(resolved.Database.Id)), cancellationToken) switch
            {
                IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } rows =>
                    new BackupPolicyBody(resolved.Database.Id.ToString(), (int)rows[0].IntegerAt(0), rows[0].TimeAt(1)),
                IReadOnlyList<IReadOnlyList<SqlValue>> => new BackupPolicyBody(resolved.Database.Id.ToString(), 7, resolved.Database.CreatedAt),
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    public static TargetChoice Target(BackupInput request) =>
        request.Target is { } target ? RestoreTargets.Parse(target.PointInTime, target.Bookmark) : RestoreTargets.Parse(request.PointInTime, request.Bookmark);

    private async ValueTask<Result<Accepted<RestoreOperationBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> RestoreIntoAsync(
        ResolvedDatabase source, BackupInput request, RestoreTarget target, CancellationToken cancellationToken)
    {
        if (!DatabaseNames.IsValid(request.Name))
        {
            return new Validation("name must be a lowercase database name of at most 63 characters", "invalid_name");
        }

        return await management.CreatePlacedAsync(source.Project, request.Name, source.Database.Regions, source.Database.AllRegions, source.Database.DefaultConsistency,
            source.Database.Strict, request.TtlSeconds, source.Database.Id, cancellationToken) switch
        {
            DatabaseRecord restored => Operation(source,
                await continuity.RestoreAsync(new PointInTimeRestoreRequest(source.Database.Id, restored.Id, target), cancellationToken)),
            Validation invalid => invalid,
            Forbidden forbidden => forbidden,
            Conflict conflict => conflict,
            QuotaExceeded quota => new Conflict(quota.Message, quota.Code),
            Unavailable unavailable => unavailable,
        };
    }

    private static Result<Accepted<RestoreOperationBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable> Operation(
        ResolvedDatabase source, Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable> started) => started switch
        {
            RestoreOperation operation => new Accepted<RestoreOperationBody>(
                string.Concat("/v1/projects/", source.Project.Id.ToString(), "/databases/", source.Database.Id.ToString(), "/restores/", operation.OperationId.ToString()),
                RestoreOperationBody.From(operation)),
            NotFound missing => missing,
            Validation invalid => invalid,
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
}
