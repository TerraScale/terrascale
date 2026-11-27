using System.Globalization;
using System.Text;
using Cronos;
using ZLogger;

namespace TerraScale.Platform.Features.Functions;

public sealed record FunctionDeleted;

public sealed record SecretDeleted;

public sealed record FunctionRunRecorded;

public sealed record ProjectFunctionsForgotten;

public sealed record ClosedFunctionsSwept;

public sealed record ClosedFunctionsIdle;

public readonly union DeployFunctionOutcome(
    FunctionBody,
    PlatformInvalidRequest,
    PlatformConflict,
    PlatformUnavailable);

public readonly union ListFunctionsOutcome(FunctionList, PlatformUnavailable);

public readonly union GetFunctionOutcome(FunctionBody, PlatformNotFound, PlatformUnavailable);

public readonly union DeleteFunctionOutcome(FunctionDeleted, PlatformNotFound, PlatformConflict, PlatformUnavailable);

public readonly union ForgetProjectFunctionsOutcome(ProjectFunctionsForgotten, PlatformUnavailable);

public readonly union SweepClosedFunctionsOutcome(ClosedFunctionsSwept, ClosedFunctionsIdle, PlatformUnavailable);

public readonly union ListFunctionLogsOutcome(FunctionLogList, PlatformNotFound, PlatformUnavailable);

public readonly union GetFunctionStatsOutcome(FunctionStats, PlatformNotFound, PlatformUnavailable);

public readonly union ListFunctionSecretsOutcome(FunctionSecretList, PlatformNotFound, PlatformUnavailable);

public readonly union SetFunctionSecretOutcome(
    FunctionSecretBody,
    PlatformInvalidRequest,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public readonly union DeleteFunctionSecretOutcome(SecretDeleted, PlatformInvalidRequest, PlatformNotFound, PlatformUnavailable);

public readonly union LoadFunctionOutcome(FunctionCode, PlatformNotFound, PlatformUnavailable);

public readonly union RecordFunctionRunOutcome(FunctionRunRecorded, PlatformUnavailable);

public sealed class FunctionService(PlatformStore store, FunctionSecretSeal seal, TimeProvider timeProvider, ILogger<FunctionService> logger)
{
    public const int MaxFunctions = 32;
    public const int MaxSecrets = 50;
    public const int MaxSecretBytes = 4 * 1024;
    public const int KeptLogs = 100;

    private const string FunctionColumns = "name, source, cron, created_at, updated_at";
    private const string Closed = "SELECT 1 FROM platform_closed_file_projects WHERE project_id = ?";
    private const string OpenProject = "NOT EXISTS (SELECT 1 FROM platform_closed_file_projects WHERE project_id = ?)";
    internal const string Open = "NOT EXISTS (SELECT 1 FROM platform_closed_file_projects c WHERE c.project_id = f.project_id)";
    internal const string Live = $"EXISTS (SELECT 1 FROM platform_functions f WHERE f.database_id = ? AND f.name = ? AND f.created_at = ? AND f.instance_id IS ? AND {Open})";
    private const string OpenExists = $"SELECT 1 FROM platform_functions f WHERE f.database_id = ? AND f.name = ? AND {Open} AND {OpenProject}";
    private const int ForgetRounds = 64;
    private const int DeleteDependentRounds = 64;
    private const int RowBatch = 256;
    private const int ByteBudget = 2 * 1024 * 1024;
    private const string ClosedFunction = "EXISTS (SELECT 1 FROM platform_closed_file_projects c WHERE c.project_id = f.project_id AND c.organization_id = f.organization_id)";
    private const string ProjectFunction = $"f.project_id = ? AND f.organization_id = ? AND {ClosedFunction}";
    internal const string TrackedDatabase = "EXISTS (SELECT 1 FROM platform_function_databases d WHERE d.database_id = f.database_id AND d.project_id <> '')";
    internal const string AdoptDatabase = "ON CONFLICT (database_id) DO UPDATE SET project_id = CASE WHEN project_id = '' THEN excluded.project_id ELSE project_id END, "
        + "organization_id = CASE WHEN project_id = '' THEN excluded.organization_id ELSE organization_id END";
    private const string SecretBytes = "octet_length(o.database_id) + octet_length(o.function_name) + octet_length(o.name) + octet_length(o.sealed) + 8";
    private const string LogBytes = "octet_length(o.database_id) + octet_length(o.function_name) + octet_length(o.id) + octet_length(o.body) + coalesce(octet_length(o.instance_id), 0) + 8";
    internal const string FunctionBytes = "octet_length(f.database_id) + octet_length(f.name) + octet_length(f.organization_id) + octet_length(f.project_id) "
        + "+ octet_length(f.database_name) + octet_length(f.source) + coalesce(octet_length(f.cron), 0) + coalesce(octet_length(f.instance_id), 0) + CASE WHEN f.cron_next IS NULL THEN 0 ELSE 8 END + 16";
    private const string Orphan = "NOT EXISTS (SELECT 1 FROM platform_functions f WHERE f.database_id = o.database_id AND f.name = o.function_name)";
    private const string CreatedInstanceKey = "created:";
    internal const string InstanceKey = $"coalesce(f.instance_id, '{CreatedInstanceKey}' || f.created_at)";
    private const string VisibleLog = $"(l.instance_id IS NULL OR l.instance_id = {InstanceKey})";
    private const string StaleLog = "EXISTS (SELECT 1 FROM platform_functions f WHERE f.database_id = o.database_id AND f.name = o.function_name "
        + $"AND o.instance_id IS NOT NULL AND o.instance_id <> {InstanceKey})";
    private const string StaleInstanceStats = $"NOT EXISTS (SELECT 1 FROM platform_functions f WHERE f.database_id = o.database_id AND f.name = o.function_name AND {InstanceKey} = o.instance_id)";
    private const string ProjectDependents = "EXISTS (SELECT 1 FROM platform_function_databases d "
        + "WHERE d.project_id = ? AND d.organization_id = ? "
        + "AND (EXISTS (SELECT 1 FROM platform_function_secrets o WHERE o.database_id = d.database_id) "
        + "OR EXISTS (SELECT 1 FROM platform_function_stats o WHERE o.database_id = d.database_id) "
        + "OR EXISTS (SELECT 1 FROM platform_function_instance_stats o WHERE o.database_id = d.database_id) "
        + "OR EXISTS (SELECT 1 FROM platform_function_logs o WHERE o.database_id = d.database_id)))";
    private const string ClosedProjectDependents = "EXISTS (SELECT 1 FROM platform_function_databases d "
        + "WHERE d.project_id = c.project_id AND d.organization_id = c.organization_id "
        + "AND (EXISTS (SELECT 1 FROM platform_function_secrets o WHERE o.database_id = d.database_id) "
        + "OR EXISTS (SELECT 1 FROM platform_function_stats o WHERE o.database_id = d.database_id) "
        + "OR EXISTS (SELECT 1 FROM platform_function_instance_stats o WHERE o.database_id = d.database_id) "
        + "OR EXISTS (SELECT 1 FROM platform_function_logs o WHERE o.database_id = d.database_id)))";
    private const string SweepPending = $"SELECT (EXISTS (SELECT 1 FROM platform_functions f WHERE {ClosedFunction}) "
        + $"OR EXISTS (SELECT 1 FROM platform_closed_file_projects c WHERE {ClosedProjectDependents}) "
        + $"OR EXISTS (SELECT 1 FROM platform_function_secrets o WHERE {Orphan}) "
        + $"OR EXISTS (SELECT 1 FROM platform_function_stats o WHERE {Orphan}) "
        + $"OR EXISTS (SELECT 1 FROM platform_function_instance_stats o WHERE {StaleInstanceStats}) "
        + $"OR EXISTS (SELECT 1 FROM platform_function_logs o WHERE {Orphan} OR {StaleLog})), "
        + "EXISTS (SELECT 1 FROM platform_closed_file_projects WHERE functions_erased = 0), "
        + $"EXISTS (SELECT 1 FROM {FunctionStatsMath.LegacyTable} WHERE minute < ?)";
    private static readonly string ExpireLegacyStats = $"DELETE FROM {FunctionStatsMath.LegacyTable} WHERE rowid IN "
        + $"(SELECT rowid FROM {FunctionStatsMath.LegacyTable} WHERE minute < ? ORDER BY rowid LIMIT {RowBatch})";
    private static readonly EraseStep[] SweepSteps = EraseSteps(ClosedFunction, "", includeOrphans: true);
    private static readonly EraseStep[] ForgetSteps = EraseSteps(ProjectFunction, " AND c.project_id = ? AND c.organization_id = ?", includeOrphans: false);
    private const string MarkProjectFunctionsErased = "UPDATE platform_closed_file_projects SET functions_erased = functions_erased + 1 "
        + "WHERE project_id = ? AND organization_id = ? "
        + "AND NOT EXISTS (SELECT 1 FROM platform_functions f WHERE f.project_id = ? AND f.organization_id = ?) "
        + $"AND NOT {ProjectDependents}";
    private static readonly string ErasableClosedProjects = "FROM platform_closed_file_projects c WHERE c.functions_erased = 0 "
        + "AND NOT EXISTS (SELECT 1 FROM platform_functions f WHERE f.project_id = c.project_id AND f.organization_id = c.organization_id) "
        + $"AND NOT {ClosedProjectDependents} "
        + $"ORDER BY c.project_id LIMIT {RowBatch}";
    private static readonly string FenceSweepProjects = "INSERT INTO platform_function_projects (project_id, organization_id, fence) "
        + $"SELECT c.project_id, c.organization_id, ? {ErasableClosedProjects} "
        + "ON CONFLICT (project_id) DO UPDATE SET fence = excluded.fence";
    private static readonly string MarkSweepFunctionsErased = "UPDATE platform_closed_file_projects SET functions_erased = 1 "
        + $"WHERE project_id IN (SELECT c.project_id {ErasableClosedProjects})";
    private const string FenceClosedProject = "INSERT INTO platform_function_projects (project_id, organization_id, fence) "
        + "SELECT project_id, organization_id, ? FROM platform_closed_file_projects WHERE project_id = ? AND organization_id = ? "
        + "ON CONFLICT (project_id) DO UPDATE SET fence = excluded.fence";
    private const string FunctionDependentsRemaining = "(EXISTS (SELECT 1 FROM platform_function_secrets WHERE database_id = ? AND function_name = ?) "
        + "OR EXISTS (SELECT 1 FROM platform_function_stats WHERE database_id = ? AND function_name = ?) "
        + "OR EXISTS (SELECT 1 FROM platform_function_instance_stats WHERE database_id = ? AND function_name = ?) "
        + "OR EXISTS (SELECT 1 FROM platform_function_logs WHERE database_id = ? AND function_name = ?))";
    private const string ConfirmFunctionDeleted = "UPDATE platform_function_databases SET delete_fence = ? "
        + "WHERE database_id = ? "
        + "AND NOT EXISTS (SELECT 1 FROM platform_functions WHERE database_id = ? AND name = ?) "
        + "AND NOT EXISTS (SELECT 1 FROM platform_function_secrets WHERE database_id = ? AND function_name = ?) "
        + "AND NOT EXISTS (SELECT 1 FROM platform_function_stats WHERE database_id = ? AND function_name = ?) "
        + "AND NOT EXISTS (SELECT 1 FROM platform_function_instance_stats WHERE database_id = ? AND function_name = ?) "
        + "AND NOT EXISTS (SELECT 1 FROM platform_function_logs WHERE database_id = ? AND function_name = ?)";
    private const string DeployGuard = "SELECT EXISTS (SELECT 1 FROM platform_functions WHERE database_id = ? AND name = ?), "
        + FunctionDependentsRemaining;
    private static readonly EraseStep[] DeleteDependentSteps =
    [
        FencedDelete(
            "platform_function_secrets",
            $"SELECT o.rowid AS row_id, o.database_id AS database_id, {SecretBytes} AS size FROM platform_function_secrets o WHERE o.database_id = ? AND o.function_name = ? AND {Orphan}"),
        TelemetryDelete($"DELETE FROM platform_function_stats WHERE rowid IN (SELECT o.rowid FROM platform_function_stats o WHERE o.database_id = ? AND o.function_name = ? AND {Orphan} ORDER BY o.rowid LIMIT {RowBatch})"),
        TelemetryDelete($"DELETE FROM platform_function_instance_stats WHERE rowid IN (SELECT o.rowid FROM platform_function_instance_stats o WHERE o.database_id = ? AND o.function_name = ? AND {Orphan} ORDER BY o.rowid LIMIT {RowBatch})"),
        TelemetryDelete($"DELETE FROM platform_function_logs WHERE rowid IN ({Budgeted($"SELECT o.rowid AS row_id, {LogBytes} AS size FROM platform_function_logs o WHERE o.database_id = ? AND o.function_name = ? AND {Orphan}", RowBatch)})"),
    ];
    private const string InstanceStatsRows = "FROM platform_function_instance_stats WHERE database_id = ? AND function_name = ? AND instance_id = ?";
    private static readonly string InstanceTrimDue = TrimDue(InstanceStatsRows);
    private static readonly string InstanceTrimStats = TrimStats(FunctionStatsMath.InstanceTable, InstanceStatsRows);
    private static readonly string TrimLogs = "DELETE FROM platform_function_logs WHERE rowid IN ("
        + Budgeted(
            $"SELECT o.rowid AS row_id, {LogBytes} AS size FROM platform_function_logs o WHERE o.database_id = ? AND o.function_name = ? AND o.instance_id = ? AND o.id NOT IN "
            + "(SELECT id FROM platform_function_logs WHERE database_id = ? AND function_name = ? AND instance_id = ? ORDER BY at DESC, id DESC LIMIT ?)",
            RowBatch)
        + ")";

    public static PlatformInvalidRequest? CheckCron(string? cron) =>
        cron is null || CronExpression.TryParse(cron, CronFormat.Standard, out _) ? null : new PlatformInvalidRequest("cron must be a five-field cron expression");

    public static long? NextRun(string? cron, DateTimeOffset after) =>
        cron is not null && CronExpression.TryParse(cron, CronFormat.Standard, out var expression)
            ? expression.GetNextOccurrence(after, TimeZoneInfo.Utc)?.ToUnixTimeMilliseconds()
            : null;

    public async ValueTask<DeployFunctionOutcome> DeployAsync(
        FunctionTarget target,
        string name,
        string? source,
        string? cron,
        CancellationToken cancellationToken)
    {
        if (source is not { Length: > 0 and <= FunctionRunner.MaxSourceChars })
        {
            return new PlatformInvalidRequest("source is required and at most 256 KiB");
        }

        if (FunctionRunner.Check(source) is { } rejected)
        {
            return new PlatformInvalidRequest("source is not valid JavaScript: " + rejected.Message);
        }

        if (CheckCron(cron) is { } invalidCron)
        {
            return invalidCron;
        }

        var now = timeProvider.GetUtcNow();
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        var organization = SqlValues.Text(target.Organization.ToString());
        var project = SqlValues.Text(target.Project.ToString());
        var guard = SqlValues.Statement(
            DeployGuard,
            database,
            function,
            database,
            function,
            database,
            function,
            database,
            function,
            database,
            function);
        var upsert = SqlValues.Statement(
            "INSERT INTO platform_functions (database_id, name, organization_id, project_id, database_name, source, cron, cron_next, created_at, updated_at, instance_id) "
            + "SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE (EXISTS (SELECT 1 FROM platform_functions WHERE database_id = ? AND name = ?) "
            + "OR ((SELECT COUNT(*) FROM platform_functions WHERE database_id = ?) < ? "
            + "AND NOT EXISTS (SELECT 1 FROM platform_function_secrets WHERE database_id = ? AND function_name = ?) "
            + "AND NOT EXISTS (SELECT 1 FROM platform_function_stats WHERE database_id = ? AND function_name = ?) "
            + "AND NOT EXISTS (SELECT 1 FROM platform_function_instance_stats WHERE database_id = ? AND function_name = ?) "
            + "AND NOT EXISTS (SELECT 1 FROM platform_function_logs WHERE database_id = ? AND function_name = ?))) "
            + "AND NOT EXISTS (SELECT 1 FROM platform_closed_file_projects WHERE project_id = ?) "
            + "ON CONFLICT (database_id, name) DO UPDATE SET source = excluded.source, cron = excluded.cron, cron_next = excluded.cron_next, "
            + "updated_at = excluded.updated_at, database_name = excluded.database_name",
            database,
            function,
            organization,
            project,
            SqlValues.Text(target.DatabaseName),
            SqlValues.Text(source),
            SqlValues.OptionalText(cron),
            SqlValues.OptionalNumber(NextRun(cron, now)),
            SqlValues.Number(now.UnixMs()),
            SqlValues.Number(now.UnixMs()),
            SqlValues.Text(Ulid.NewUlid(now).ToString()),
            database,
            function,
            database,
            SqlValues.Number(MaxFunctions),
            database,
            function,
            database,
            function,
            database,
            function,
            database,
            function,
            project);
        var read = SqlValues.Statement(
            $"SELECT {FunctionColumns} FROM platform_functions f WHERE f.database_id = ? AND f.name = ? AND {Open} AND {OpenProject}",
            database,
            function,
            project);
        var outcome = await store.ExecuteAsync(
            [SqlValues.Statement(Closed, project), guard, upsert, FenceDatabase(database, project, organization, now), read, FenceProject(project, organization, now)],
            SqlContext.Strong,
            cancellationToken);
        var result = outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count > 0 =>
                new DeployPlan(new PlatformConflict("the project is closed", "project_closed"), false),
            SqlExecuteResponse { Results: var results } when results[2].RowsAffected == 0
                && results[1].FirstRow() is { } state && state.Number(0) == 0 && state.Number(1) != 0 =>
                new DeployPlan(new PlatformConflict("previous function deletion is still completing; retry", "function_deleting"), true),
            SqlExecuteResponse { Results: var results } when results[2].RowsAffected == 0 =>
                new DeployPlan(new PlatformConflict($"at most {MaxFunctions} functions may be deployed per database", "function_limit"), false),
            SqlExecuteResponse { Results: var results } => new DeployPlan(
                results[4].FirstRow() is { } row
                    ? Body(row, withSource: true)
                    : new PlatformUnavailable("the deployed function was not readable", "platform_state"),
                false),
            PlatformConflict { Code: "serialization_conflict" } conflict =>
                new DeployPlan(new PlatformUnavailable(conflict.Message, "platform_state"), false),
            PlatformConflict conflict => new DeployPlan(conflict, false),
            PlatformUnavailable unavailable => new DeployPlan(unavailable, false),
        };
        if (result.DrainOrphans)
        {
            var progress = await EraseRoundAsync(DeleteDependentSteps, [database, function], cancellationToken);
            if (progress.Failure is { } drainFailure)
            {
                logger.ZLogWarning($"Function {name} on {target.Database} orphan cleanup deferred during deploy: {drainFailure.Message}");
            }
        }

        return result.Outcome;
    }

    public async ValueTask<ListFunctionsOutcome> ListAsync(FunctionTarget target, CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement(
                $"SELECT {FunctionColumns} FROM platform_functions f WHERE f.database_id = ? AND {Open} AND {OpenProject} ORDER BY f.name",
                SqlValues.Text(target.Database.ToString()),
                SqlValues.Text(target.Project.ToString())),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => new FunctionList([.. results[0].RowsOf().Select(static row => Body(row, withSource: false))]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<GetFunctionOutcome> GetAsync(FunctionTarget target, string name, CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement(
                $"SELECT {FunctionColumns} FROM platform_functions f WHERE f.database_id = ? AND f.name = ? AND {Open} AND {OpenProject}",
                SqlValues.Text(target.Database.ToString()),
                SqlValues.Text(name),
                SqlValues.Text(target.Project.ToString())),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].FirstRow() is { } row ? Body(row, withSource: true) : Missing(),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DeleteFunctionOutcome> DeleteAsync(FunctionTarget target, string name, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        var project = SqlValues.Text(target.Project.ToString());
        var organization = SqlValues.Text(target.Organization.ToString());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(Closed, project),
            FenceDatabase(database, project, organization, now),
            SqlValues.Statement($"DELETE FROM platform_functions WHERE database_id = ? AND name = ? AND {OpenProject}", database, function, project),
            SqlValues.Statement($"DELETE FROM platform_function_secrets WHERE database_id = ? AND function_name = ? AND {OpenProject}", database, function, project),
            SqlValues.Statement(
                $"SELECT ({FunctionDependentsRemaining}) AND {OpenProject}",
                database, function, database, function, database, function, database, function, project),
            FenceProject(project, organization, now),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
        var plan = outcome switch
        {
            SqlExecuteResponse { Results: var results } => PlanDelete(results),
            PlatformConflict conflict => new DeletePlan(false, new PlatformUnavailable(conflict.Message, "platform_state")),
            PlatformUnavailable unavailable => new DeletePlan(false, unavailable),
        };
        if (plan.Settled is { } settled)
        {
            return settled;
        }

        if (plan.DrainDependents)
        {
            for (var round = 0; round < DeleteDependentRounds; round++)
            {
                var progress = await EraseRoundAsync(DeleteDependentSteps, [database, function], cancellationToken);
                if (progress.Failure is { } drainFailure)
                {
                    return drainFailure;
                }

                if (!progress.More)
                {
                    break;
                }
            }
        }

        var check = await store.ExecuteAsync(
            [
                SqlValues.Statement(
                    ConfirmFunctionDeleted,
                    Fence(timeProvider.GetUtcNow()),
                    database,
                    database,
                    function,
                    database,
                    function,
                    database,
                    function,
                    database,
                    function,
                    database,
                    function),
            ],
            SqlContext.Strong,
            cancellationToken);
        return check switch
        {
            SqlExecuteResponse { Results: var results } when results[0].RowsAffected == 1 =>
                new FunctionDeleted(),
            SqlExecuteResponse =>
                new PlatformUnavailable("function dependents are still being erased; retry", "function_delete_incomplete"),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static DeletePlan PlanDelete(IReadOnlyList<SqlHttpStatementResult> results)
    {
        if (results[0].Rows.Count > 0)
        {
            return new DeletePlan(false, new PlatformConflict("the project is closed and its functions are being erased", "project_closed"));
        }

        var deletedAny = results[2].RowsAffected > 0 || results[3].RowsAffected > 0;
        var dependentsRemaining = results[4].FirstRow() is { } row && row.Number(0) != 0;
        return !deletedAny && !dependentsRemaining
            ? new DeletePlan(false, Missing())
            : new DeletePlan(dependentsRemaining, null);
    }

    public async ValueTask<ForgetProjectFunctionsOutcome> ForgetProjectAsync(Ulid project, Ulid organization, CancellationToken cancellationToken)
    {
        var projectId = SqlValues.Text(project.ToString());
        var organizationId = SqlValues.Text(organization.ToString());
        for (var round = 0; round < ForgetRounds; round++)
        {
            var progress = await EraseRoundAsync(ForgetSteps, [projectId, organizationId], cancellationToken);
            if (progress.Failure is { } failure)
            {
                return failure;
            }

            if (!progress.More)
            {
                break;
            }
        }

        var marked = await store.ExecuteAsync(
            [
                FenceClosed(projectId, organizationId, timeProvider.GetUtcNow()),
                SqlValues.Statement(MarkProjectFunctionsErased, projectId, organizationId, projectId, organizationId, projectId, organizationId),
            ],
            SqlContext.Strong,
            cancellationToken);
        return marked switch
        {
            SqlExecuteResponse { Results: var results } when results[1].RowsAffected > 0 =>
                new ProjectFunctionsForgotten(),
            SqlExecuteResponse =>
                new PlatformUnavailable("project functions and dependents are still being erased; retry", "function_erasure_incomplete"),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<SweepClosedFunctionsOutcome> SweepClosedAsync(CancellationToken cancellationToken)
    {
        var expired = SqlValues.Number(timeProvider.GetUtcNow().ToUnixTimeSeconds() - FunctionStatsMath.RetainedSeconds);
        var pending = await store.ReadAsync(SqlValues.Statement(SweepPending, expired), cancellationToken);
        var due = pending switch
        {
            SqlExecuteResponse { Results: var results } => results[0].FirstRow() is { } row
                ? new SweepPlan(row.Number(0) != 0, row.Number(1) != 0, row.Number(2) != 0, null)
                : new SweepPlan(false, false, false, null),
            PlatformConflict conflict => new SweepPlan(false, false, false, new PlatformUnavailable(conflict.Message, "platform_state")),
            PlatformUnavailable unavailable => new SweepPlan(false, false, false, unavailable),
        };
        if (due.Failure is { } failure)
        {
            return failure;
        }

        if (!due.HasPendingRows && !due.HasUnmarkedClosedProjects && !due.HasExpiredLegacyStats)
        {
            return new ClosedFunctionsIdle();
        }

        var more = false;
        if (due.HasPendingRows)
        {
            var progress = await EraseRoundAsync(SweepSteps, [], cancellationToken);
            if (progress.Failure is { } stepFailure)
            {
                return stepFailure;
            }

            more |= progress.More;
        }

        if (due.HasUnmarkedClosedProjects)
        {
            var marked = Progress(await store.ExecuteAsync(
                [SqlValues.Statement(FenceSweepProjects, Fence(timeProvider.GetUtcNow())), SqlValues.Statement(MarkSweepFunctionsErased)],
                SqlContext.Strong,
                cancellationToken));
            if (marked.Failure is { } markFailure)
            {
                return markFailure;
            }

            more |= marked.More;
        }

        if (due.HasExpiredLegacyStats)
        {
            var trimmed = Progress(await store.ExecuteAsync([SqlValues.Statement(ExpireLegacyStats, expired)], SqlContext.Fast, cancellationToken));
            if (trimmed.Failure is { } trimFailure)
            {
                return trimFailure;
            }

            more |= trimmed.More;
        }

        return more ? new ClosedFunctionsSwept() : new ClosedFunctionsIdle();
    }

    private async ValueTask<ForgetProgress> EraseRoundAsync(IReadOnlyList<EraseStep> steps, IReadOnlyList<JsonElement> scope, CancellationToken cancellationToken)
    {
        var more = false;
        foreach (var step in steps)
        {
            var fence = Fence(timeProvider.GetUtcNow());
            IReadOnlyList<SqlHttpStatement> statements =
            [
                .. step.Fences.Select(sql => SqlValues.Statement(sql, [fence, .. scope])),
                SqlValues.Statement(step.Sql, scope),
            ];
            var progress = Progress(await store.ExecuteAsync(statements, step.Context, cancellationToken));
            if (progress.Failure is not null)
            {
                return progress;
            }

            more |= progress.More;
        }

        return new ForgetProgress(more, null);
    }

    private static EraseStep[] EraseSteps(string functions, string trackedFilter, bool includeOrphans) =>
    [
        RecordDatabases(functions),
        FencedDelete(
            "platform_functions",
            $"SELECT f.rowid AS row_id, f.database_id AS database_id, {FunctionBytes} AS size FROM platform_functions f WHERE {functions} AND {TrackedDatabase}"),
        FencedDelete("platform_function_secrets", Dependents("platform_function_secrets", $", o.database_id AS database_id, {SecretBytes} AS size", trackedFilter, includeOrphans, null)),
        TelemetryDelete($"DELETE FROM platform_function_stats WHERE rowid IN (SELECT row_id FROM ({Dependents("platform_function_stats", "", trackedFilter, includeOrphans, null)} ORDER BY row_id LIMIT {RowBatch}))"),
        TelemetryDelete($"DELETE FROM platform_function_instance_stats WHERE rowid IN (SELECT row_id FROM ({Dependents("platform_function_instance_stats", "", trackedFilter, includeOrphans, StaleInstanceStats)} ORDER BY row_id LIMIT {RowBatch}))"),
        TelemetryDelete($"DELETE FROM platform_function_logs WHERE rowid IN ({Budgeted(Dependents("platform_function_logs", $", {LogBytes} AS size", trackedFilter, includeOrphans, StaleLog), RowBatch)})"),
    ];

    private static EraseStep RecordDatabases(string functions)
    {
        var untracked = "SELECT DISTINCT f.database_id, f.project_id, f.organization_id FROM platform_functions f "
            + $"WHERE {functions} AND NOT {TrackedDatabase} ORDER BY f.database_id LIMIT {RowBatch}";
        return new EraseStep(
            $"INSERT INTO platform_function_databases (database_id, project_id, organization_id) SELECT r.database_id, r.project_id, r.organization_id FROM ({untracked}) r WHERE true {AdoptDatabase}",
            [
                "INSERT INTO platform_function_projects (project_id, organization_id, fence) "
                + $"SELECT DISTINCT r.project_id, r.organization_id, ? FROM ({untracked}) r WHERE true "
                + "ON CONFLICT (project_id) DO UPDATE SET fence = excluded.fence",
            ],
            SqlContext.Strong);
    }

    private static EraseStep FencedDelete(string table, string rows)
    {
        var databases = $"SELECT database_id FROM ({rows} ORDER BY row_id LIMIT {RowBatch})";
        return new EraseStep(
            $"DELETE FROM {table} WHERE rowid IN ({Budgeted(rows, RowBatch)})",
            [
                "INSERT INTO platform_function_databases (database_id, project_id, organization_id, delete_fence) "
                + $"SELECT DISTINCT r.database_id, '', '', ? FROM ({databases}) r WHERE true "
                + "ON CONFLICT (database_id) DO UPDATE SET delete_fence = excluded.delete_fence",
                "INSERT INTO platform_function_projects (project_id, organization_id, fence) "
                + $"SELECT DISTINCT d.project_id, d.organization_id, ? FROM platform_function_databases d WHERE d.database_id IN ({databases}) AND d.project_id <> '' "
                + "ON CONFLICT (project_id) DO UPDATE SET fence = excluded.fence",
            ],
            SqlContext.Strong);
    }

    private static EraseStep TelemetryDelete(string delete) => new(delete, [], SqlContext.Fast);

    private static string Dependents(string table, string size, string trackedFilter, bool includeOrphans, string? stale)
    {
        var closed = "EXISTS (SELECT 1 FROM platform_closed_file_projects c "
            + "JOIN platform_function_databases d ON d.project_id = c.project_id AND d.organization_id = c.organization_id "
            + $"WHERE d.database_id = o.database_id{trackedFilter})";
        var predicate = includeOrphans
            ? stale is null ? $"{closed} OR {Orphan}" : $"{closed} OR {Orphan} OR {stale}"
            : closed;
        return $"SELECT o.rowid AS row_id{size} FROM {table} o WHERE {predicate}";
    }

    private static string TrimDue(string statsRows) =>
        "SELECT EXISTS (SELECT 1 FROM platform_function_logs WHERE database_id = ? AND function_name = ? AND instance_id = ? LIMIT 1 OFFSET ?), "
        + $"EXISTS (SELECT 1 {statsRows} AND minute < ?)";

    private static string TrimStats(string table, string statsRows) =>
        $"DELETE FROM {table} WHERE rowid IN (SELECT rowid {statsRows} AND minute < ? ORDER BY rowid LIMIT {RowBatch})";

    private static string Budgeted(string rows, int limit) =>
        "SELECT row_id FROM (SELECT row_id, size, SUM(size) OVER (ORDER BY row_id ROWS UNBOUNDED PRECEDING) AS total "
        + $"FROM ({rows} ORDER BY row_id LIMIT {limit})) WHERE total = size OR total <= {ByteBudget}";

    private static ForgetProgress Progress(PlatformStoreOutcome outcome) => outcome switch
    {
        SqlExecuteResponse { Results: var results } => new ForgetProgress(results.Any(static result => result.RowsAffected > 0), null),
        PlatformConflict conflict => new ForgetProgress(false, new PlatformUnavailable(conflict.Message, "platform_state")),
        PlatformUnavailable unavailable => new ForgetProgress(false, unavailable),
    };

    private readonly record struct DeployPlan(DeployFunctionOutcome Outcome, bool DrainOrphans);

    private readonly record struct DeletePlan(bool DrainDependents, DeleteFunctionOutcome? Settled);

    private readonly record struct ForgetProgress(bool More, PlatformUnavailable? Failure);

    private sealed record EraseStep(string Sql, IReadOnlyList<string> Fences, SqlContext Context);

    private readonly record struct SweepPlan(bool HasPendingRows, bool HasUnmarkedClosedProjects, bool HasExpiredLegacyStats, PlatformUnavailable? Failure);

    public async ValueTask<ListFunctionLogsOutcome> LogsAsync(FunctionTarget target, string name, int limit, CancellationToken cancellationToken)
    {
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        var project = SqlValues.Text(target.Project.ToString());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(OpenExists, database, function, project),
            SqlValues.Statement(
                $"SELECT l.body FROM platform_function_logs l JOIN platform_functions f ON f.database_id = l.database_id AND f.name = l.function_name "
                + $"WHERE l.database_id = ? AND l.function_name = ? AND {VisibleLog} AND {Open} AND {OpenProject} ORDER BY l.at DESC, l.id DESC LIMIT ?",
                database, function, project, SqlValues.Number(limit)),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count == 0 => Missing(),
            SqlExecuteResponse { Results: var results } => new FunctionLogList([.. results[1].RowsOf().Select(static row => Log(row.Text(0))).OfType<FunctionLogBody>()]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<GetFunctionStatsOutcome> StatsAsync(FunctionTarget target, string name, string window, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(OpenExists, SqlValues.Text(target.Database.ToString()), SqlValues.Text(name), SqlValues.Text(target.Project.ToString())),
            FunctionStatsMath.Read(target, name, window, now),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count == 0 => Missing(),
            SqlExecuteResponse { Results: var results } => FunctionStatsMath.Aggregate(window, results[1], now),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ListFunctionSecretsOutcome> ListSecretsAsync(FunctionTarget target, string name, CancellationToken cancellationToken)
    {
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        var project = SqlValues.Text(target.Project.ToString());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(OpenExists, database, function, project),
            SqlValues.Statement(
                $"SELECT name, updated_at FROM platform_function_secrets WHERE database_id = ? AND function_name = ? AND {OpenProject} ORDER BY name",
                database, function, project),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count == 0 => Missing(),
            SqlExecuteResponse { Results: var results } =>
                new FunctionSecretList([.. results[1].RowsOf().Select(static row => new FunctionSecretBody(row.Text(0), row.Number(1).FromUnixMs()))]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<SetFunctionSecretOutcome> SetSecretAsync(
        FunctionTarget target,
        string name,
        string secret,
        string? value,
        CancellationToken cancellationToken)
    {
        if (FunctionNames.CheckSecret(secret) is { } invalid)
        {
            return invalid;
        }

        if (value is null || Encoding.UTF8.GetByteCount(value) is 0 or > MaxSecretBytes)
        {
            return new PlatformInvalidRequest("value is required and at most 4 KiB");
        }

        var now = timeProvider.GetUtcNow();
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        var project = SqlValues.Text(target.Project.ToString());
        var organization = SqlValues.Text(target.Organization.ToString());
        var key = SqlValues.Text(secret);
        var upsert = SqlValues.Statement(
            "INSERT INTO platform_function_secrets (database_id, function_name, name, sealed, updated_at) SELECT ?, ?, ?, ?, ? "
            + $"WHERE EXISTS ({OpenExists}) "
            + "AND ((SELECT COUNT(*) FROM platform_function_secrets WHERE database_id = ? AND function_name = ?) < ? "
            + "OR EXISTS (SELECT 1 FROM platform_function_secrets WHERE database_id = ? AND function_name = ? AND name = ?)) "
            + "ON CONFLICT (database_id, function_name, name) DO UPDATE SET sealed = excluded.sealed, updated_at = excluded.updated_at",
            database,
            function,
            key,
            SqlValues.Text(seal.Seal(target.Database, name, secret, value)),
            SqlValues.Number(now.UnixMs()),
            database,
            function,
            project,
            database,
            function,
            SqlValues.Number(MaxSecrets),
            database,
            function,
            key);
        var outcome = await store.ExecuteAsync(
            [FenceDatabase(database, project, organization, now), upsert, SqlValues.Statement(OpenExists, database, function, project), FenceProject(project, organization, now)],
            SqlContext.Strong,
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[2].Rows.Count == 0 => Missing(),
            SqlExecuteResponse { Results: var results } when results[1].RowsAffected == 0 =>
                new PlatformConflict($"at most {MaxSecrets} secrets may be set per function", "function_secret_limit"),
            SqlExecuteResponse => new FunctionSecretBody(secret, DateTimeOffset.FromUnixTimeMilliseconds(now.UnixMs())),
            PlatformConflict { Code: "serialization_conflict" } conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformConflict conflict => conflict,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DeleteFunctionSecretOutcome> DeleteSecretAsync(
        FunctionTarget target,
        string name,
        string secret,
        CancellationToken cancellationToken)
    {
        if (FunctionNames.CheckSecret(secret) is { } invalid)
        {
            return invalid;
        }

        var now = timeProvider.GetUtcNow();
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        var project = SqlValues.Text(target.Project.ToString());
        var organization = SqlValues.Text(target.Organization.ToString());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            FenceDatabase(database, project, organization, now),
            SqlValues.Statement(OpenExists, database, function, project),
            SqlValues.Statement(
                $"DELETE FROM platform_function_secrets WHERE database_id = ? AND function_name = ? AND name = ? AND {OpenProject}",
                database, function, SqlValues.Text(secret), project),
            FenceProject(project, organization, now),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[1].Rows.Count == 0 => Missing(),
            SqlExecuteResponse { Results: var results } when results[2].RowsAffected == 0 => new PlatformNotFound("secret not found", "secret_not_found"),
            SqlExecuteResponse => new SecretDeleted(),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static SqlHttpStatement FenceDatabase(
        JsonElement database,
        JsonElement project,
        JsonElement organization,
        DateTimeOffset now) =>
        SqlValues.Statement(
            "INSERT INTO platform_function_databases (database_id, project_id, organization_id, delete_fence) "
            + $"SELECT ?, ?, ?, ? WHERE {OpenProject} {AdoptDatabase}, delete_fence = excluded.delete_fence",
            database,
            project,
            organization,
            Fence(now),
            project);

    internal static SqlHttpStatement FenceProject(JsonElement project, JsonElement organization, DateTimeOffset now) =>
        SqlValues.Statement(
            "INSERT INTO platform_function_projects (project_id, organization_id, fence) "
            + $"SELECT ?, ?, ? WHERE {OpenProject} "
            + "ON CONFLICT (project_id) DO UPDATE SET fence = excluded.fence",
            project,
            organization,
            Fence(now),
            project);

    internal static SqlHttpStatement FenceCronClaim(
        JsonElement project,
        JsonElement organization,
        JsonElement database,
        JsonElement function,
        JsonElement due,
        DateTimeOffset now) =>
        SqlValues.Statement(
            "INSERT INTO platform_function_projects (project_id, organization_id, fence) "
            + $"SELECT ?, ?, ? WHERE {OpenProject} "
            + "AND EXISTS (SELECT 1 FROM platform_functions f WHERE f.database_id = ? AND f.name = ? AND f.project_id = ? AND f.cron_next = ?) "
            + "ON CONFLICT (project_id) DO UPDATE SET fence = excluded.fence",
            project,
            organization,
            Fence(now),
            project,
            database,
            function,
            project,
            due);

    internal static SqlHttpStatement FenceClosed(JsonElement project, JsonElement organization, DateTimeOffset now) =>
        SqlValues.Statement(FenceClosedProject, Fence(now), project, organization);

    internal static JsonElement Fence(DateTimeOffset now) => SqlValues.Text(Ulid.NewUlid(now).ToString());

    public async ValueTask<LoadFunctionOutcome> LoadAsync(FunctionTarget target, string name, CancellationToken cancellationToken)
    {
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        var project = SqlValues.Text(target.Project.ToString());
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                $"SELECT f.source, f.created_at, f.instance_id FROM platform_functions f WHERE f.database_id = ? AND f.name = ? AND {Open} AND {OpenProject}",
                database, function, project),
            SqlValues.Statement(
                $"SELECT name, sealed FROM platform_function_secrets WHERE database_id = ? AND function_name = ? AND {OpenProject}",
                database, function, project),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].FirstRow() is { } row
                ? new FunctionCode(row.Text(0), row.Number(1), row.OptionalText(2), [.. results[1].RowsOf().Select(static secret => new SealedSecret(secret.Text(0), secret.Text(1)))])
                : Missing(),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<RecordFunctionRunOutcome> RecordAsync(
        FunctionTarget target,
        string name,
        long createdAt,
        string? instanceId,
        FunctionLogBody log,
        CancellationToken cancellationToken)
    {
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        var created = SqlValues.Number(createdAt);
        var instance = SqlValues.OptionalText(instanceId);
        var instanceKey = instanceId ?? CreatedInstanceKey + createdAt.ToString(CultureInfo.InvariantCulture);
        var logKey = SqlValues.Text(instanceKey);
        var expired = SqlValues.Number(log.At.ToUnixTimeSeconds() - FunctionStatsMath.RetainedSeconds);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "INSERT INTO platform_function_logs (database_id, function_name, id, at, body, instance_id) SELECT ?, ?, ?, ?, ?, ? WHERE " + Live,
                database, function, SqlValues.Text(log.Id), SqlValues.Number(log.At.UnixMs()),
                SqlValues.Text(JsonSerializer.Serialize(log, FunctionsJsonContext.Default.FunctionLogBody)), logKey, database, function, created, instance),
            FunctionStatsMath.Upsert(target, name, createdAt, instanceId, instanceKey, log),
            SqlValues.Statement(InstanceTrimDue, database, function, logKey, SqlValues.Number(KeptLogs), database, function, logKey, expired),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        var trim = outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].RowsAffected > 0 && results[2].FirstRow() is { } row =>
                new TrimPlan(row.Number(0) != 0, row.Number(1) != 0, null),
            SqlExecuteResponse => new TrimPlan(false, false, null),
            PlatformConflict conflict => new TrimPlan(false, false, new PlatformUnavailable(conflict.Message, "platform_state")),
            PlatformUnavailable unavailable => new TrimPlan(false, false, unavailable),
        };
        if (trim.Failure is { } failure)
        {
            return failure;
        }

        if (trim.Logs)
        {
            await TrimAsync(target, name, "logs", SqlValues.Statement(TrimLogs, database, function, logKey, database, function, logKey, SqlValues.Number(KeptLogs)), cancellationToken);
        }

        if (trim.Stats)
        {
            await TrimAsync(target, name, "stats", SqlValues.Statement(InstanceTrimStats, database, function, logKey, expired), cancellationToken);
        }

        return new FunctionRunRecorded();
    }

    private async ValueTask TrimAsync(FunctionTarget target, string name, string kind, SqlHttpStatement trim, CancellationToken cancellationToken)
    {
        var outcome = await store.ExecuteAsync([trim], SqlContext.Fast, cancellationToken);
        string? failure = outcome switch
        {
            SqlExecuteResponse => null,
            PlatformConflict conflict => conflict.Message,
            PlatformUnavailable unavailable => unavailable.Message,
        };
        if (failure is { } message)
        {
            logger.ZLogWarning($"Function {name} on {target.Database} recorded its run but the {kind} trim failed: {message}");
        }
    }

    private readonly record struct TrimPlan(bool Logs, bool Stats, PlatformUnavailable? Failure);

    private static PlatformNotFound Missing() => new("the function does not exist", "function_not_found");

    private static FunctionBody Body(SqlRow row, bool withSource) =>
        new(row.Text(0), withSource ? row.Text(1) : null, row.OptionalText(2), row.Number(3).FromUnixMs(), row.Number(4).FromUnixMs());

    private static FunctionLogBody? Log(string json) => TerraBaseAnswers.Parse(Encoding.UTF8.GetBytes(json), FunctionsJsonContext.Default.FunctionLogBody);
}
