using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class SchemaMigrationTests
{
    [Test]
    public async Task A_version_3_database_persists_its_incarnation_across_reopen_and_gives_copies_new_identities()
    {
        // Its own data directory: a second server reopens it once the first stopped.
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-v3-").FullName;
        try
        {
            var id = DocumentApi.NewId();
            string route, copyRoute, migratedVersion, copiedVersion, copiedDocument, organization;
            TenantContext sourceTenant, copyTenant;
            Guid sourceIncarnation, copyIncarnation;
            await using (var api = await DocumentApi.StartAsync(settings => settings["data-directory"] = dataDirectory))
            {
                organization = api.Tenant.OrganizationId;
                var path = await api.ReplaceWithOlderFileAsync("v3", async path =>
                {
                    await using var connection = new SqliteConnection(DocumentApi.ConnectionString(path));
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
                });

                route = $"{api.DatabasePath("v3")}/collections/items/documents/{id}";
                var migrated = await api.SendAsync(HttpMethod.Get, route);
                migrated.Status.ShouldBe(HttpStatusCode.OK);
                migratedVersion = migrated.Version;
                var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
                sourceTenant = await api.TenantOfAsync("v3");
                var store = (await engine.OpenDatabaseAsync(sourceTenant, CancellationToken.None))!;
                await Assert.That(store.Incarnation.Version).IsEqualTo(4);
                sourceIncarnation = store.Incarnation;
                await using (var check = new SqliteConnection(DocumentApi.ConnectionString(path, readOnly: true)))
                {
                    await check.OpenAsync();
                    await using var command = check.CreateCommand();
                    command.CommandText = "PRAGMA user_version;";
                    await Assert.That(await command.ExecuteScalarAsync()).IsEqualTo(8L);
                    command.CommandText = "SELECT incarnation FROM commit_offset WHERE id = 1;";
                    await Assert.That(Guid.TryParseExact((string?)await command.ExecuteScalarAsync(), "N", out var persisted)).IsTrue();
                    await Assert.That(store.Incarnation).IsEqualTo(persisted);
                }

                // A copy (a branch, through the Management API) gets its own identity.
                var source = (await api.SendAsync(HttpMethod.Put, api.DatabasePath("v3"))).Json.GetProperty("id").GetString();
                using var owner = api.Management.As(api.Tenant.User);
                using var branched = await owner.PostAsJsonAsync(
                    new Uri($"/v1/management/organizations/{organization}/projects/{api.Tenant.ProjectId}/databases/{source}/branches", UriKind.Relative),
                    new { name = "copied" });
                branched.StatusCode.ShouldBe(HttpStatusCode.Created);
                copyRoute = $"{api.DatabasePath("copied")}/collections/items/documents/{id}";
                var copied = await api.SendAsync(HttpMethod.Get, copyRoute);
                await Assert.That(copied.Status).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(copied.Version).IsNotEqualTo(migratedVersion);
                await Assert.That(copied.Json.GetProperty("document").GetRawText()).IsEqualTo(migrated.Json.GetProperty("document").GetRawText());
                copiedVersion = copied.Version;
                copyTenant = await api.TenantOfAsync("copied");
                var copiedStore = (await engine.OpenDatabaseAsync(copyTenant, CancellationToken.None))!;
                await Assert.That(copiedStore.Incarnation.Version).IsEqualTo(4);
                await Assert.That(copiedStore.Incarnation).IsNotEqualTo(sourceIncarnation);
                copyIncarnation = copiedStore.Incarnation;
                copiedDocument = copied.Json.GetProperty("document").GetRawText();
            }

            // Another server opens the same persisted files with a new engine and store cache.
            await using var reopened = await ManagementApi.StartAsync(settings => settings["data-directory"] = dataDirectory);
            using var client = reopened.DataClient(await reopened.Server.IssueAsync(organization, "documents:read", "documents:write"));
            var afterReopen = await DocumentApi.SendAsync(client, HttpMethod.Get, route);
            await Assert.That(afterReopen.Version).IsEqualTo(migratedVersion);
            var unchanged = await DocumentApi.SendAsync(client, HttpMethod.Get, route, configure: DocumentApi.IfNoneMatch(migratedVersion));
            await Assert.That(unchanged.Status).IsEqualTo(HttpStatusCode.NotModified);
            var copiedReopened = await DocumentApi.SendAsync(client, HttpMethod.Get, copyRoute);
            await Assert.That(copiedReopened.Version).IsEqualTo(copiedVersion);
            await Assert.That(copiedReopened.Json.GetProperty("document").GetRawText()).IsEqualTo(copiedDocument);
            // Incarnations are persisted: the new engine reads the same ones, the copy's distinct from its source's.
            var reopenedEngine = reopened.Server.Services.GetRequiredService<IStorageEngine>();
            await Assert.That((await reopenedEngine.OpenDatabaseAsync(sourceTenant, CancellationToken.None))!.Incarnation).IsEqualTo(sourceIncarnation);
            await Assert.That((await reopenedEngine.OpenDatabaseAsync(copyTenant, CancellationToken.None))!.Incarnation).IsEqualTo(copyIncarnation);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_migrated_maximum_version_refuses_writes_without_changing_data_or_offset()
    {
        await using var api = await DocumentApi.StartAsync();
        var path = await api.ReplaceWithOlderFileAsync("exhausted", async path =>
        {
            await using var connection = new SqliteConnection(DocumentApi.ConnectionString(path));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT NOT NULL,
                    PRIMARY KEY (collection, id)) WITHOUT ROWID;
                INSERT INTO documents VALUES ('items', 'Legacy', 9223372036854775807, '{"n":1}');
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        });

        var route = $"{api.DatabasePath("exhausted")}/collections/items/documents/Legacy";
        var original = await api.SendAsync(HttpMethod.Get, route);
        var put = await api.SendAsync(HttpMethod.Put, route, new { n = 2 });
        var deleted = await api.SendAsync(HttpMethod.Delete, route);
        var transaction = await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath("exhausted")}/transactions", new
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
        await using var check = new SqliteConnection(DocumentApi.ConnectionString(path, readOnly: true));
        await check.OpenAsync();
        await using var counter = check.CreateCommand();
        counter.CommandText = "SELECT value FROM commit_offset WHERE id = 1;";
        await Assert.That(await counter.ExecuteScalarAsync()).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task A_version_2_database_gains_the_live_document_index_on_first_open()
    {
        await using var api = await DocumentApi.StartAsync();
        var path = await api.ReplaceWithOlderFileAsync("v2", async path =>
        {
            await using var connection = new SqliteConnection(DocumentApi.ConnectionString(path));
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
        });

        var result = await api.SendAsync(HttpMethod.Get, $"{api.DatabasePath("v2")}/collections/people/documents/customer-1");
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
        await api.ReplaceWithOlderFileAsync("legacy", async file =>
        {
            var connection = new SqliteConnection(DocumentApi.ConnectionString(file));
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
        });

        var path = $"{api.DatabasePath("legacy")}/collections/people/documents/{Uri.EscapeDataString(id)}";
        var collections = await api.SendAsync(HttpMethod.Get, $"{api.DatabasePath("legacy")}/collections");
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
        var transacted = await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath("legacy")}/transactions", new
        {
            ops = new object[] { new { op = "put", collection = "people", id, document = new { name = "Eva" }, if_match = recreated.Version } },
        });
        var listed = await api.SendAsync(HttpMethod.Get, $"{api.DatabasePath("legacy")}/collections/people/documents");
        var queried = await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath("legacy")}/collections/people/query", new { filter = new { eq = new[] { "$id", id } } });
        var newLegacy = await api.SendAsync(HttpMethod.Put, $"{api.DatabasePath("legacy")}/collections/people/documents/customer-2", new { name = "new" });
        var newLegacyTransaction = await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath("legacy")}/transactions", new
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
