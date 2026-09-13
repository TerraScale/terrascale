using System.Linq;
using Microsoft.Data.Sqlite;
using TerraBase.Storage.Migrations;
using TerraBase.Storage.Schema;

namespace TerraBase.Api.Tests;

/// <summary>SQLite schema and migration invariants owned by the TerraBase storage seam.</summary>
public sealed class SqliteSchemaTests
{
    [Test]
    public async Task FileSchemaUsesWalFullAndExplicitTables()
    {
        var path = NewPath();
        try
        {
            await using var connection = Open(path);
            await SqliteSchema.EnsureCreatedAsync(connection);

            await Assert.That(Scalar(connection, "PRAGMA journal_mode;")?.ToString()).IsEqualTo("wal");
            await Assert.That(Convert.ToInt64(Scalar(connection, "PRAGMA synchronous;"))).IsEqualTo(2L);
            await Assert.That(Convert.ToInt64(Scalar(connection, "PRAGMA busy_timeout;"))).IsEqualTo(SqliteSchema.BusyTimeoutMilliseconds);
            await Assert.That(Convert.ToInt64(Scalar(connection, "PRAGMA foreign_keys;"))).IsEqualTo(1L);

            foreach (var table in new[]
            {
                "meta",
                "documents",
                "ledger",
                "journal",
                "usage_facts",
                "consumer_frontiers",
                "schema_definitions",
                "migration_records",
                "index_descriptors",
                "index_catalog",
                "index_usage",
                "index_history",
                "schema_migrations",
            })
            {
                await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;", ("$name", table)))).IsEqualTo(1L);
            }

            await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations;"))).IsEqualTo(SqliteSchema.CurrentVersion);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public async Task ReopeningTheDatabaseIsIdempotent()
    {
        var path = NewPath();
        try
        {
            await using (var first = Open(path))
            {
                await SqliteSchema.EnsureCreatedAsync(first);
            }

            await using (var second = Open(path))
            {
                await SqliteSchema.EnsureCreatedAsync(second);
                await Assert.That(Convert.ToInt64(Scalar(second, "SELECT COUNT(*) FROM schema_migrations;"))).IsEqualTo(SqliteSchema.CurrentVersion);
                await Assert.That(Convert.ToInt64(Scalar(second, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'index_catalog_collection_state';"))).IsEqualTo(1L);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public async Task LegacyDocumentsAreBackfilledWhenTheSchemaAdvances()
    {
        var path = NewPath();
        try
        {
            await using (var legacy = Open(path))
            {
                SqliteMigrationRunner.Apply(
                    legacy,
                    SqliteSchema.Migrations.Where(migration => migration.Version <= 2).ToArray());

                await using var insert = legacy.CreateCommand();
                insert.CommandText = """
                    INSERT INTO documents
                        (tenant, collection, id, commit_seq, version, deleted, body, updated_at)
                    VALUES
                        ('tenant-a', 'users', 'user-1', 1, 2, 0, X'01', '1970-01-01T00:00:42+00:00'),
                        ('tenant-a', 'users', 'user-1', 2, 5, 0, X'02', '1970-01-01T00:00:43+00:00');
                    """;
                await insert.ExecuteNonQueryAsync();
            }

            await using var current = Open(path);
            await SqliteSchema.EnsureCreatedAsync(current);

            await Assert.That(Convert.ToInt64(Scalar(
                current,
                "SELECT revision FROM documents WHERE tenant = 'tenant-a' AND collection = 'users' AND id = 'user-1' AND commit_seq = 1;")))
                .IsEqualTo(2L);
            await Assert.That(Convert.ToInt64(Scalar(
                current,
                "SELECT revision FROM documents WHERE tenant = 'tenant-a' AND collection = 'users' AND id = 'user-1' AND commit_seq = 2;")))
                .IsEqualTo(5L);
            await Assert.That(Convert.ToInt64(Scalar(
                current,
                "SELECT updated_at_ms FROM documents WHERE tenant = 'tenant-a' AND collection = 'users' AND id = 'user-1' AND commit_seq = 2;")))
                .IsEqualTo(43_000L);
            await Assert.That(Convert.ToInt64(Scalar(
                current,
                "SELECT version FROM version_watermarks WHERE tenant = 'tenant-a' AND collection = 'users' AND document_id = 'user-1';")))
                .IsEqualTo(5L);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public async Task ConcurrentMigrationAppliersRecheckAfterWaitingForTheWriteLock()
    {
        var path = NewPath();
        var firstEntered = new ManualResetEventSlim();
        var releaseFirst = new ManualResetEventSlim();
        Task? firstTask = null;
        Task? secondTask = null;
        try
        {
            await using var first = Open(path);
            await using var second = Open(path);
            SqliteSchema.ConfigureConnection(first);
            SqliteSchema.ConfigureConnection(second);
            SqliteMigrationRunner.Apply(first, []);

            first.CreateFunction("migration_gate", () =>
            {
                firstEntered.Set();
                if (!releaseFirst.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("the migration race gate was not released");
                }

                return 1;
            });
            second.CreateFunction("migration_gate", () => 1);
            var migration = new SqliteMigration(
                1,
                "race-proof",
                "SELECT migration_gate(); CREATE TABLE race_proof (id INTEGER NOT NULL);");

            firstTask = Task.Run(() => SqliteMigrationRunner.Apply(first, [migration]));
            await Assert.That(await Task.Run(() => firstEntered.Wait(TimeSpan.FromSeconds(5)))).IsTrue();
            secondTask = Task.Run(() => SqliteMigrationRunner.Apply(second, [migration]));
            releaseFirst.Set();

            try
            {
                await Task.WhenAll(firstTask, secondTask);
            }
            catch (Exception failure)
            {
                throw new InvalidOperationException(
                    "concurrent migration appliers must not duplicate a migration receipt after waiting for BEGIN IMMEDIATE.",
                    failure);
            }

            await Assert.That(Convert.ToInt64(Scalar(first, "SELECT COUNT(*) FROM schema_migrations;"))).IsEqualTo(1L);
            await Assert.That(Convert.ToInt64(Scalar(first, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'race_proof';"))).IsEqualTo(1L);
        }
        finally
        {
            releaseFirst.Set();
            if (firstTask is not null || secondTask is not null)
            {
                try
                {
                    await Task.WhenAll(
                        firstTask ?? Task.CompletedTask,
                        secondTask ?? Task.CompletedTask);
                }
                catch
                {
                    // The assertion above reports the original failure; cleanup
                    // only releases a potentially blocked migration callback.
                }
            }

            firstEntered.Dispose();
            releaseFirst.Dispose();
            DeleteDatabase(path);
        }
    }

    [Test]
    public async Task MigrationNameDriftFailsClosed()
    {
        var path = NewPath();
        try
        {
            await using var connection = Open(path);
            var original = new SqliteMigration(1, "one", "CREATE TABLE sample (value INTEGER NOT NULL);");
            SqliteMigrationRunner.Apply(connection, [original]);

            var changed = new SqliteMigration(1, "two", "CREATE TABLE sample (value TEXT NOT NULL);");
            var failure = await Assert.ThrowsAsync<InvalidDataException>(
                () => Task.Run(() => SqliteMigrationRunner.Apply(connection, [changed])));

            await Assert.That(failure).IsNotNull();
            await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 1 AND name = $name;", ("$name", original.Name)))).IsEqualTo(1L);
            await Assert.That(Scalar(connection, "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'sample';")?.ToString()).Contains("INTEGER");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public async Task CollectionMigrationStateIsDurableAndMonotonic()
    {
        var path = NewPath();
        try
        {
            await using var connection = Open(path);
            await SqliteSchema.EnsureCreatedAsync(connection);
            var store = new SqliteCollectionMigrationStore(connection);
            var migration = SqliteCollectionMigration.Planned(
                "tenant-a",
                "users",
                "migration-1",
                1,
                2,
                [1, 2, 3]);

            await store.CreateAsync(migration);
            await store.StartAsync("tenant-a", "users", "migration-1");
            await store.AdvanceAsync("tenant-a", "users", "migration-1", SqliteCollectionMigrationState.Running, 7, 7, 5, 2);
            var progress = await store.FindAsync("tenant-a", "users", "migration-1");
            await Assert.That(progress?.State).IsEqualTo(SqliteCollectionMigrationState.Running);
            await Assert.That(progress?.Cursor).IsEqualTo(7L);
            await Assert.That(progress?.Transformed).IsEqualTo(5L);

            await store.CompleteAsync("tenant-a", "users", "migration-1", [9, 9]);
            var complete = await store.FindAsync("tenant-a", "users", "migration-1");
            await Assert.That(complete?.State).IsEqualTo(SqliteCollectionMigrationState.Completed);
            await Assert.That(complete?.ResultDigest).IsNotNull();
            await Assert.That(complete!.ResultDigest!.SequenceEqual(new byte[] { 9, 9 })).IsTrue();

            var second = SqliteCollectionMigration.Planned("tenant-a", "users", "migration-2", 2, 3, [4]);
            await store.CreateAsync(second);
            await store.StartAsync("tenant-a", "users", "migration-2");
            var conflict = await Assert.ThrowsAsync<SqliteMigrationException>(
                () => store.StartAsync("tenant-a", "users", "migration-2"));
            await Assert.That(conflict).IsNotNull();
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteScalar();
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"terrabase-schema-{Guid.NewGuid():N}.sqlite3");

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, $"{path}-wal", $"{path}-shm" })
        {
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // The test connection has already disposed its handles. A
                // concurrent SQLite checkpoint may still have a sidecar open;
                // leaving that OS-managed artifact is safe for the throwaway path.
            }
        }
    }
}
