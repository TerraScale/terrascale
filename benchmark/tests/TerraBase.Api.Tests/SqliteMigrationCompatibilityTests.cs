using Microsoft.Data.Sqlite;
using TerraBase.Storage.Migrations;
using TerraBase.Storage.Schema;

namespace TerraBase.Api.Tests;

/// <summary>Regression rows for durable collection progress and schema upgrades.</summary>
public sealed class SqliteMigrationCompatibilityTests
{
    /// <summary>Every persisted progress counter remains monotonic once recorded.</summary>
    [Test]
    public async Task MigrationProgressRejectsRegressiveCursorAndCounters()
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
                "migration-monotonicity",
                1,
                2,
                [1, 2, 3]);

            await store.CreateAsync(migration);
            await store.StartAsync(migration.Tenant, migration.Collection, migration.MigrationId);
            await store.AdvanceAsync(
                migration.Tenant,
                migration.Collection,
                migration.MigrationId,
                SqliteCollectionMigrationState.Running,
                cursor: 7,
                documents: 7,
                transformed: 5,
                pages: 2);

            foreach (var progress in new (long Cursor, long Documents, long Transformed, long Pages)[]
            {
                (6, 7, 5, 2),
                (7, 6, 5, 2),
                (7, 7, 4, 2),
                (7, 7, 5, 1),
            })
            {
                var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() => store.AdvanceAsync(
                    migration.Tenant,
                    migration.Collection,
                    migration.MigrationId,
                    SqliteCollectionMigrationState.Running,
                    progress.Cursor,
                    progress.Documents,
                    progress.Transformed,
                    progress.Pages));
                await Assert.That(failure).IsNotNull();
            }

            var retained = await store.FindAsync(migration.Tenant, migration.Collection, migration.MigrationId);
            await Assert.That(retained?.Cursor).IsEqualTo(7L);
            await Assert.That(retained?.Documents).IsEqualTo(7L);
            await Assert.That(retained?.Transformed).IsEqualTo(5L);
            await Assert.That(retained?.Pages).IsEqualTo(2L);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>Migration 3 preserves legacy rows and backfills their document identity.</summary>
    [Test]
    public async Task MigrationThreeBackfillsLegacyDocumentIdentity()
    {
        var path = NewPath();
        try
        {
            await using var connection = Open(path);
            var firstTwoMigrations = SqliteSchema.Migrations.Take(2).ToArray();
            SqliteMigrationRunner.Apply(connection, firstTwoMigrations);

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO documents
                        (tenant, collection, id, commit_seq, version, deleted, body, updated_at)
                    VALUES
                        ('tenant-legacy', 'users', 'legacy-1', 4, 2, 0,
                         X'7B226E616D65223A22416461227D', '2026-09-13T00:00:00+00:00');
                    """;
                await insert.ExecuteNonQueryAsync();
            }

            await SqliteSchema.EnsureCreatedAsync(connection);

            await using var read = connection.CreateCommand();
            read.CommandText = """
                SELECT document_id, commit_ulid, revision, context, updated_at_ms
                FROM documents
                WHERE tenant = 'tenant-legacy' AND collection = 'users' AND id = 'legacy-1';
                """;
            await using var reader = await read.ExecuteReaderAsync();
            await Assert.That(await reader.ReadAsync()).IsTrue();
            await Assert.That(reader.GetString(0)).IsEqualTo("legacy-1");
            await Assert.That(reader.GetString(1)).IsEqualTo(string.Empty);
            await Assert.That(reader.GetInt64(2)).IsEqualTo(2L);
            await Assert.That(((byte[])reader[3]).Length).IsEqualTo(0);
            await Assert.That(reader.GetInt64(4)).IsEqualTo(1789257600000L);
            await Assert.That(Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;") ))
                .IsEqualTo(SqliteSchema.CurrentVersion);
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

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"terrabase-migration-compat-{Guid.NewGuid():N}.sqlite3");

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
                // concurrent SQLite checkpoint may still have a sidecar open.
            }
        }
    }
}
