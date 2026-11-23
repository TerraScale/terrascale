using TerraScale.Platform.Features.Functions;
using ZLogger;

namespace TerraScale.Platform.TerraBase;

public sealed class PlatformStore(TerraBaseClient terraBase, PlatformOptions options)
{
    public async ValueTask<Result<IReadOnlyList<SqlHttpStatementResult>, Conflict, Unavailable>> ExecuteAsync(
        IReadOnlyList<SqlHttpStatement> statements,
        SqlContext context,
        CancellationToken cancellationToken) =>
        await terraBase.ExecuteAsync(options.StateDatabase, new SqlExecuteRequest(statements), context, cancellationToken) switch
        {
            SqlExecuteResponse response => Results(response),
            SqlFailure { Error.Code: var code } failure when code.Contains("constraint", StringComparison.OrdinalIgnoreCase)
                || code.Contains("unique", StringComparison.OrdinalIgnoreCase) => new Conflict(failure.Error.Message, "conflict"),
            SqlFailure failure => new Unavailable($"the platform state database refused a statement: {failure.Error.Message}", "platform_state"),
            NotFound => new Unavailable("the platform state database does not exist", "platform_state_missing"),
            Unavailable unavailable => unavailable,
        };

    public ValueTask<Result<IReadOnlyList<SqlHttpStatementResult>, Conflict, Unavailable>> ReadAsync(
        SqlHttpStatement statement,
        CancellationToken cancellationToken) =>
        ExecuteAsync([statement], SqlContext.Fast, cancellationToken);

    private static Result<IReadOnlyList<SqlHttpStatementResult>, Conflict, Unavailable> Results(SqlExecuteResponse response) => response.Results;
}

public sealed class PlatformSchema(PlatformStore store, PlatformReadiness readiness, TimeProvider timeProvider, ILogger<PlatformSchema> logger) : BackgroundService
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
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), timeProvider);
        do
        {
            var statements = Tables.Select(static sql => SqlValues.Statement(sql)).ToArray();
            var applied = await store.ExecuteAsync(statements, SqlContext.Strong, stoppingToken) switch
            {
                IReadOnlyList<SqlHttpStatementResult> => true,
                Conflict conflict => Failed(conflict.Message),
                Unavailable unavailable => Failed(unavailable.Message),
            };
            if (applied)
            {
                readiness.MarkSchemaReady();
                logger.ZLogInformation($"Platform state schema is in place");
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private bool Failed(string message)
    {
        logger.ZLogWarning($"Platform state schema is not in place yet: {message}");
        return false;
    }
}
