using Microsoft.Data.Sqlite;
using TerraBase.Storage.Migrations;
using TerraBase.Storage.Schema;

namespace TerraBase.Tests;

/// <summary>Focused checks for the SQLite migration runner.</summary>
public static class SqliteMigrationTests
{
    /// <summary>Verifies idempotent application and transactional failure.</summary>
    public static async Task RunAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"terrabase-migrations-{Guid.NewGuid():N}.sqlite3");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await SqliteSchema.EnsureCreatedAsync(connection);
                Assert(await CountAsync(connection, "schema_migrations") == SqliteSchema.CurrentVersion, "all schema migrations must be recorded");
                Assert(await CountAsync(connection, "index_descriptors") == 0, "schema migrations must create the index catalog");

                await SqliteSchema.EnsureCreatedAsync(connection);
                Assert(await CountAsync(connection, "schema_migrations") == SqliteSchema.CurrentVersion, "reopening a schema must not duplicate migrations");
            }

            await using (var connection = new SqliteConnection($"Data Source={path}.failure;Pooling=False"))
            {
                await connection.OpenAsync();
                var failed = false;
                try
                {
                    await SqliteMigrationRunner.ApplyAsync(
                        connection,
                        [new SqliteMigration(1, "broken", "CREATE TABLE should_rollback (id INTEGER); SELECT missing_function();")]);
                }
                catch (SqliteException)
                {
                    failed = true;
                }

                Assert(failed, "a failed migration must report the SQLite error");
                Assert(await CountAsync(connection, "schema_migrations") == 0, "a failed migration must not record a receipt");
                Assert(!await TableExistsAsync(connection, "should_rollback"), "a failed migration must roll back its DDL");
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete($"{path}-wal");
            File.Delete($"{path}-shm");
            File.Delete($"{path}.failure");
            File.Delete($"{path}.failure-wal");
            File.Delete($"{path}.failure-shm");
        }
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
