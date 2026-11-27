using TerraScale.Platform.Features.Functions;
using ZLogger;

namespace TerraScale.Platform.TerraBase;

public readonly union PlatformStoreOutcome(SqlExecuteResponse, PlatformConflict, PlatformUnavailable);

public sealed class PlatformStore(TerraBaseClient terraBase, PlatformOptions options, ILogger<PlatformStore> logger)
{
    public async ValueTask<PlatformStoreOutcome> ExecuteAsync(
        IReadOnlyList<SqlHttpStatement> statements,
        SqlContext context,
        CancellationToken cancellationToken)
    {
        var outcome = await terraBase.ExecuteAsync(options.StateDatabase, new SqlExecuteRequest(statements), context, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse response => response,
            SqlFailure { Error.Code: var code } failure when code.Contains("constraint", StringComparison.OrdinalIgnoreCase)
                || code.Contains("unique", StringComparison.OrdinalIgnoreCase) => Conflict(failure, "conflict"),
            SqlFailure { Error.Code: "serialization_conflict" } failure => Conflict(failure, "serialization_conflict"),
            SqlFailure failure => Refused(failure),
            PlatformNotFound => new PlatformUnavailable("the platform state database does not exist", "platform_state_missing"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private PlatformConflict Conflict(SqlFailure failure, string code)
    {
        logger.ZLogWarning($"The platform state database answered {failure.Error.Code}: {failure.Error.Message}");
        return new PlatformConflict("the request conflicts with existing platform state", code);
    }

    private PlatformUnavailable Refused(SqlFailure failure)
    {
        logger.ZLogWarning($"The platform state database answered {failure.Error.Code}: {failure.Error.Message}");
        return new PlatformUnavailable("the platform state database refused the statement; retry", "platform_state");
    }

    public ValueTask<PlatformStoreOutcome> ReadAsync(
        SqlHttpStatement statement,
        CancellationToken cancellationToken) =>
        ExecuteAsync([statement], SqlContext.Fast, cancellationToken);
}

public sealed class PlatformSchema(
    PlatformStore store,
    PlatformReadiness readiness,
    PlatformOptions options,
    TimeProvider timeProvider,
    ILogger<PlatformSchema> logger) : BackgroundService
{
    private const int BackfillBatch = 500;

    private static readonly (string Check, string Insert)[] MappingBackfills =
    [
        (
            $"SELECT 1 FROM platform_functions f WHERE NOT {FunctionService.TrackedDatabase} LIMIT 1",
            "INSERT INTO platform_function_databases (database_id, project_id, organization_id) "
            + "SELECT f.database_id, MIN(f.project_id), MIN(f.organization_id) FROM platform_functions f "
            + $"WHERE NOT {FunctionService.TrackedDatabase} "
            + $"GROUP BY f.database_id ORDER BY f.database_id LIMIT {BackfillBatch} {FunctionService.AdoptDatabase}"
        ),
        (
            "SELECT 1 FROM platform_buckets b WHERE NOT EXISTS (SELECT 1 FROM platform_bucket_regions r WHERE r.project_id = b.project_id AND r.bucket_id = b.id) LIMIT 1",
            "INSERT OR IGNORE INTO platform_bucket_regions (project_id, bucket_id, organization_id, region) "
            + "SELECT b.project_id, b.id, b.organization_id, b.region FROM platform_buckets b "
            + "WHERE NOT EXISTS (SELECT 1 FROM platform_bucket_regions r WHERE r.project_id = b.project_id AND r.bucket_id = b.id) "
            + $"ORDER BY b.project_id, b.id LIMIT {BackfillBatch}"
        ),
        (
            "SELECT 1 FROM platform_buckets b WHERE NOT EXISTS (SELECT 1 FROM platform_file_regions r WHERE r.region = b.region) LIMIT 1",
            "INSERT OR IGNORE INTO platform_file_regions (region) "
            + "SELECT DISTINCT b.region FROM platform_buckets b "
            + "WHERE NOT EXISTS (SELECT 1 FROM platform_file_regions r WHERE r.region = b.region) "
            + $"ORDER BY b.region LIMIT {BackfillBatch}"
        ),
    ];

    private static readonly string[] Tables =
    [
        """
        CREATE TABLE IF NOT EXISTS platform_functions (
            database_id TEXT NOT NULL, name TEXT NOT NULL, organization_id TEXT NOT NULL, project_id TEXT NOT NULL, database_name TEXT NOT NULL,
            source TEXT NOT NULL, cron TEXT, cron_next INTEGER, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, instance_id TEXT,
            PRIMARY KEY (database_id, name))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_function_secrets (
            database_id TEXT NOT NULL, function_name TEXT NOT NULL, name TEXT NOT NULL, sealed TEXT NOT NULL, updated_at INTEGER NOT NULL,
            PRIMARY KEY (database_id, function_name, name))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_function_logs (
            database_id TEXT NOT NULL, function_name TEXT NOT NULL, id TEXT NOT NULL, at INTEGER NOT NULL, body TEXT NOT NULL, instance_id TEXT,
            PRIMARY KEY (database_id, function_name, id))
        """,
        "CREATE TABLE IF NOT EXISTS platform_function_stats (database_id TEXT NOT NULL, function_name TEXT NOT NULL, minute INTEGER NOT NULL, "
            + "invocations INTEGER NOT NULL, errors INTEGER NOT NULL, max_ms INTEGER NOT NULL, sql_calls INTEGER NOT NULL, rows_read INTEGER NOT NULL, "
            + "rows_written INTEGER NOT NULL, " + FunctionStatsMath.HistogramDefinitions() + ", PRIMARY KEY (database_id, function_name, minute))",
        "CREATE TABLE IF NOT EXISTS platform_function_instance_stats (database_id TEXT NOT NULL, function_name TEXT NOT NULL, instance_id TEXT NOT NULL, "
            + "minute INTEGER NOT NULL, invocations INTEGER NOT NULL, errors INTEGER NOT NULL, max_ms INTEGER NOT NULL, sql_calls INTEGER NOT NULL, "
            + "rows_read INTEGER NOT NULL, rows_written INTEGER NOT NULL, " + FunctionStatsMath.HistogramDefinitions()
            + ", PRIMARY KEY (database_id, function_name, instance_id, minute))",
        """
        CREATE TABLE IF NOT EXISTS platform_function_databases (
            database_id TEXT NOT NULL PRIMARY KEY, project_id TEXT NOT NULL, organization_id TEXT NOT NULL, delete_fence TEXT)
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_function_projects (
            project_id TEXT NOT NULL PRIMARY KEY, organization_id TEXT NOT NULL, fence TEXT NOT NULL)
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_buckets (
            project_id TEXT NOT NULL, id TEXT NOT NULL, organization_id TEXT NOT NULL, name TEXT NOT NULL, region TEXT NOT NULL, rules TEXT NOT NULL, delete_fence TEXT,
            PRIMARY KEY (project_id, id))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_bucket_regions (
            project_id TEXT NOT NULL, bucket_id TEXT NOT NULL, organization_id TEXT NOT NULL, region TEXT NOT NULL,
            PRIMARY KEY (project_id, bucket_id))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_files (
            project_id TEXT NOT NULL, bucket_id TEXT NOT NULL, id TEXT NOT NULL, organization_id TEXT NOT NULL, body TEXT NOT NULL, status TEXT NOT NULL,
            upload_expires_at INTEGER NOT NULL, delete_after INTEGER,
            PRIMARY KEY (project_id, bucket_id, id))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_file_purges (
            project_id TEXT NOT NULL, bucket_id TEXT NOT NULL, id TEXT NOT NULL, organization_id TEXT NOT NULL, region TEXT NOT NULL, purge_after INTEGER NOT NULL,
            PRIMARY KEY (project_id, bucket_id, id))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_closed_file_projects (
            project_id TEXT NOT NULL PRIMARY KEY, organization_id TEXT NOT NULL, closed_at INTEGER NOT NULL DEFAULT 0, swept_at INTEGER, functions_erased INTEGER NOT NULL DEFAULT 0)
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_file_regions (region TEXT NOT NULL PRIMARY KEY)
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_shapes (
            handle TEXT NOT NULL PRIMARY KEY, shape_id TEXT NOT NULL, organization_id TEXT NOT NULL, project_id TEXT NOT NULL,
            database_id TEXT NOT NULL, project_route TEXT NOT NULL, database_route TEXT NOT NULL, table_name TEXT NOT NULL,
            where_clause TEXT, columns TEXT, viewer_key TEXT NOT NULL, head INTEGER NOT NULL, head_digest TEXT NOT NULL,
            sealer TEXT NOT NULL, owner TEXT NOT NULL, lease_until INTEGER NOT NULL, last_read_at INTEGER NOT NULL, created_at INTEGER NOT NULL)
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_shape_lineages (shape_id TEXT NOT NULL PRIMARY KEY, handle TEXT NOT NULL)
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_shape_chunks (
            handle TEXT NOT NULL, chunk_offset INTEGER NOT NULL, object TEXT NOT NULL, sha256 TEXT NOT NULL, size INTEGER NOT NULL,
            epoch INTEGER NOT NULL, digest TEXT NOT NULL, row_count INTEGER NOT NULL, created_at INTEGER NOT NULL,
            PRIMARY KEY (handle, chunk_offset))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_shape_object_deletions (
            id TEXT NOT NULL PRIMARY KEY, handle TEXT NOT NULL, name TEXT NOT NULL, due_at INTEGER NOT NULL, claimed INTEGER NOT NULL DEFAULT 0)
        """,
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<SqlHttpStatement> statements = [.. Tables.Select(static sql => SqlValues.Statement(sql))];
        if (options.Files is not null)
        {
            statements.Add(SqlValues.Statement("INSERT OR IGNORE INTO platform_file_regions (region) VALUES (?)", SqlValues.Text(options.Region)));
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), timeProvider);
        do
        {
            var appliedOutcome = await store.ExecuteAsync(statements, SqlContext.Strong, stoppingToken);
            var applied = appliedOutcome switch
            {
                SqlExecuteResponse => true,
                PlatformConflict conflict => Failed(conflict.Message),
                PlatformUnavailable unavailable => Failed(unavailable.Message),
            };
            if (applied && await EnsureSchemaColumnsAsync(stoppingToken) && await BackfillMappingsAsync(stoppingToken))
            {
                readiness.MarkSchemaReady();
                logger.ZLogInformation($"Platform state schema is in place");
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async ValueTask<bool> BackfillMappingsAsync(CancellationToken cancellationToken)
    {
        var checkOutcome = await store.ExecuteAsync(
            [.. MappingBackfills.Select(static backfill => SqlValues.Statement(backfill.Check))],
            SqlContext.Fast,
            cancellationToken);
        bool[]? pending = checkOutcome switch
        {
            SqlExecuteResponse response => [.. response.Results.Select(static result => result.Rows.Count > 0)],
            PlatformConflict conflict => FailBackfill(conflict.Message),
            PlatformUnavailable unavailable => FailBackfill(unavailable.Message),
        };
        if (pending is null)
        {
            return false;
        }

        for (var index = 0; index < MappingBackfills.Length; index++)
        {
            if (!pending[index])
            {
                continue;
            }

            var insert = SqlValues.Statement(MappingBackfills[index].Insert);
            while (true)
            {
                var insertedOutcome = await store.ExecuteAsync([insert], SqlContext.Strong, cancellationToken);
                long? affected = insertedOutcome switch
                {
                    SqlExecuteResponse response => response.Results[0].RowsAffected,
                    PlatformConflict conflict => FailBackfillCount(conflict.Message),
                    PlatformUnavailable unavailable => FailBackfillCount(unavailable.Message),
                };
                if (affected is not { } count)
                {
                    return false;
                }

                if (count < BackfillBatch)
                {
                    break;
                }
            }
        }

        return true;
    }

    private bool[]? FailBackfill(string message)
    {
        _ = Failed(message);
        return null;
    }

    private long? FailBackfillCount(string message)
    {
        _ = Failed(message);
        return null;
    }

    private async ValueTask<bool> EnsureSchemaColumnsAsync(CancellationToken cancellationToken)
    {
        var columnsOutcome = await store.ExecuteAsync(
            [
                SqlValues.Statement("SELECT name FROM pragma_table_info('platform_closed_file_projects')"),
                SqlValues.Statement("SELECT name FROM pragma_table_info('platform_buckets')"),
                SqlValues.Statement("SELECT name FROM pragma_table_info('platform_functions')"),
                SqlValues.Statement("SELECT name FROM pragma_table_info('platform_function_databases')"),
                SqlValues.Statement("SELECT name FROM pragma_table_info('platform_function_logs')"),
            ],
            SqlContext.Fast,
            cancellationToken);
        (HashSet<string> ClosedProjects, HashSet<string> Buckets, HashSet<string> Functions, HashSet<string> FunctionDatabases, HashSet<string> FunctionLogs)? existing = columnsOutcome switch
        {
            SqlExecuteResponse response => (
                [.. response.Results[0].RowsOf().Select(static row => row.Text(0))],
                [.. response.Results[1].RowsOf().Select(static row => row.Text(0))],
                [.. response.Results[2].RowsOf().Select(static row => row.Text(0))],
                [.. response.Results[3].RowsOf().Select(static row => row.Text(0))],
                [.. response.Results[4].RowsOf().Select(static row => row.Text(0))]),
            PlatformConflict conflict => FailColumns(conflict.Message),
            PlatformUnavailable unavailable => FailColumns(unavailable.Message),
        };
        if (existing is not { } columns)
        {
            return false;
        }

        List<SqlHttpStatement> migrations = [];
        if (!columns.ClosedProjects.Contains("closed_at"))
        {
            migrations.Add(SqlValues.Statement("ALTER TABLE platform_closed_file_projects ADD COLUMN closed_at INTEGER NOT NULL DEFAULT 0"));
        }

        if (!columns.ClosedProjects.Contains("swept_at"))
        {
            migrations.Add(SqlValues.Statement("ALTER TABLE platform_closed_file_projects ADD COLUMN swept_at INTEGER"));
        }

        if (!columns.ClosedProjects.Contains("functions_erased"))
        {
            migrations.Add(SqlValues.Statement("ALTER TABLE platform_closed_file_projects ADD COLUMN functions_erased INTEGER NOT NULL DEFAULT 0"));
        }

        if (!columns.Buckets.Contains("delete_fence"))
        {
            migrations.Add(SqlValues.Statement("ALTER TABLE platform_buckets ADD COLUMN delete_fence TEXT"));
        }

        if (!columns.Functions.Contains("instance_id"))
        {
            migrations.Add(SqlValues.Statement("ALTER TABLE platform_functions ADD COLUMN instance_id TEXT"));
        }

        if (!columns.FunctionDatabases.Contains("delete_fence"))
        {
            migrations.Add(SqlValues.Statement("ALTER TABLE platform_function_databases ADD COLUMN delete_fence TEXT"));
        }

        if (!columns.FunctionLogs.Contains("instance_id"))
        {
            migrations.Add(SqlValues.Statement("ALTER TABLE platform_function_logs ADD COLUMN instance_id TEXT"));
        }

        if (migrations.Count == 0)
        {
            return true;
        }

        var migrated = await store.ExecuteAsync(migrations, SqlContext.Strong, cancellationToken);
        return migrated switch
        {
            SqlExecuteResponse => true,
            PlatformConflict conflict => Failed(conflict.Message),
            PlatformUnavailable unavailable => Failed(unavailable.Message),
        };
    }

    private (HashSet<string> ClosedProjects, HashSet<string> Buckets, HashSet<string> Functions, HashSet<string> FunctionDatabases, HashSet<string> FunctionLogs)? FailColumns(string message)
    {
        _ = Failed(message);
        return null;
    }

    private bool Failed(string message)
    {
        logger.ZLogWarning($"Platform state schema is not in place yet: {message}");
        return false;
    }
}
