using TerraBase.Configuration;
using TerraBase.Storage;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Engine;

internal sealed class SystemDatabase(TenantDatabases databases, DatabaseKeyring keyring, IClusterCoordinator coordinator, TerraBaseOptions options, TimeProvider time)
{
    public static readonly Ulid Id = Ulid.Empty;

    private const string KeyIdFile = "system-key-id";

    private static readonly TimeSpan SchemaPoll = TimeSpan.FromMilliseconds(500);

    private static readonly (string Table, string Sql)[] Tables =
    [
        ("organizations", "CREATE TABLE organizations (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_by TEXT NOT NULL, created_at INTEGER NOT NULL, plan TEXT NOT NULL, suspended INTEGER NOT NULL DEFAULT 0)"),
        ("projects", "CREATE TABLE projects (id TEXT PRIMARY KEY, organization_id TEXT NOT NULL, name TEXT NOT NULL, created_at INTEGER NOT NULL, plan TEXT)"),
        ("databases", "CREATE TABLE databases (id TEXT PRIMARY KEY, project_id TEXT NOT NULL, name TEXT NOT NULL, regions TEXT NOT NULL, all_regions INTEGER NOT NULL, default_consistency TEXT NOT NULL, strict INTEGER NOT NULL, state TEXT NOT NULL, key_id TEXT NOT NULL, branched_from TEXT, previous_name TEXT, created_at INTEGER NOT NULL, expires_at INTEGER, version INTEGER NOT NULL, storage_limit INTEGER NOT NULL DEFAULT 0)"),
        ("database_names", "CREATE TABLE database_names (project_id TEXT NOT NULL, name TEXT NOT NULL, database_id TEXT NOT NULL, PRIMARY KEY (project_id, name))"),
        ("api_keys", "CREATE TABLE api_keys (id TEXT PRIMARY KEY, name TEXT NOT NULL, scopes TEXT NOT NULL, organization_id TEXT NOT NULL, project_id TEXT, database_id TEXT, expires_at INTEGER, hash TEXT NOT NULL, created_at INTEGER NOT NULL)"),
        ("api_key_hashes", "CREATE TABLE api_key_hashes (hash TEXT PRIMARY KEY, key_id TEXT NOT NULL)"),
        ("rules_versions", "CREATE TABLE rules_versions (database_id TEXT NOT NULL, version TEXT NOT NULL, body TEXT NOT NULL, updated_at INTEGER NOT NULL, updated_by TEXT NOT NULL, digest TEXT NOT NULL, PRIMARY KEY (database_id, version))"),
        ("rules_current", "CREATE TABLE rules_current (database_id TEXT PRIMARY KEY, version TEXT NOT NULL)"),
        ("usage_counters", "CREATE TABLE usage_counters (scope TEXT NOT NULL, month TEXT NOT NULL, metric TEXT NOT NULL, value INTEGER NOT NULL, PRIMARY KEY (scope, month, metric))"),
        ("request_logs", "CREATE TABLE request_logs (database_id TEXT NOT NULL, id TEXT NOT NULL, at INTEGER NOT NULL, method TEXT NOT NULL, path TEXT NOT NULL, status INTEGER NOT NULL, duration_ms INTEGER NOT NULL, region TEXT NOT NULL, PRIMARY KEY (database_id, id))"),
        ("backup_policies", "CREATE TABLE backup_policies (database_id TEXT PRIMARY KEY, retention_days INTEGER NOT NULL, updated_at INTEGER NOT NULL)"),
    ];

    private ISqlDatabaseSession? _session;

    public ISqlDatabaseSession Session => _session ?? throw new InvalidOperationException("the system database is not open");

    public string KeyId => FileText.Read(Path.Combine(options.DataDirectory, KeyIdFile))?.Trim() ?? keyring.ActiveKeyId;

    public static void RegisterDeltaColumns(DeltaColumnRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(Id, "usage_counters", "value");
    }

    public async ValueTask<Result<Done, ConfigurationError>> OpenAsync(bool bootstrap, CancellationToken cancellationToken)
    {
        var keyFile = Path.Combine(options.DataDirectory, KeyIdFile);
        var keyId = KeyId;
        return File.Exists(keyFile)
            ? await databases.OpenAsync(Id, keyId, cancellationToken) switch
            {
                ISqlDatabaseSession session => await InitializeAsync(session, keyFile, keyId, bootstrap, cancellationToken),
                NotFound => new ConfigurationError("the system catalog is missing from a data-directory that already holds one; restore it before starting"),
                Unavailable error => new ConfigurationError(string.Concat("the system database could not be opened: ", error.Message)),
            }
            : await databases.OpenOrCreateAsync(Id, keyId, strict: false, cancellationToken) switch
            {
                ISqlDatabaseSession session => await InitializeAsync(session, keyFile, keyId, bootstrap, cancellationToken),
                Unavailable error => new ConfigurationError(string.Concat("the system database could not be opened: ", error.Message)),
            };
    }

    private async ValueTask<Result<Done, ConfigurationError>> InitializeAsync(ISqlDatabaseSession session, string keyFile, string keyId, bool bootstrap, CancellationToken cancellationToken)
    {
        _session = session;
        if (WriteKeyId(keyFile, keyId) is { } unwritable)
        {
            return unwritable;
        }

        return await EnsureSchemaAsync(bootstrap, cancellationToken);
    }

    public async ValueTask<Result<IReadOnlyList<IReadOnlyList<SqlValue>>, Unavailable>> QueryAsync(SqlStatement statement, CancellationToken cancellationToken)
    {
        var request = new SqlStatementRequest(Id, [statement], SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);
        return await Session.ExecuteReadAsync(request, cancellationToken) switch
        {
            SqlReadResult read => Rows(read.ResultSets),
            SqlError error => new Unavailable(error.Message, "system_catalog_error"),
            StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
            RequestTimeout timeout => new Unavailable(timeout.Message, "system_catalog_timeout"),
        };
    }

    public async ValueTask<Result<SqlWriteResult, Conflict, Unavailable>> WriteAsync(
        IReadOnlyList<SqlStatement> statements, ConsistencyMode mode, CancellationToken cancellationToken)
    {
        var request = new SqlStatementRequest(Id, statements, SqlAccess.Write, mode, CausalBookmark.Empty);
        return await coordinator.ExecuteWriteAsync(Session, request, cancellationToken) switch
        {
            SqlWriteResult written => written,
            SqlError { Code: "unique_violation" or "constraint_violation" } error => new Conflict(error.Message, "already_exists"),
            SqlError error => new Unavailable(error.Message, "system_catalog_error"),
            SerializationConflict conflict => new Conflict(conflict.Reason, "serialization_conflict"),
            SchemaMismatch => new Unavailable("the system catalog schema is changing; retry", "system_catalog_busy"),
            Unavailable unavailable => unavailable,
            RequestTimeout timeout => new Unavailable(timeout.Message, "system_catalog_timeout"),
            StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
        };
    }

    public ValueTask<Result<SqlWriteResult, Conflict, Unavailable>> WriteAsync(SqlStatement statement, ConsistencyMode mode, CancellationToken cancellationToken) =>
        WriteAsync([statement], mode, cancellationToken);

    private static Result<IReadOnlyList<IReadOnlyList<SqlValue>>, Unavailable> Rows(IReadOnlyList<SqlResultSet> sets) =>
        sets.Count == 0 ? new([]) : new(sets[^1].Rows);

    private static ConfigurationError? WriteKeyId(string path, string keyId)
    {
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, keyId);
            }

            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new ConfigurationError("the system database key id file under data-directory is not writable");
        }
    }

    private async ValueTask<Result<Done, ConfigurationError>> EnsureSchemaAsync(bool bootstrap, CancellationToken cancellationToken)
    {
        while (true)
        {
            Result<Done, ConfigurationError>? settled = await QueryAsync(SqlBuild.Statement("SELECT name FROM sqlite_schema WHERE type = 'table'"), cancellationToken) switch
            {
                IReadOnlyList<IReadOnlyList<SqlValue>> rows => await SettleAsync(Missing([.. rows.Select(static row => row.TextAt(0))]), bootstrap, cancellationToken),
                Unavailable failure => new ConfigurationError(string.Concat("the system database schema could not be read: ", failure.Message)),
            };
            if (settled is { } outcome)
            {
                return outcome;
            }

            await Task.Delay(SchemaPoll, time, cancellationToken);
        }
    }

    private static SqlStatement[] Missing(HashSet<string> present) =>
        [.. Tables.Where(table => !present.Contains(table.Table)).Select(static table => SqlBuild.Statement(table.Sql))];

    private async ValueTask<Result<Done, ConfigurationError>?> SettleAsync(SqlStatement[] missing, bool bootstrap, CancellationToken cancellationToken)
    {
        if (missing.Length == 0)
        {
            return new Done();
        }

        if (!bootstrap)
        {
            return null;
        }

        return await WriteAsync(missing, ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult => new Done(),
            Unavailable { Code: "strong_quorum_unreachable" } => null,
            Conflict conflict => new ConfigurationError(string.Concat("the system database schema could not be created: ", conflict.Message)),
            Unavailable unavailable => new ConfigurationError(string.Concat("the system database schema could not be created: ", unavailable.Message)),
        };
    }
}
