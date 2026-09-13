using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TerraBase.Storage.Migrations;

/// <summary>Applies ordered SQLite schema migrations exactly once.</summary>
public static class SqliteMigrationRunner
{
    private const string MigrationTable = "schema_migrations";

    /// <summary>Applies all pending migrations synchronously.</summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="migrations">The ordered migration catalog.</param>
    public static void Apply(SqliteConnection connection, IReadOnlyList<SqliteMigration> migrations)
    {
        ValidateInputs(connection, migrations);
        EnsureMigrationTable(connection);
        var catalog = NormalizeCatalog(migrations);
        var applied = ReadApplied(connection);
        ValidateApplied(catalog, applied);

        foreach (var migration in catalog.Where(item => !applied.ContainsKey(item.Version)))
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migration.Sql;
            command.ExecuteNonQuery();

            using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = """
                INSERT INTO schema_migrations (version, name, applied_at)
                VALUES ($version, $name, $applied_at);
                """;
            receipt.Parameters.AddWithValue("$version", migration.Version);
            receipt.Parameters.AddWithValue("$name", migration.Name);
            receipt.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            receipt.ExecuteNonQuery();
            transaction.Commit();
            applied.Add(migration.Version, migration.Name);
        }
    }

    /// <summary>Applies all pending migrations asynchronously.</summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="migrations">The ordered migration catalog.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    public static async Task ApplyAsync(
        SqliteConnection connection,
        IReadOnlyList<SqliteMigration> migrations,
        CancellationToken cancellationToken = default)
    {
        ValidateInputs(connection, migrations);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureMigrationTableAsync(connection, cancellationToken).ConfigureAwait(false);
        var catalog = NormalizeCatalog(migrations);
        var applied = await ReadAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
        ValidateApplied(catalog, applied);

        foreach (var migration in catalog.Where(item => !applied.ContainsKey(item.Version)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var receipt = connection.CreateCommand())
            {
                receipt.Transaction = transaction;
                receipt.CommandText = """
                    INSERT INTO schema_migrations (version, name, applied_at)
                    VALUES ($version, $name, $applied_at);
                    """;
                receipt.Parameters.AddWithValue("$version", migration.Version);
                receipt.Parameters.AddWithValue("$name", migration.Name);
                receipt.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            applied.Add(migration.Version, migration.Name);
        }
    }

    private static void ValidateInputs(SqliteConnection connection, IReadOnlyList<SqliteMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("SQLite migrations require an open connection.");
        }
    }

    private static SqliteMigration[] NormalizeCatalog(IReadOnlyList<SqliteMigration> migrations)
    {
        foreach (var migration in migrations)
        {
            ArgumentNullException.ThrowIfNull(migration);
            migration.Validate();
        }

        var catalog = migrations.OrderBy(item => item.Version).ToArray();
        if (catalog.Select(item => item.Version).Distinct().Count() != catalog.Length)
        {
            throw new InvalidOperationException("SQLite migration versions must be unique.");
        }

        return catalog;
    }

    private static void EnsureMigrationTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY CHECK (version > 0),
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            ) WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();
    }

    private static async Task EnsureMigrationTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY CHECK (version > 0),
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            ) WITHOUT ROWID;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<int, string> ReadApplied(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT version, name FROM {MigrationTable} ORDER BY version;";
        using var reader = command.ExecuteReader();
        var applied = new Dictionary<int, string>();
        while (reader.Read())
        {
            var version = reader.GetInt32(0);
            if (!applied.TryAdd(version, reader.GetString(1)))
            {
                throw new InvalidDataException($"SQLite contains duplicate migration version {version}.");
            }
        }

        return applied;
    }

    private static async Task<Dictionary<int, string>> ReadAppliedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT version, name FROM {MigrationTable} ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var applied = new Dictionary<int, string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var version = reader.GetInt32(0);
            if (!applied.TryAdd(version, reader.GetString(1)))
            {
                throw new InvalidDataException($"SQLite contains duplicate migration version {version}.");
            }
        }

        return applied;
    }

    private static void ValidateApplied(
        IReadOnlyList<SqliteMigration> catalog,
        IReadOnlyDictionary<int, string> applied)
    {
        var knownVersions = catalog.Select(item => item.Version).ToHashSet();
        foreach (var (version, name) in applied)
        {
            var migration = catalog.FirstOrDefault(item => item.Version == version);
            if (!knownVersions.Contains(version) || migration is null)
            {
                throw new InvalidDataException($"SQLite contains unknown migration version {version}.");
            }

            if (!string.Equals(migration.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"SQLite migration {version} is recorded as '{name}', expected '{migration.Name}'.");
            }
        }
    }
}
