using System.Net;
using System.Text;
using System.Text.Json;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class EntityIdTests
{
    [Test]
    public async Task Generated_ids_are_monotonic_ULIDs_at_one_millisecond_and_payload_identity_is_the_key()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        var ids = new List<string>();
        for (var i = 0; i < 16; i++)
        {
            var created = await api.SendAsync(HttpMethod.Post, api.Documents, new { value = i });
            await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
            var id = created.Json.GetProperty("id").GetString()!;
            await Assert.That(EntityIds.IsValid(id)).IsTrue();
            await Assert.That(Ulid.Parse(id).Time).IsEqualTo(clock.GetUtcNow());
            await Assert.That(created.Location!.OriginalString).IsEqualTo(api.Document(id));
            var read = await api.GetAsync(id);
            await Assert.That(read.Json.GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo(i);
            ids.Add(id);
        }

        var transaction = await api.TransactAsync(
            new { op = "create", collection = "items", document = new { value = 16 } },
            new { op = "create", collection = "items", document = new { value = 17 } });
        await Assert.That(transaction.Status).IsEqualTo(HttpStatusCode.OK);
        ids.AddRange(transaction.Json.GetProperty("results").EnumerateArray().Select(result => result.GetProperty("id").GetString()!));
        await Assert.That(ids.All(EntityIds.IsValid)).IsTrue();
        await Assert.That(ids.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(ids.Count);
        await Assert.That(ids).IsEquivalentTo(ids.Order(StringComparer.Ordinal));
        for (var i = 1; i < ids.Count; i++)
        {
            await Assert.That(string.CompareOrdinal(ids[i - 1], ids[i])).IsLessThan(0);
        }

        var duplicate = await api.PutAsync(ids[0], new { id = ids[0], value = "key owns identity" });
        var stored = await api.GetAsync(ids[0]);
        await Assert.That(duplicate.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(stored.Json.GetProperty("document").TryGetProperty("id", out _)).IsFalse();
        await Assert.That(stored.Version).IsEqualTo(duplicate.Version);
    }

    [Test]
    [Arguments("00112233-4455-6677-8899-aabbccddeeff")]
    [Arguments("org_01JC9XQ1T3V5X7Z9B2D4F7HJ2K")]
    [Arguments("01jc9xq1t3v5x7z9b2d4f7hj2k")]
    [Arguments("81JC9XQ1T3V5X7Z9B2D4F7HJ2K")]
    public async Task Entity_routes_reject_noncanonical_identifiers(string id)
    {
        await using var api = await ManagementApi.StartAsync();
        using var route = await api.Client.GetAsync(ManagementApi.Uri($"/v1/management/organizations/{id}"));
        await Assert.That(route.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    [Arguments("01jc9xq1t3v5x7z9b2d4f7hj2k")]
    [Arguments("81JC9XQ1T3V5X7Z9B2D4F7HJ2K")]
    [Arguments("01JC9XQ1T3V5X7Z9B2D4F7HJ2\"")]
    public async Task Invalid_collection_entity_ids_cannot_commit(string id)
    {
        await using var api = await DocumentApi.StartAsync();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
        await using (var write = await store.BeginTransactionAsync(write: true, token))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => write.CreateCollectionAsync(new CollectionInfo("invalid", 0, Id: id), token).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => write.CommitAsync(token).AsTask());
        }

        var read = await api.SendAsync(HttpMethod.Get, api.Collections + "/invalid");
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments('<')]
    [Arguments('é')]
    [Arguments('\u2028')]
    public async Task Removing_duplicate_identity_keeps_near_limit_raw_documents_within_the_storage_byte_limit(char character)
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var value = new string(character, (DocumentJson.MaxBytes - 128) / Encoding.UTF8.GetByteCount(character.ToString()));
        var plain = await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()),
            $$"""{"value":"{{value}}"}""", "application/json");
        await Assert.That(plain.Status).IsEqualTo(HttpStatusCode.Created);
        var created = await api.SendRawAsync(HttpMethod.Put, api.Document(id),
            $$"""{"id":"{{id}}","value":"{{value}}"}""", "application/json");
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var read = await api.GetAsync(id);
        await Assert.That(read.Json.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
        await Assert.That(read.Json.GetProperty("document").TryGetProperty("id", out _)).IsFalse();
    }

    [Test]
    public async Task Patch_validates_the_complete_value_count_before_removing_duplicate_identity()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var created = await api.PutAsync(id, new { values = new int[4094] });
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var patched = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), $$"""{"id":"{{id}}"}""", "application/merge-patch+json");
        await Assert.That((patched.Status, patched.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        var read = await api.GetAsync(id);
        await Assert.That(read.Version).IsEqualTo(created.Version);
        await Assert.That(read.Json.GetProperty("document").GetProperty("values").GetArrayLength()).IsEqualTo(4094);
    }

    [Test]
    public async Task Patch_of_a_deep_stored_document_returns_validation_and_can_remove_its_deep_subtree()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var created = await api.PutAsync(id, new { value = 0 });
        var deep = "{\"deep\":" + string.Concat(Enumerable.Repeat("{\"a\":", 80)) + "1" + new string('}', 80) + "}";
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync())))
        {
            await connection.OpenAsync();
            // Deeper than the API admits: written with its state, as an earlier write path could have stored it.
            await StoredStates.WriteAsync(connection, "items", id, deep);
        }

        var rejected = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), """{"value":1}""", "application/merge-patch+json");
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync())))
        {
            await connection.OpenAsync();
            await using var readStored = connection.CreateCommand();
            readStored.CommandText = "SELECT doc FROM \"items\" WHERE id = $id;";
            readStored.Parameters.AddWithValue("$id", id);
            await Assert.That((string)(await readStored.ExecuteScalarAsync())!).IsEqualTo(deep);
        }

        var repaired = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), $$"""{"deep":null,"id":"{{id}}","value":1}""", "application/merge-patch+json");
        await Assert.That(repaired.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(repaired.Version).IsNotEqualTo(created.Version);
        var read = await api.GetAsync(id);
        await Assert.That(read.Json.GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo(1);
        await Assert.That(read.Json.GetProperty("document").TryGetProperty("deep", out _)).IsFalse();
        await Assert.That(read.Json.GetProperty("document").TryGetProperty("id", out _)).IsFalse();
    }

    [Test]
    public async Task Catalog_collections_and_credentials_store_ULIDs_without_duplicate_payload_ids()
    {
        await using var api = await DocumentApi.StartAsync();
        await Assert.That(EntityIds.IsValid(api.Tenant.OrganizationId)).IsTrue();
        await Assert.That(EntityIds.IsValid(api.Tenant.ProjectId)).IsTrue();
        var database = await api.TenantOfAsync("db");
        await Assert.That(EntityIds.IsValid(database.DatabaseId)).IsTrue();
        var collection = await api.SendAsync(HttpMethod.Get, $"{api.Collections}/items");
        await Assert.That(EntityIds.IsValid(collection.Json.GetProperty("id").GetString())).IsTrue();
        using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(api.Server.DataDirectory, "_system.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }));
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT id, body FROM documents WHERE collection IN ('organizations', 'api_keys') AND body IS NOT NULL;";
        using var rows = await query.ExecuteReaderAsync();
        var count = 0;
        while (await rows.ReadAsync())
        {
            await Assert.That(EntityIds.IsValid(rows.GetString(0))).IsTrue();
            using var body = JsonDocument.Parse(rows.GetString(1));
            await Assert.That(body.RootElement.TryGetProperty("id", out _)).IsFalse();
            count++;
        }

        await Assert.That(count).IsGreaterThanOrEqualTo(2);
    }
}
