using System.Globalization;
using TerraBase.Continuity;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Features.Backup;

internal readonly union BackupTargetResolution(ResolvedDatabase, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

internal readonly union BackupStatusResponse(BackupStatusBody, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

internal readonly union RestoreStartResponse(Accepted<RestoreOperationBody>, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

internal readonly union RestoreOperationResponse(RestoreOperationBody, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

internal readonly union RestoreWindowResponse(RestoreWindowBody, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

internal readonly union BackupPolicyResponse(BackupPolicyBody, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

internal sealed class BackupFlow(DatabaseAccess access, ManagementService management, IBackupContinuity continuity, SystemDatabase system, TimeProvider time)
{
    public const string Database = "/v1/projects/{project}/databases/{database}";

    private const int DefaultRetentionDays = 7;

    public async ValueTask<BackupTargetResolution> ResolveAsync(
        Caller caller, BackupInput request, string scope, CancellationToken cancellationToken)
    {
        var outcome = await access.ManagedAsync(caller, request.Project, request.Database, scope, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => resolved,
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<BackupStatusResponse> StatusAsync(
        Caller caller, BackupInput request, bool archiveNow, CancellationToken cancellationToken)
    {
        var outcome = await ResolveAsync(caller, request, archiveNow ? Scopes.DatabasesWrite : Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => await (archiveNow
                ? continuity.ArchiveNowAsync(resolved.Database.Id, cancellationToken)
                : continuity.GetStatusAsync(resolved.Database.Id, cancellationToken)) switch
            {
                BackupStatus status => BackupStatusBody.From(status),
                ContinuityNotFound missing => new ResourceNotFound(missing.Message, missing.Code),
                ContinuityUnavailable unavailable => new BackendUnavailable(unavailable.Message, unavailable.Code),
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<RestoreStartResponse> RestoreAsync(
        Caller caller, BackupInput request, TargetChoice target, CancellationToken cancellationToken)
    {
        var outcome = await ResolveAsync(caller, request, Scopes.DatabasesWrite, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved when DatabaseAccess.IsNarrowed(resolved.Caller) => DatabaseAccess.NarrowedCreate,
            ResolvedDatabase resolved => target switch
            {
                DateTimeOffset at => await RestoreIntoAsync(resolved, request, new RestoreTarget(at), cancellationToken),
                CausalBookmark bookmark => await RestoreIntoAsync(resolved, request, new RestoreTarget(bookmark), cancellationToken),
                NoTarget => new InvalidInput("target names pointInTime or bookmark", "invalid_target"),
                InvalidInput invalid => invalid,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<RestoreStartResponse> RewindAsync(
        Caller caller, BackupInput request, CancellationToken cancellationToken)
    {
        var outcome = await ResolveAsync(caller, request, Scopes.DatabasesWrite, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => Target(request) switch
            {
                DateTimeOffset at => Operation(resolved, await continuity.RewindAsync(resolved.Database.Id, new RestoreTarget(at), cancellationToken)),
                CausalBookmark bookmark => Operation(resolved, await continuity.RewindAsync(resolved.Database.Id, new RestoreTarget(bookmark), cancellationToken)),
                NoTarget => new InvalidInput("send pointInTime or bookmark", "invalid_target"),
                InvalidInput invalid => invalid,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<RestoreOperationResponse> OperationAsync(
        Caller caller, BackupInput request, CancellationToken cancellationToken)
    {
        var outcome = await ResolveAsync(caller, request, Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => ManagementService.Id(request.Operation) is { } operationId
                ? await continuity.GetOperationAsync(operationId, cancellationToken) switch
                {
                    RestoreOperation operation when operation.SourceDatabaseId == resolved.Database.Id || operation.TargetDatabaseId == resolved.Database.Id =>
                        RestoreOperationBody.From(operation),
                    RestoreOperation or ContinuityNotFound => new ResourceNotFound("the restore operation does not exist", "unknown_operation"),
                }
                : new ResourceNotFound("the restore operation does not exist", "unknown_operation"),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<RestoreWindowResponse> WindowAsync(
        Caller caller, BackupInput request, CancellationToken cancellationToken)
    {
        var outcome = await ResolveAsync(caller, request, Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => await continuity.GetStatusAsync(resolved.Database.Id, cancellationToken) switch
            {
                BackupStatus status => new RestoreWindowBody(resolved.Database.Id.ToString(), status.EarliestRestorePointUtc, status.LatestRestorePointUtc,
                    request.PointInTime is not { } at || (at >= status.EarliestRestorePointUtc && at <= status.LatestRestorePointUtc)),
                ContinuityNotFound missing => new ResourceNotFound(missing.Message, missing.Code),
                ContinuityUnavailable unavailable => new BackendUnavailable(unavailable.Message, unavailable.Code),
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<BackupPolicyResponse> PolicyAsync(
        Caller caller, BackupInput request, bool write, CancellationToken cancellationToken)
    {
        if (write && request.RetentionDays is not (>= 1 and <= 3650))
        {
            return new InvalidInput("retentionDays is a whole number from 1 to 3650", "invalid_retention");
        }

        if (write && request.FullBackupCron is { } cron && !IsCron(cron))
        {
            return new InvalidInput("fullBackupCron is null or five cron fields of digits, *, /, - and ,", "invalid_schedule");
        }

        var outcome = await ResolveAsync(caller, request, write ? Scopes.DatabasesWrite : Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => await management.PlanAsync(resolved.Project, cancellationToken) switch
            {
                string plan when write => await WritePolicyAsync(resolved, request, BackupRetention.PlanCapDays(plan), cancellationToken),
                string plan => await ReadPolicyAsync(resolved, BackupRetention.PlanCapDays(plan), cancellationToken),
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<BackupPolicyResponse> WritePolicyAsync(ResolvedDatabase resolved, BackupInput request, int maximum, CancellationToken cancellationToken)
    {
        if (request.RetentionDays is not { } days || days > maximum)
        {
            return new InvalidInput(string.Concat("retentionDays is at most ", maximum.ToString(CultureInfo.InvariantCulture), " on this plan"), "invalid_retention");
        }

        var now = time.GetUtcNow();
        var outcome = await system.WriteAsync(SqlBuild.Statement(
            "INSERT INTO backup_policies (database_id, retention_days, full_backup_cron, updated_at) VALUES (?, ?, ?, ?) ON CONFLICT (database_id) DO UPDATE SET "
            + "retention_days = excluded.retention_days, full_backup_cron = excluded.full_backup_cron, updated_at = excluded.updated_at",
            SqlBuild.Id(resolved.Database.Id), SqlBuild.Integer(days), SqlBuild.OptionalText(request.FullBackupCron), SqlBuild.Time(now)),
            ConsistencyMode.Strong, cancellationToken);
        return outcome switch
        {
            SqlWriteResult => new BackupPolicyBody(resolved.Database.Id.ToString(), days, request.FullBackupCron, now),
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<BackupPolicyResponse> ReadPolicyAsync(ResolvedDatabase resolved, int maximum, CancellationToken cancellationToken)
    {
        var outcome = await system.QueryAsync(SqlBuild.Statement(
            "SELECT retention_days, full_backup_cron, updated_at FROM backup_policies WHERE database_id = ?", SqlBuild.Id(resolved.Database.Id)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } rows =>
                new BackupPolicyBody(resolved.Database.Id.ToString(), (int)Math.Min(rows[0].IntegerAt(0), maximum), rows[0].OptionalTextAt(1), rows[0].TimeAt(2)),
            IReadOnlyList<IReadOnlyList<SqlValue>> => new BackupPolicyBody(resolved.Database.Id.ToString(), Math.Min(DefaultRetentionDays, maximum), "0 2 * * *", resolved.Database.CreatedAt),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private static bool IsCron(string cron) =>
        cron.Length <= 64 && cron.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 5 } fields
        && fields.All(static field => field.All(static character => char.IsAsciiDigit(character) || character is '*' or '/' or '-' or ','));

    public static TargetChoice Target(BackupInput request) =>
        request.Target is { } target ? RestoreTargets.Parse(target.PointInTime, target.Bookmark) : RestoreTargets.Parse(request.PointInTime, request.Bookmark);

    private async ValueTask<RestoreStartResponse> RestoreIntoAsync(
        ResolvedDatabase source, BackupInput request, RestoreTarget target, CancellationToken cancellationToken) =>
        Operation(source, await management.CopyAsync(source.Project, source.Database, request.Name, request.TtlSeconds, target, branch: false, cancellationToken));

    private static RestoreStartResponse Operation(
        ResolvedDatabase source, DatabaseCopyOutcome started) => started switch
        {
            RestoreOperation operation => Accepted(source, operation),
            ResourceNotFound missing => missing,
            InvalidInput invalid => invalid,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };

    private static RestoreStartResponse Operation(ResolvedDatabase source, RestoreStartOutcome started) => started switch
    {
        RestoreOperation operation => Accepted(source, operation),
        ContinuityNotFound missing => new ResourceNotFound(missing.Message, missing.Code),
        ContinuityInvalidTarget invalid => new InvalidInput(invalid.Message, invalid.Code),
        ContinuityConflict conflict => new StateConflict(conflict.Message, conflict.Code),
        ContinuityUnavailable unavailable => new BackendUnavailable(unavailable.Message, unavailable.Code),
    };

    private static Accepted<RestoreOperationBody> Accepted(ResolvedDatabase source, RestoreOperation operation) => new(
        string.Concat("/v1/projects/", source.Project.Id.ToString(), "/databases/", source.Database.Id.ToString(), "/restores/", operation.OperationId.ToString()),
        RestoreOperationBody.From(operation));
}
