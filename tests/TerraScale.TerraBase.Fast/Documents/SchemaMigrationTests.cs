using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Documents;

public sealed class SchemaMigrationTests
{
    [Test]
    public async Task A_version_3_database_persists_its_incarnation_across_reopen_and_gives_copies_new_identities()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var path = Path.Combine(api.Server.DataDirectory, "v3.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT,
                    PRIMARY KEY (collection, id)) WITHOUT ROWID;
                CREATE TABLE collections (name TEXT PRIMARY KEY, created_at INTEGER NOT NULL) WITHOUT ROWID;
                CREATE TABLE commit_offset (id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL);
                CREATE INDEX documents_live ON documents (collection, id) WHERE body IS NOT NULL;
                INSERT INTO collections VALUES ('items', 1);
                INSERT INTO commit_offset VALUES (1, 7);
                INSERT INTO documents VALUES ('items', $id, 7, '{"n":1}');
                PRAGMA user_version = 3;
                """;
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync();
        }

        var route = $"/v1/databases/v3/collections/items/documents/{id}";
        var migrated = await api.SendAsync(HttpMethod.Get, route);
        migrated.Status.ShouldBe(HttpStatusCode.OK);
        await using (var check = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly"))
        {
            await check.OpenAsync();
            await using var command = check.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            await Assert.That(await command.ExecuteScalarAsync()).IsEqualTo(4L);
            command.CommandText = "SELECT incarnation FROM commit_offset WHERE id = 1;";
            await Assert.That(Guid.TryParseExact((string?)await command.ExecuteScalarAsync(), "N", out _)).IsTrue();
        }

        // A second application instance opens the same persisted files with a new engine/store cache.
        using var tls = new ClientTls();
        await using var reopened = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            settings["data-directory"] = api.Server.DataDirectory;
        });
        using var client = tls.CreateClient(reopened.ClientAddress!, await reopened.IssueAsync("documents:read", "documents:write"));
        var afterReopen = await DocumentApi.SendAsync(client, HttpMethod.Get, route);
        await Assert.That(afterReopen.Version).IsEqualTo(migrated.Version);
        var unchanged = await DocumentApi.SendAsync(client, HttpMethod.Get, route, configure: DocumentApi.IfNoneMatch(migrated.Version));
        await Assert.That(unchanged.Status).IsEqualTo(HttpStatusCode.NotModified);

        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await Assert.That(DatabaseName.TryParse("v3", out var source)).IsTrue();
        await Assert.That(DatabaseName.TryParse("copied", out var target)).IsTrue();
        await Assert.That(await engine.CopyDatabaseAsync(source, target, CancellationToken.None)).IsTrue();
        var copyRoute = $"/v1/databases/copied/collections/items/documents/{id}";
        var copied = await api.SendAsync(HttpMethod.Get, copyRoute);
        await Assert.That(copied.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(copied.Version).IsNotEqualTo(migrated.Version);
        await Assert.That(copied.Json.GetProperty("document").GetRawText()).IsEqualTo(migrated.Json.GetProperty("document").GetRawText());
        var copiedReopened = await DocumentApi.SendAsync(client, HttpMethod.Get, copyRoute);
        await Assert.That(copiedReopened.Version).IsEqualTo(copied.Version);
    }

    [Test]
    public async Task A_migrated_maximum_version_refuses_writes_without_changing_data_or_offset()
    {
        await using var api = await DocumentApi.StartAsync();
        var path = Path.Combine(api.Server.DataDirectory, "exhausted.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT NOT NULL,
                    PRIMARY KEY (collection, id)) WITHOUT ROWID;
                INSERT INTO documents VALUES ('items', 'Legacy', 9223372036854775807, '{"n":1}');
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        const string route = "/v1/databases/exhausted/collections/items/documents/Legacy";
        var original = await api.SendAsync(HttpMethod.Get, route);
        var put = await api.SendAsync(HttpMethod.Put, route, new { n = 2 });
        var deleted = await api.SendAsync(HttpMethod.Delete, route);
        var transaction = await api.SendAsync(HttpMethod.Post, "/v1/databases/exhausted/transactions", new
        {
            ops = new[] { new { op = "put", collection = "items", id = "Legacy", document = new { n = 3 } } },
        });
        foreach (var response in new[] { put, deleted, transaction })
        {
            await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.ServiceUnavailable, "capacity"));
        }

        await Assert.That(transaction.Op).IsEqualTo(0);
        var unchanged = await api.SendAsync(HttpMethod.Get, route);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await using var check = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");
        await check.OpenAsync();
        await using var counter = check.CreateCommand();
        counter.CommandText = "SELECT value FROM commit_offset WHERE id = 1;";
        await Assert.That(await counter.ExecuteScalarAsync()).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task A_version_2_database_gains_the_live_document_index_on_first_open()
    {
        await using var api = await DocumentApi.StartAsync();
        var path = Path.Combine(api.Server.DataDirectory, "v2.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT,
                    PRIMARY KEY (collection, id)) WITHOUT ROWID;
                CREATE TABLE collections (name TEXT PRIMARY KEY, created_at INTEGER NOT NULL) WITHOUT ROWID;
                CREATE TABLE commit_offset (id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL);
                INSERT INTO collections VALUES ('people', 1);
                INSERT INTO commit_offset VALUES (1, 7);
                INSERT INTO documents VALUES ('people', 'customer-1', 7, '{"name":"Ana"}');
                PRAGMA user_version = 2;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var result = await api.SendAsync(HttpMethod.Get, "/v1/databases/v2/collections/people/documents/customer-1");
        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        var plan = await QueryPlan.ExplainAsync(path,
            "SELECT id FROM documents INDEXED BY documents_live WHERE collection = $collection AND body IS NOT NULL ORDER BY id LIMIT $limit;");
        await Assert.That(plan.Contains("documents_live", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments("customer-1")]
    [Arguments("Customer-1@Legacy+Tag?#")]
    [Arguments("00000000-0000-7000-8000-000000000001")]
    public async Task A_version_1_database_preserves_existing_ids_on_first_open(string id)
    {
        await using var api = await DocumentApi.StartAsync();
        // The schema-1 layout: no collections table, per-document version counters.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(api.Server.DataDirectory, "legacy.db"),
            Pooling = false,
        }.ToString());
        await using (connection)
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            await using (command)
            {
                command.CommandText = $$"""
                    CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT NOT NULL,
                        PRIMARY KEY (collection, id)) WITHOUT ROWID;
                    INSERT INTO documents VALUES ('people', '{{id}}', 7, '{"name":"Ana"}');
                    PRAGMA user_version = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }
        }

        var path = $"/v1/databases/legacy/collections/people/documents/{Uri.EscapeDataString(id)}";
        var collections = await api.SendAsync(HttpMethod.Get, "/v1/databases/legacy/collections");
        var read = await api.SendAsync(HttpMethod.Get, path);
        var written = await api.SendAsync(HttpMethod.Put, path, new { name = "Bia" });

        await Assert.That(collections.Json.GetProperty("items")[0].GetProperty("name").GetString()).IsEqualTo("people");
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.Json.GetProperty("document").GetProperty("name").GetString()).IsEqualTo("Ana");
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(written.Version).IsNotEqualTo(read.Version);
        // Offsets continue after every schema-1 version, so no document state reuses a version.
        await Assert.That(long.Parse(written.Json.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture)).IsEqualTo(8);

        var patched = await api.SendRawAsync(HttpMethod.Patch, path, """{"name":"Cris"}""", "application/merge-patch+json");
        var deleted = await api.SendAsync(HttpMethod.Delete, path);
        var tombstone = await api.SendAsync(HttpMethod.Get, path);
        var recreated = await api.SendAsync(HttpMethod.Put, path, new { name = "Dani" }, DocumentApi.IfMatch(deleted.Version));
        var followLocation = await api.SendAsync(HttpMethod.Get, recreated.Location!.OriginalString);
        var transacted = await api.SendAsync(HttpMethod.Post, "/v1/databases/legacy/transactions", new
        {
            ops = new object[] { new { op = "put", collection = "people", id, document = new { name = "Eva" }, if_match = recreated.Version } },
        });
        var listed = await api.SendAsync(HttpMethod.Get, "/v1/databases/legacy/collections/people/documents");
        var queried = await api.SendAsync(HttpMethod.Post, "/v1/databases/legacy/collections/people/query", new { filter = new { eq = new[] { "$id", id } } });
        var newLegacy = await api.SendAsync(HttpMethod.Put, "/v1/databases/legacy/collections/people/documents/customer-2", new { name = "new" });
        var newLegacyTransaction = await api.SendAsync(HttpMethod.Post, "/v1/databases/legacy/transactions", new
        {
            ops = new object[] { new { op = "create", collection = "people", id = "customer-2", document = new { } } },
        });

        await Assert.That(patched.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(tombstone.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(tombstone.ETag).IsEqualTo(deleted.Version);
        await Assert.That(recreated.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(recreated.Location!.OriginalString).IsEqualTo(path);
        await Assert.That(followLocation.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(followLocation.Json.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(transacted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(listed.Ids).IsEquivalentTo([id]);
        await Assert.That(queried.Ids).IsEquivalentTo([id]);
        await Assert.That((newLegacy.Status, newLegacy.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        await Assert.That((newLegacyTransaction.Status, newLegacyTransaction.Code, newLegacyTransaction.Op))
            .IsEqualTo((HttpStatusCode.BadRequest, "invalid_request", 0));
    }
}
