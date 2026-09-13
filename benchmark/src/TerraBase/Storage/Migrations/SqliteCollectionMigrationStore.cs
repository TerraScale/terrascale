using Microsoft.Data.Sqlite;

namespace TerraBase.Storage.Migrations;

/// <summary>The durable state of one collection schema migration.</summary>
public enum SqliteCollectionMigrationState
{
    /// <summary>A plan exists but has not started processing documents.</summary>
    Planned,

    /// <summary>The forward migration is processing documents.</summary>
    Running,

    /// <summary>A rollback is processing documents.</summary>
    RollingBack,

    /// <summary>The target schema was seated successfully.</summary>
    Completed,

    /// <summary>The migration stopped because a typed failure was recorded.</summary>
    Failed,

    /// <summary>The migration was rolled back to its source version.</summary>
    RolledBack,
}

/// <summary>
/// The durable migration record persisted in SQLite. The contract and digest
/// fields are opaque bytes so this layer never parses document bodies.
/// </summary>
public sealed record SqliteCollectionMigration(
    string Tenant,
    string Collection,
    string MigrationId,
    long FromVersion,
    long ToVersion,
    SqliteCollectionMigrationState State,
    long Cursor,
    long Documents,
    long Transformed,
    long Pages,
    byte[] Contract,
    byte[]? SourceDigest,
    byte[]? ResultDigest,
    string? ErrorCode,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Validates one new migration record.</summary>
    public static SqliteCollectionMigration Planned(
        string tenant,
        string collection,
        string migrationId,
        long fromVersion,
        long toVersion,
        byte[] contract,
        byte[]? sourceDigest = null)
    {
        ValidateIdentity(tenant, collection, migrationId);
        if (fromVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromVersion));
        }

        if (toVersion <= fromVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(toVersion), "A migration must move forward.");
        }

        ArgumentNullException.ThrowIfNull(contract);
        if (contract.Length == 0)
        {
            throw new ArgumentException("A migration contract cannot be empty.", nameof(contract));
        }

        return new(
            tenant,
            collection,
            migrationId,
            fromVersion,
            toVersion,
            SqliteCollectionMigrationState.Planned,
            0,
            0,
            0,
            0,
            contract.ToArray(),
            sourceDigest?.ToArray(),
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private static void ValidateIdentity(string tenant, string collection, string migrationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
    }
}

/// <summary>Reads and advances collection migration state atomically in SQLite.</summary>
public sealed class SqliteCollectionMigrationStore
{
    /// <summary>Creates a store over an existing, migrated SQLite connection.</summary>
    public SqliteCollectionMigrationStore(SqliteConnection connection)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }
    }

    /// <summary>The SQLite connection owned by the storage layer.</summary>
    public SqliteConnection Connection { get; }

    /// <summary>Persists a new planned migration.</summary>
    public async Task CreateAsync(SqliteCollectionMigration migration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ValidateNewRecord(migration);
        await ExecuteAsync(
            """
            INSERT INTO migration_records
                (tenant, collection, migration_id, from_version, to_version, state,
                 cursor, documents, transformed, pages, contract, source_digest,
                 result_digest, error_code, updated_at)
            VALUES
                ($tenant, $collection, $migrationId, $fromVersion, $toVersion, $state,
                 $cursor, $documents, $transformed, $pages, $contract, $sourceDigest,
                 $resultDigest, $errorCode, $updatedAt);
            """,
            migration,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one migration, or null when no matching record exists.</summary>
    public async Task<SqliteCollectionMigration?> FindAsync(
        string tenant,
        string collection,
        string migrationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);

        await using var command = Connection.CreateCommand();
        command.CommandText = """
            SELECT tenant, collection, migration_id, from_version, to_version,
                   state, cursor, documents, transformed, pages, contract,
                   source_digest, result_digest, error_code, updated_at
            FROM migration_records
            WHERE tenant = $tenant AND collection = $collection AND migration_id = $migrationId;
            """;
        command.Parameters.AddWithValue("$tenant", tenant);
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$migrationId", migrationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    /// <summary>Moves a planned migration into the running state.</summary>
    public Task StartAsync(
        string tenant,
        string collection,
        string migrationId,
        CancellationToken cancellationToken = default) =>
        UpdateStateAsync(tenant, collection, migrationId, SqliteCollectionMigrationState.Planned, SqliteCollectionMigrationState.Running, null, cancellationToken);

    /// <summary>Records one bounded page of forward or rollback progress.</summary>
    public Task AdvanceAsync(
        string tenant,
        string collection,
        string migrationId,
        SqliteCollectionMigrationState state,
        long cursor,
        long documents,
        long transformed,
        long pages,
        CancellationToken cancellationToken = default)
    {
        if (state is not (SqliteCollectionMigrationState.Running or SqliteCollectionMigrationState.RollingBack))
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Progress requires a running migration.");
        }

        if (cursor < 0 || documents < 0 || transformed < 0 || pages < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor), "Migration progress cannot be negative.");
        }

        return ExecuteProgressAsync(
            tenant,
            collection,
            migrationId,
            state,
            cursor,
            documents,
            transformed,
            pages,
            cancellationToken);
    }

    /// <summary>Seats the target version after final evidence verification.</summary>
    public Task CompleteAsync(
        string tenant,
        string collection,
        string migrationId,
        byte[] resultDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resultDigest);
        if (resultDigest.Length == 0)
        {
            throw new ArgumentException("A result digest cannot be empty.", nameof(resultDigest));
        }

        return UpdateStateAsync(
            tenant,
            collection,
            migrationId,
            SqliteCollectionMigrationState.Running,
            SqliteCollectionMigrationState.Completed,
            resultDigest,
            cancellationToken);
    }

    /// <summary>Records a typed terminal failure without hiding the plan.</summary>
    public Task FailAsync(
        string tenant,
        string collection,
        string migrationId,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return UpdateStateAsync(
            tenant,
            collection,
            migrationId,
            SqliteCollectionMigrationState.Running,
            SqliteCollectionMigrationState.Failed,
            errorCode,
            cancellationToken);
    }

    private async Task ExecuteProgressAsync(
        string tenant,
        string collection,
        string migrationId,
        SqliteCollectionMigrationState state,
        long cursor,
        long documents,
        long transformed,
        long pages,
        CancellationToken cancellationToken)
    {
        await using var command = Connection.CreateCommand();
        command.CommandText = """
            UPDATE migration_records
            SET state = $state, cursor = $cursor, documents = $documents,
                transformed = $transformed, pages = $pages, updated_at = $updatedAt
            WHERE tenant = $tenant AND collection = $collection AND migration_id = $migrationId
              AND state = $state;
            """;
        AddIdentity(command, tenant, collection, migrationId);
        command.Parameters.AddWithValue("$state", StateText(state));
        command.Parameters.AddWithValue("$cursor", cursor);
        command.Parameters.AddWithValue("$documents", documents);
        command.Parameters.AddWithValue("$transformed", transformed);
        command.Parameters.AddWithValue("$pages", pages);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new SqliteMigrationException("Migration progress did not match a migration in the requested state.");
        }
    }

    private async Task UpdateStateAsync(
        string tenant,
        string collection,
        string migrationId,
        SqliteCollectionMigrationState expected,
        SqliteCollectionMigrationState next,
        object? result,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);

        await using var command = Connection.CreateCommand();
        command.CommandText = result switch
        {
            byte[] => """
                UPDATE migration_records
                SET state = $next, result_digest = $result, updated_at = $updatedAt
                WHERE tenant = $tenant AND collection = $collection AND migration_id = $migrationId
                  AND state = $expected;
                """,
            string => """
                UPDATE migration_records
                SET state = $next, error_code = $result, updated_at = $updatedAt
                WHERE tenant = $tenant AND collection = $collection AND migration_id = $migrationId
                  AND state = $expected;
                """,
            _ => """
                UPDATE migration_records
                SET state = $next, updated_at = $updatedAt
                WHERE tenant = $tenant AND collection = $collection AND migration_id = $migrationId
                  AND state = $expected;
                """,
        };
        AddIdentity(command, tenant, collection, migrationId);
        command.Parameters.AddWithValue("$expected", StateText(expected));
        command.Parameters.AddWithValue("$next", StateText(next));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        if (result is byte[] digest)
        {
            command.Parameters.AddWithValue("$result", digest);
        }
        else if (result is string errorCode)
        {
            command.Parameters.AddWithValue("$result", errorCode);
        }

        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new SqliteMigrationException(
                $"Migration state transition {StateText(expected)} -> {StateText(next)} was not accepted.");
        }
    }

    private async Task ExecuteAsync(
        string sql,
        SqliteCollectionMigration migration,
        CancellationToken cancellationToken)
    {
        await using var command = Connection.CreateCommand();
        command.CommandText = sql;
        AddIdentity(command, migration.Tenant, migration.Collection, migration.MigrationId);
        command.Parameters.AddWithValue("$fromVersion", migration.FromVersion);
        command.Parameters.AddWithValue("$toVersion", migration.ToVersion);
        command.Parameters.AddWithValue("$state", StateText(migration.State));
        command.Parameters.AddWithValue("$cursor", migration.Cursor);
        command.Parameters.AddWithValue("$documents", migration.Documents);
        command.Parameters.AddWithValue("$transformed", migration.Transformed);
        command.Parameters.AddWithValue("$pages", migration.Pages);
        command.Parameters.AddWithValue("$contract", migration.Contract);
        command.Parameters.AddWithValue("$sourceDigest", (object?)migration.SourceDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$resultDigest", (object?)migration.ResultDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)migration.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", migration.UpdatedAt.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException failure) when (failure.SqliteErrorCode == 19)
        {
            throw new SqliteMigrationException(
                "Another active migration already owns this tenant collection, or the migration identity is duplicated.",
                failure);
        }
    }

    private static void ValidateNewRecord(SqliteCollectionMigration migration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migration.Tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(migration.Collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migration.MigrationId);
        if (migration.FromVersion <= 0 || migration.ToVersion <= migration.FromVersion)
        {
            throw new ArgumentException("Migration versions must be positive and move forward.", nameof(migration));
        }

        if (migration.State != SqliteCollectionMigrationState.Planned)
        {
            throw new ArgumentException("New migration records must start in the planned state.", nameof(migration));
        }

        if (migration.Contract is null || migration.Contract.Length == 0)
        {
            throw new ArgumentException("A migration contract cannot be empty.", nameof(migration));
        }
    }

    private static SqliteCollectionMigration ReadRecord(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            ParseState(reader.GetString(5)),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            (byte[])reader[10],
            ReadBytes(reader, 11),
            ReadBytes(reader, 12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            DateTimeOffset.Parse(reader.GetString(14), null, System.Globalization.DateTimeStyles.RoundtripKind));

    private static byte[]? ReadBytes(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (byte[])reader[ordinal];

    private static void AddIdentity(SqliteCommand command, string tenant, string collection, string migrationId)
    {
        command.Parameters.AddWithValue("$tenant", tenant);
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$migrationId", migrationId);
    }

    private static string StateText(SqliteCollectionMigrationState state) =>
        state switch
        {
            SqliteCollectionMigrationState.Planned => "planned",
            SqliteCollectionMigrationState.Running => "running",
            SqliteCollectionMigrationState.RollingBack => "rolling_back",
            SqliteCollectionMigrationState.Completed => "completed",
            SqliteCollectionMigrationState.Failed => "failed",
            SqliteCollectionMigrationState.RolledBack => "rolled_back",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static SqliteCollectionMigrationState ParseState(string state) =>
        state switch
        {
            "planned" => SqliteCollectionMigrationState.Planned,
            "running" => SqliteCollectionMigrationState.Running,
            "rolling_back" => SqliteCollectionMigrationState.RollingBack,
            "completed" => SqliteCollectionMigrationState.Completed,
            "failed" => SqliteCollectionMigrationState.Failed,
            "rolled_back" => SqliteCollectionMigrationState.RolledBack,
            _ => throw new SqliteMigrationException($"Unknown durable migration state '{state}'."),
        };
}
