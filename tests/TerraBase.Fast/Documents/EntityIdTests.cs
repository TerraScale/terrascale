using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Primitives;
using TerraBase.Testing;
using Turso.Data.Sqlite;

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
    public async Task Catalog_collections_and_credentials_store_ULIDs_without_duplicate_payload_ids()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
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
