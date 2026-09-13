// <copyright file="SqliteStorageTests.cs" company="TerraScale">
// TerraBase storage contract tests.
// </copyright>

namespace TerraBase.Api.Tests.Storage;

using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>SQLite persistence rows for the TerraBase storage port.</summary>
public sealed class SqliteStorageTests
{
    /// <summary>SQLite opens in WAL mode and creates the durable schema.</summary>
    [Test]
    public async Task OpenCreatesWalSchemaAndOpaqueBodyStorage()
    {
        var path = NewPath();
        try
        {
            await using var provider = new TerraBase.SqliteStorageProvider(path);
            var key = Key("schema", "documents", "one");
            using var body = JsonDocument.Parse("{\"name\":\"Ada\",\"nested\":{\"ok\":true}} ");

            var result = await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);

            await Assert.That(result.Outcome).IsEqualTo(TerraBase.WriteOutcome.Created);
            await using var connection = OpenInspectionConnection(path);
            await using var journalMode = connection.CreateCommand();
            journalMode.CommandText = "PRAGMA journal_mode;";
            await Assert.That((string?)await journalMode.ExecuteScalarAsync()).IsEqualTo("wal");

            await using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            await using var reader = await schema.ExecuteReaderAsync();
            var tables = new List<string>();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            await Assert.That(tables).Contains("documents");
            await Assert.That(tables).Contains("version_watermarks");
            await Assert.That(tables).Contains("publication_journal");

            await using var bodyCommand = connection.CreateCommand();
            bodyCommand.CommandText = "SELECT typeof(body), length(body) FROM documents;";
            await using var bodyReader = await bodyCommand.ExecuteReaderAsync();
            await Assert.That(await bodyReader.ReadAsync()).IsTrue();
            await Assert.That(bodyReader.GetString(0)).IsEqualTo("blob");
            await Assert.That(bodyReader.GetInt64(1)).IsGreaterThan(0);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>Document writes, deletes, and watermarks survive reopening.</summary>
    [Test]
    public async Task ReopenRetainsDocumentAndMonotonicVersionAfterDelete()
    {
        var path = NewPath();
        var key = Key("durable", "documents", "one");
        try
        {
            await using (var provider = new TerraBase.SqliteStorageProvider(path))
            {
                using var body = JsonDocument.Parse("{\"value\":1}");
                var created = await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
                await Assert.That(created.Document?.Version).IsEqualTo(1ul);
                var deleted = await provider.DeleteAsync(key, DocumentPrecondition.Version(1));
                await Assert.That(deleted.Outcome).IsEqualTo(TerraBase.WriteOutcome.Deleted);
                await Assert.That(await provider.ReadAsync(key)).IsNull();

                using var recreatedBody = JsonDocument.Parse("{\"value\":2}");
                var recreated = await provider.UpsertAsync(key, recreatedBody.RootElement, DocumentPrecondition.Missing);
                await Assert.That(recreated.Document?.Version).IsEqualTo(3ul);
            }

            await using (var reopened = new TerraBase.SqliteStorageProvider(path))
            {
                var read = await reopened.ReadAsync(key);
                await Assert.That(read?.Version).IsEqualTo(3ul);
                await Assert.That(read?.Body.GetProperty("value").GetInt32()).IsEqualTo(2);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>Stale CAS writes do not append a document or journal row.</summary>
    [Test]
    public async Task StalePreconditionLeavesAtomicStateUnchanged()
    {
        var path = NewPath();
        var key = Key("cas", "documents", "one");
        try
        {
            await using var provider = new TerraBase.SqliteStorageProvider(path);
            using var firstBody = JsonDocument.Parse("{\"value\":1}");
            await provider.UpsertAsync(key, firstBody.RootElement, DocumentPrecondition.Missing);
            using var rejectedBody = JsonDocument.Parse("{\"value\":2}");

            var rejected = await provider.UpsertAsync(key, rejectedBody.RootElement, DocumentPrecondition.Version(99));

            await Assert.That(rejected.Outcome).IsEqualTo(TerraBase.WriteOutcome.Conflict);
            var current = await provider.ReadAsync(key);
            await Assert.That(current?.Version).IsEqualTo(1ul);
            await Assert.That(current?.Body.GetProperty("value").GetInt32()).IsEqualTo(1);

            await using var connection = OpenInspectionConnection(path);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM publication_journal;";
            await Assert.That(Convert.ToInt64(await command.ExecuteScalarAsync())).IsEqualTo(1L);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>Several SQLite mutations commit as one journal-visible transaction.</summary>
    [Test]
    public async Task BatchCommitsMutationsAndReturnsJournalSequenceRange()
    {
        var path = NewPath();
        var existingKey = Key("batch", "documents", "existing");
        var createdKey = Key("batch", "documents", "created");
        try
        {
            await using var provider = new TerraBase.SqliteStorageProvider(path);
            using var initialBody = JsonDocument.Parse("{\"value\":0}");
            await provider.UpsertAsync(existingKey, initialBody.RootElement, DocumentPrecondition.Missing);
            using var createdBody = JsonDocument.Parse("{\"value\":1}");

            var batch = await provider.ApplyBatchAsync([
                TerraBase.StorageMutation.Upsert(createdKey, createdBody.RootElement, DocumentPrecondition.Missing),
                TerraBase.StorageMutation.Delete(existingKey, DocumentPrecondition.Version(1)),
            ]);

            await Assert.That(batch.Outcome).IsEqualTo(TerraBase.StorageBatchOutcome.Committed);
            await Assert.That(batch.Writes).Count().IsEqualTo(2);
            await Assert.That(batch.Writes[0].Outcome).IsEqualTo(TerraBase.WriteOutcome.Created);
            await Assert.That(batch.Writes[1].Outcome).IsEqualTo(TerraBase.WriteOutcome.Deleted);
            await Assert.That(batch.FirstSequence).IsEqualTo(2ul);
            await Assert.That(batch.LastSequence).IsEqualTo(3ul);
            await Assert.That((await provider.ReadAsync(createdKey))?.Body.GetProperty("value").GetInt32()).IsEqualTo(1);
            await Assert.That(await provider.ReadAsync(existingKey)).IsNull();

            await using var connection = OpenInspectionConnection(path);
            await using var journal = connection.CreateCommand();
            journal.CommandText = "SELECT COUNT(*) FROM publication_journal;";
            await Assert.That(Convert.ToInt64(await journal.ExecuteScalarAsync())).IsEqualTo(3L);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>A failed mutation rolls back earlier mutations in the same batch.</summary>
    [Test]
    public async Task BatchConflictRollsBackDocumentsAndJournalRows()
    {
        var path = NewPath();
        var existingKey = Key("batch-conflict", "documents", "existing");
        var createdKey = Key("batch-conflict", "documents", "created");
        try
        {
            await using var provider = new TerraBase.SqliteStorageProvider(path);
            using var initialBody = JsonDocument.Parse("{\"value\":0}");
            await provider.UpsertAsync(existingKey, initialBody.RootElement, DocumentPrecondition.Missing);
            using var createdBody = JsonDocument.Parse("{\"value\":1}");

            var batch = await provider.ApplyBatchAsync([
                TerraBase.StorageMutation.Upsert(createdKey, createdBody.RootElement, DocumentPrecondition.Missing),
                TerraBase.StorageMutation.Upsert(existingKey, createdBody.RootElement, DocumentPrecondition.Version(99)),
            ]);

            await Assert.That(batch.Outcome).IsEqualTo(TerraBase.StorageBatchOutcome.Conflict);
            await Assert.That(batch.Writes).Count().IsEqualTo(0);
            await Assert.That(batch.FirstSequence).IsNull();
            await Assert.That(batch.LastSequence).IsNull();
            await Assert.That(await provider.ReadAsync(createdKey)).IsNull();
            await Assert.That((await provider.ReadAsync(existingKey))?.Body.GetProperty("value").GetInt32()).IsEqualTo(0);

            await using var connection = OpenInspectionConnection(path);
            await using var journal = connection.CreateCommand();
            journal.CommandText = "SELECT COUNT(*) FROM publication_journal;";
            await Assert.That(Convert.ToInt64(await journal.ExecuteScalarAsync())).IsEqualTo(1L);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>The SQLite engine composition root exposes its atomic batch capability.</summary>
    [Test]
    public async Task EngineAppliesSqliteBatchThroughItsStoragePort()
    {
        var path = NewPath();
        var key = Key("batch-engine", "documents", "one");
        try
        {
            await using var engine = TerraBase.TerraBaseEngine.OpenSqlite(path);
            using var body = JsonDocument.Parse("{\"value\":7}");

            var batch = await engine.ApplyBatchAsync([
                TerraBase.StorageMutation.Upsert(key, body.RootElement, DocumentPrecondition.Missing),
            ]);

            await Assert.That(batch.Outcome).IsEqualTo(TerraBase.StorageBatchOutcome.Committed);
            await Assert.That((await engine.Documents.ReadAsync(key))?.Body.GetProperty("value").GetInt32()).IsEqualTo(7);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>Collection scans use deterministic binary identifier order.</summary>
    [Test]
    public async Task ScanReturnsOnlyRequestedCollectionInBinaryOrder()
    {
        var path = NewPath();
        try
        {
            await using var provider = new TerraBase.SqliteStorageProvider(path);
            using var body = JsonDocument.Parse("{\"ok\":true}");
            await provider.UpsertAsync(Key("scan", "users", "z"), body.RootElement, DocumentPrecondition.Missing);
            await provider.UpsertAsync(Key("scan", "users", "a"), body.RootElement, DocumentPrecondition.Missing);
            await provider.UpsertAsync(Key("scan", "orders", "b"), body.RootElement, DocumentPrecondition.Missing);

            var ids = new List<string>();
            await foreach (var document in provider.ScanAsync(new TerraBase.TenantId("scan"), new TerraBase.CollectionId("users")))
            {
                ids.Add(document.Key.DocumentId);
            }

            await Assert.That(ids).IsEquivalentTo(["a", "z"]);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static TerraBase.DocumentKey Key(string tenant, string collection, string document) =>
        new(new TerraBase.TenantId(tenant), new TerraBase.CollectionId(collection), document);

    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"terrabase-sqlite-{Guid.NewGuid():N}.sqlite3");

    private static SqliteConnection OpenInspectionConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        connection.Open();
        return connection;
    }

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
                // The test process can still be releasing a read handle.
            }
        }
    }
}
