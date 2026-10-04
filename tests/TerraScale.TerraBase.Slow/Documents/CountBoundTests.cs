using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Slow.Documents;

public sealed class CountBoundTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Public_count_excludes_the_overflow_row_whether_or_not_it_matches(bool overflowMatches)
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:read"));
        // Seed an isolated schema-1 database, then exercise migration and bounded count through the listener.
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(server.DataDirectory, "count.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT NOT NULL,
                    PRIMARY KEY (collection, id)) WITHOUT ROWID;
                WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 100001)
                INSERT INTO documents SELECT 'items', printf('00000000-0000-7000-8000-%012x', n), 1,
                    CASE WHEN n = 100001 THEN $last ELSE '{"n":1}' END FROM rows;
                PRAGMA user_version = 1;
                """;
            command.Parameters.AddWithValue("$last", overflowMatches ? "{\"n\":1}" : "{\"n\":0}");
            await command.ExecuteNonQueryAsync();
        }

        using var response = await client.PostAsJsonAsync(new Uri("/v1/databases/count/collections/items/query", UriKind.Relative),
            new { filter = new { eq = new object[] { "/n", 1 } }, limit = 1, count = true });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("count").GetRawText()).IsEqualTo("""{"value":"100000","exact":false}""");
    }

    [Test]
    public async Task Assert_count_examines_at_most_10000_rows_whatever_it_matches()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!, await server.IssueAsync("documents:read", "documents:write"));
        (await client.PutAsync(new Uri("/v1/databases/db", UriKind.Relative), null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(new Uri("/v1/databases/db/collections", UriKind.Relative), new { name = "items" })).EnsureSuccessStatusCode();
        var transactions = new Uri("/v1/databases/db/transactions", UriKind.Relative);
        string? someId = null;
        for (var batch = 0; batch < 10; batch++)
        {
            var ops = Enumerable.Range(0, 1000).Select(_ => new { op = "create", collection = "items", document = new { n = 1 } });
            using var created = await client.PostAsJsonAsync(transactions, new { ops });
            created.EnsureSuccessStatusCode();
            someId ??= (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0].GetProperty("id").GetString();
        }

        // A filter that matches nothing still examines every row; exactly 10000 rows is within the bound.
        object sparse = new { ops = new[] { new { op = "assert_count", collection = "items", filter = new { exists = "/missing" }, lte = 0 } } };
        using var atBound = await client.PostAsJsonAsync(transactions, sparse);
        // One delete and one create: 10000 live documents, but 10001 rows, because a tombstone is a row too.
        var churn = new object[]
        {
            new { op = "delete", collection = "items", id = someId },
            new { op = "create", collection = "items", document = new { n = 1 } },
        };
        (await client.PostAsJsonAsync(transactions, new { ops = churn })).EnsureSuccessStatusCode();
        using var pastBound = await client.PostAsJsonAsync(transactions, sparse);
        using var counted = await client.PostAsJsonAsync(
            new Uri("/v1/databases/db/collections/items/query", UriKind.Relative), new { limit = 1, count = true });
        var error = (await pastBound.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        var count = (await counted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count");

        // Selective filters still consume VM work despite limit=1. This bounded filter must hit the
        // instruction budget, and the next query must work on the reused session after its handler is cleared.
        var expensive = new
        {
            or = Enumerable.Range(0, 64).Select(_ => new
            {
                and = new object[] { new { exists = "/n" }, new { eq = new object[] { "/n", 999 } } },
            }),
        };
        var query = new Uri("/v1/databases/db/collections/items/query", UriKind.Relative);
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
        // Exactly 10000 live rows still sort; tombstones do not consume that candidate bound.
        await Assert.That(sorted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((tooManyToSort.StatusCode, sortError.GetProperty("code").GetString()))
            .IsEqualTo((HttpStatusCode.BadRequest, "sort_requires_index"));
    }
}
