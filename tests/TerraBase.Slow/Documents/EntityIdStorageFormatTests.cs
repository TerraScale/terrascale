using System.Net;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

public sealed class EntityIdStorageFormatTests
{
    [Test]
    [Arguments(9)]
    [Arguments(10)]
    public async Task A_previous_snapshot_is_refused_before_import_publication(int schemaVersion)
    {
        await using var api = await DocumentApi.StartAsync();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var backup = api.Server.Services.GetRequiredService<IWalBackup>();
        var created = await api.SendAsync(HttpMethod.Put, api.DatabasePath("imported"));
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var target = await api.TenantOfAsync("imported");
        var targetPath = await api.DatabaseFileAsync("imported");
        await engine.DeleteDatabaseAsync(target, token);
        var directory = Directory.CreateTempSubdirectory("terrabase-old-snapshot-").FullName;
        try
        {
            var snapshot = Path.Combine(directory, "snapshot.db");
            TestMasterKey.Create(snapshot);
            await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(snapshot)))
            {
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                // GUID schema 9 lacks entity_id; ULID schema 10 lacks uniqueness storage.
                var identity = schemaVersion == 9 ? "00112233-4455-6677-8899-aabbccddeeff" : DocumentApi.NewId();
                var identityColumn = schemaVersion == 9 ? "" : ", entity_id TEXT NOT NULL CHECK (length(entity_id) = 26)";
                var collectionIdentity = schemaVersion == 9 ? "" : $", '{DocumentApi.NewId()}'";
                command.CommandText = $$"""
                    CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT,
                        PRIMARY KEY (collection, id));
                    INSERT INTO documents VALUES ('items', '{{identity}}', 1, '{"value":"preserved"}');
                    CREATE TABLE collections (name TEXT PRIMARY KEY, created_at INTEGER NOT NULL, generation INTEGER NOT NULL{{identityColumn}});
                    INSERT INTO collections VALUES ('items', 0, 1{{collectionIdentity}});
                    CREATE TABLE commit_offset (id INTEGER PRIMARY KEY, value INTEGER NOT NULL, incarnation TEXT NOT NULL);
                    INSERT INTO commit_offset VALUES (1, 0, '{{identity}}');
                    CREATE TABLE changes (sequence INTEGER NOT NULL, ordinal INTEGER NOT NULL, collection TEXT NOT NULL,
                        id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT, PRIMARY KEY (sequence, ordinal));
                    CREATE INDEX changes_deleted ON changes (collection, id, version) WHERE body IS NULL;
                    CREATE INDEX documents_replication_keys ON documents (collection, id);
                    CREATE INDEX changes_replication_deleted ON changes (collection, id, version);
                    CREATE TABLE replication_state (id INTEGER PRIMARY KEY, complete INTEGER NOT NULL);
                    INSERT INTO replication_state VALUES (1, 1);
                    CREATE TABLE journal_prefixes (sequence INTEGER PRIMARY KEY, digest BLOB NOT NULL);
                    CREATE TABLE applied (origin BLOB PRIMARY KEY, sequence INTEGER NOT NULL, digest BLOB);
                    PRAGMA user_version = {{schemaVersion}};
                    """;
                await command.ExecuteNonQueryAsync(token);
                // Turso does not checkpoint on close, and the snapshot is copied without its log.
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await command.ExecuteNonQueryAsync(token);
            }

            var key = DocumentApi.NewId();
            var checksum = await backup.ChecksumAsync(snapshot, token);
            var imported = Path.Combine(directory, "import.db");
            File.Copy(snapshot, imported);
            File.Copy(snapshot + ".key", imported + ".key");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backup.ImportDatabaseAsync(target, imported, token).AsTask());
            await Assert.That(error!.Message).Contains("old database format");
            await Assert.That(File.Exists(targetPath)).IsFalse();
            var missing = await api.SendAsync(HttpMethod.Get, $"{api.DatabasePath("imported")}/collections/items/documents/by-key?id={key}");
            await Assert.That(missing.Status).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(await backup.ChecksumAsync(snapshot, token)).IsEquivalentTo(checksum);

            var written = await api.PutAsync(key, new { value = "fresh" });
            await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(await engine.CopyDatabaseAsync(await api.TenantOfAsync(), target, token)).IsTrue();
            var read = await api.SendAsync(HttpMethod.Get, $"{api.DatabasePath("imported")}/collections/items/documents/by-key?id={key}");
            await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(read.Json.GetProperty("document").GetProperty("value").GetString()).IsEqualTo("fresh");
            await using var current = new SqliteConnection(DocumentApi.ConnectionString(targetPath, readOnly: true));
            await current.OpenAsync(token);
            await using var version = current.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            await Assert.That((long)(await version.ExecuteScalarAsync(token))!).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
