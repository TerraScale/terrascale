using System.Net;
using System.Text.Json;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

public sealed class CountBoundTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Public_count_excludes_the_overflow_row_whether_or_not_it_matches(bool overflowMatches)
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        await api.SetPlanAsync(tenant.User, tenant.OrganizationId, "enterprise");
        var client = tenant.Data;
        using var registered = await client.PutAsync(tenant.Database("count"), null);
        registered.EnsureSuccessStatusCode();
        var id = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var database = new TenantContext(tenant.OrganizationId, PlanTier.Enterprise, tenant.ProjectId, id);
        await api.Server.Services.GetRequiredService<IStorageEngine>().DeleteDatabaseAsync(database, CancellationToken.None);
        var path = Path.Combine(api.Server.DataDirectory, tenant.OrganizationId, tenant.ProjectId, id + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await DocumentFiles.WriteAsync(path);
        await using (var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false })))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $$"""
                INSERT INTO collections VALUES ('items', 0, $collectionId);
                CREATE TABLE "items" (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);
                WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 100001),
                    bodies(n, body) AS (SELECT n, CASE WHEN n = 100001 THEN $last ELSE '{"n":1}' END FROM rows)
                INSERT INTO "items" (id, doc) SELECT printf('%026d', n), body FROM bodies;
                INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2)
                    SELECT 'items', id, '', {{StoredStates.Version}}, 0, 1, 0, 0 FROM "items";
                """;
            command.Parameters.AddWithValue("$collectionId", TerraBase.Primitives.EntityIds.New().ToString());
            command.Parameters.AddWithValue("$last", overflowMatches ? "{\"n\":1}" : "{\"n\":0}");
            await command.ExecuteNonQueryAsync();
        }

        using var response = await client.PostAsJsonAsync(tenant.Database("count", "/collections/items/query"),
            new { filter = new { eq = new object[] { "/n", 1 } }, limit = 1, count = true });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("count").GetRawText()).IsEqualTo("""{"value":"100000","exact":false}""");
    }

    [Test]
    public async Task Assert_count_examines_at_most_10000_rows_whatever_it_matches()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        await api.SetPlanAsync(tenant.User, tenant.OrganizationId, "pro");
        var client = tenant.Data;
        (await client.PutAsync(tenant.Database("db"), null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(tenant.Database("db", "/collections"), new { name = "items" })).EnsureSuccessStatusCode();
        var transactions = tenant.Database("db", "/transactions");
        string? someId = null;
        for (var batch = 0; batch < 10; batch++)
        {
            var ops = Enumerable.Range(0, 1000).Select(_ => new { op = "create", collection = "items", document = new { n = 1 } });
            using var created = await client.PostAsJsonAsync(transactions, new { ops });
            created.EnsureSuccessStatusCode();
            someId ??= (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0].GetProperty("id").GetString();
        }
        object sparse = new { ops = new[] { new { op = "assert_count", collection = "items", filter = new { exists = "/missing" }, lte = 0 } } };
        using var atBound = await client.PostAsJsonAsync(transactions, sparse);
        var churn = new object[]
        {
            new { op = "delete", collection = "items", id = someId },
            new { op = "create", collection = "items", document = new { n = 1 } },
        };
        (await client.PostAsJsonAsync(transactions, new { ops = churn })).EnsureSuccessStatusCode();
        using var pastBound = await client.PostAsJsonAsync(transactions, sparse);
        using var counted = await client.PostAsJsonAsync(
            tenant.Database("db", "/collections/items/query"), new { limit = 1, count = true });
        counted.EnsureSuccessStatusCode();
        var error = (await pastBound.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        var count = (await counted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count");
        var expensive = new
        {
            or = Enumerable.Range(0, 64).Select(_ => new
            {
                and = new object[] { new { exists = "/n" }, new { eq = new object[] { "/n", 999 } } },
            }),
        };
        var query = tenant.Database("db", "/collections/items/query");
        using var exhausted = await client.PostAsJsonAsync(query, new { filter = expensive, limit = 1 });
        using var exhaustedSorted = await client.PostAsJsonAsync(query, new { filter = expensive, sort = new[] { new { path = "$id" } }, limit = 1 });
        using var exhaustedCount = await client.PostAsJsonAsync(transactions,
            new { ops = new[] { new { op = "assert_count", collection = "items", filter = expensive, lte = 0 } } });
        var countError = (await exhaustedCount.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        var exhaustedError = (await exhausted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        using var resumed = await client.PostAsJsonAsync(query, new { limit = 1 });
        using var sorted = await client.PostAsJsonAsync(query, new { sort = new[] { new { path = "/n" } }, limit = 1 });
        (await client.PostAsJsonAsync(transactions, new { ops = new[] { new { op = "create", collection = "items", document = new { n = 1 } } } }))
            .EnsureSuccessStatusCode();
        using var tooManyToSort = await client.PostAsJsonAsync(query, new { sort = new[] { new { path = "/n" } }, limit = 1 });
        var sortError = (await tooManyToSort.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        var idSort = new[] { new { path = "$id" }, new { path = "/n" } };
        using var idSorted = await client.PostAsJsonAsync(query, new { sort = idSort, limit = 1 });
        var firstIdPage = await idSorted.Content.ReadFromJsonAsync<JsonElement>();
        using var nextIdSorted = await client.PostAsJsonAsync(query,
            new { sort = idSort, limit = 1, cursor = firstIdPage.GetProperty("next_cursor").GetString() });
        var secondIdPage = await nextIdSorted.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(idSorted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(nextIdSorted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(secondIdPage.GetProperty("items")[0].GetProperty("id").GetString())
            .IsNotEqualTo(firstIdPage.GetProperty("items")[0].GetProperty("id").GetString());

        await Assert.That(atBound.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(pastBound.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("assert_requires_index");
        await Assert.That(error.GetProperty("op").GetInt32()).IsEqualTo(0);
        await Assert.That(count.GetRawText()).IsEqualTo("""{"value":"10000","exact":true}""");
        await Assert.That((exhausted.StatusCode, exhaustedError.GetProperty("code").GetString()))
            .IsEqualTo((HttpStatusCode.BadRequest, "invalid_query"));
        await Assert.That(exhaustedError.GetProperty("message").GetString()).IsEqualTo("query too expensive");
        await Assert.That(exhaustedSorted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((exhaustedCount.StatusCode, countError.GetProperty("code").GetString(), countError.GetProperty("op").GetInt32()))
            .IsEqualTo((HttpStatusCode.BadRequest, "invalid_query", 0));
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sorted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((tooManyToSort.StatusCode, sortError.GetProperty("code").GetString()))
            .IsEqualTo((HttpStatusCode.BadRequest, "sort_requires_index"));
    }

    [Test]
    public async Task Query_count_is_exact_up_to_100000_documents_and_a_lower_bound_beyond()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        await api.SetPlanAsync(tenant.User, tenant.OrganizationId, "enterprise");
        var client = tenant.Data;
        using var registered = await client.PutAsync(tenant.Database("db"), null, cancellationToken);
        registered.EnsureSuccessStatusCode();
        var databaseId = (await registered.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString()!;
        var database = new TenantContext(tenant.OrganizationId, PlanTier.Enterprise, tenant.ProjectId, databaseId);
        static string Id(int index)
        {
            return index.ToString("D26", System.Globalization.CultureInfo.InvariantCulture);
        }
        await api.Server.Services.GetRequiredService<IStorageEngine>().DeleteDatabaseAsync(database, cancellationToken);
        var path = Path.Combine(api.Server.DataDirectory, tenant.OrganizationId, tenant.ProjectId, databaseId + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await DocumentFiles.WriteAsync(path, ("items", Id(0), """{"n":1}"""));
        await using (var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false })))
        {
            await connection.OpenAsync(cancellationToken);
            using var body = JsonDocument.Parse("""{"n":1}""");
            var state = DocumentState.Bottom.Put(new EntryVersion(new Hlc((ulong)StoredStates.Version), StoredStates.Replica), body.RootElement);
            connection.CreateFunction<string, long, long>("seed_digest", (id, part) =>
            {
                var digest = DocumentStateEncoding.Digest("items", id, state);
                return part == 0 ? unchecked((long)(ulong)(digest >> 64)) : unchecked((long)(ulong)digest);
            });
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 99999)
                INSERT INTO "items" (id, doc) SELECT printf('%026d', n), '{"n":1}' FROM rows;
                INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2)
                    SELECT 'items', items.id, template.state, template.stamp, template.origin,
                        1, seed_digest(items.id, 0), seed_digest(items.id, 1)
                    FROM "items" AS items CROSS JOIN (
                        SELECT state, stamp, origin FROM _tb_state WHERE collection = 'items' AND id = $first
                    ) AS template WHERE items.id != $first;
                """;
            command.Parameters.AddWithValue("$first", Id(0));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        async Task<(long Value, bool Exact)> CountAsync(object? filter)
        {
            using var response = await client.PostAsJsonAsync(
                tenant.Database("db", "/collections/items/query"), new { filter, limit = 1, count = true });
            response.EnsureSuccessStatusCode();
            var count = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count");
            return (long.Parse(count.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture), count.GetProperty("exact").GetBoolean());
        }

        var matches = new { eq = new object[] { "/n", 1 } };
        var atLimit = await CountAsync(null);
        var atLimitFiltered = await CountAsync(matches);
        (await client.PutAsJsonAsync(tenant.Database("db", $"/collections/items/documents/by-key?id={Id(100_000)}"), new { n = 2 }))
            .EnsureSuccessStatusCode();
        var (beyondValue, beyondExact) = await CountAsync(null);
        var beyondFiltered = await CountAsync(matches);
        var (noneValue, noneExact) = await CountAsync(new { eq = new object[] { "/n", 2 } });
        await Assert.That(atLimit).IsEqualTo((100_000L, true));
        await Assert.That(atLimitFiltered).IsEqualTo((100_000L, true));
        await Assert.That(beyondExact).IsFalse();
        await Assert.That(beyondValue is >= 100_000 and <= 100_001).IsTrue();
        await Assert.That(beyondFiltered).IsEqualTo((100_000L, false));
        await Assert.That(noneExact).IsFalse();
        await Assert.That(noneValue is >= 0 and <= 1).IsTrue();
    }
}
