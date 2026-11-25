using TerraScale.Platform.Features.Functions;
using ZLogger;

namespace TerraScale.Platform.TerraBase;

public readonly union PlatformStoreOutcome(SqlExecuteResponse, PlatformConflict, PlatformUnavailable);

public sealed class PlatformStore(TerraBaseClient terraBase, PlatformOptions options)
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
                || code.Contains("unique", StringComparison.OrdinalIgnoreCase) => new PlatformConflict(failure.Error.Message, "conflict"),
            SqlFailure failure => new PlatformUnavailable($"the platform state database refused a statement: {failure.Error.Message}", "platform_state"),
            PlatformNotFound => new PlatformUnavailable("the platform state database does not exist", "platform_state_missing"),
            PlatformUnavailable unavailable => unavailable,
        };
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
    private static readonly string[] Tables =
    [
        """
        CREATE TABLE IF NOT EXISTS platform_functions (
            database_id TEXT NOT NULL, name TEXT NOT NULL, organization_id TEXT NOT NULL, project_id TEXT NOT NULL, database_name TEXT NOT NULL,
            source TEXT NOT NULL, cron TEXT, cron_next INTEGER, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
            PRIMARY KEY (database_id, name))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_function_secrets (
            database_id TEXT NOT NULL, function_name TEXT NOT NULL, name TEXT NOT NULL, sealed TEXT NOT NULL, updated_at INTEGER NOT NULL,
            PRIMARY KEY (database_id, function_name, name))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_function_logs (
            database_id TEXT NOT NULL, function_name TEXT NOT NULL, id TEXT NOT NULL, at INTEGER NOT NULL, body TEXT NOT NULL,
            PRIMARY KEY (database_id, function_name, id))
        """,
        "CREATE TABLE IF NOT EXISTS platform_function_stats (database_id TEXT NOT NULL, function_name TEXT NOT NULL, minute INTEGER NOT NULL, "
            + "invocations INTEGER NOT NULL, errors INTEGER NOT NULL, max_ms INTEGER NOT NULL, sql_calls INTEGER NOT NULL, rows_read INTEGER NOT NULL, "
            + "rows_written INTEGER NOT NULL, " + FunctionStatsMath.HistogramDefinitions() + ", PRIMARY KEY (database_id, function_name, minute))",
        """
        CREATE TABLE IF NOT EXISTS platform_buckets (
            project_id TEXT NOT NULL, id TEXT NOT NULL, organization_id TEXT NOT NULL, name TEXT NOT NULL, region TEXT NOT NULL, rules TEXT NOT NULL,
            PRIMARY KEY (project_id, id))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_files (
            project_id TEXT NOT NULL, bucket_id TEXT NOT NULL, id TEXT NOT NULL, organization_id TEXT NOT NULL, body TEXT NOT NULL, status TEXT NOT NULL,
            upload_expires_at INTEGER NOT NULL, delete_after INTEGER,
            PRIMARY KEY (project_id, bucket_id, id))
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_closed_file_projects (
            project_id TEXT NOT NULL PRIMARY KEY, organization_id TEXT NOT NULL, closed_at INTEGER NOT NULL DEFAULT 0, swept_at INTEGER)
        """,
        """
        CREATE TABLE IF NOT EXISTS platform_file_regions (region TEXT NOT NULL PRIMARY KEY)
        """,
        "INSERT OR IGNORE INTO platform_file_regions (region) SELECT DISTINCT region FROM platform_buckets",
        """
        CREATE TABLE IF NOT EXISTS platform_shapes (
            handle TEXT NOT NULL PRIMARY KEY, shape_id TEXT NOT NULL, organization_id TEXT NOT NULL, project_id TEXT NOT NULL,
            database_id TEXT NOT NULL, project_route TEXT NOT NULL, database_route TEXT NOT NULL, table_name TEXT NOT NULL,
            where_clause TEXT, columns TEXT, viewer_key TEXT NOT NULL, head INTEGER NOT NULL, head_digest TEXT NOT NULL,
            sealer TEXT NOT NULL, owner TEXT NOT NULL, lease_until INTEGER NOT NULL, last_read_at INTEGER NOT NULL, created_at INTEGER NOT NULL)
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
            if (applied && await EnsureClosedFileProjectColumnsAsync(stoppingToken))
            {
                readiness.MarkSchemaReady();
                logger.ZLogInformation($"Platform state schema is in place");
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async ValueTask<bool> EnsureClosedFileProjectColumnsAsync(CancellationToken cancellationToken)
    {
        var columnsOutcome = await store.ReadAsync(SqlValues.Statement("SELECT name FROM pragma_table_info('platform_closed_file_projects')"), cancellationToken);
        HashSet<string>? existing = columnsOutcome switch
        {
            SqlExecuteResponse response => [.. response.Results[0].RowsOf().Select(static row => row.Text(0))],
            PlatformConflict conflict => FailColumns(conflict.Message),
            PlatformUnavailable unavailable => FailColumns(unavailable.Message),
        };
        if (existing is null)
        {
            return false;
        }

        List<SqlHttpStatement> migrations = [];
        if (!existing.Contains("closed_at"))
        {
            migrations.Add(SqlValues.Statement("ALTER TABLE platform_closed_file_projects ADD COLUMN closed_at INTEGER NOT NULL DEFAULT 0"));
        }

        if (!existing.Contains("swept_at"))
        {
            migrations.Add(SqlValues.Statement("ALTER TABLE platform_closed_file_projects ADD COLUMN swept_at INTEGER"));
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

    private HashSet<string>? FailColumns(string message)
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
