using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DocumentRestPaginationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task FilteredEmptyNativePagesKeepTheirOpaqueContinuationAndEffectiveLimit()
        {
            const string cursor = "opaque+/= continuation";
            var reads = new List<(string Limit, string Cursor)>();
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            {
                if (http.Request.Method != "GET" || !http.Request.Path.Value!.EndsWith("/collections/items/documents", StringComparison.Ordinal))
                {
                    return Task.FromResult<IResult?>(null);
                }

                var after = http.Request.Query["cursor"].ToString();
                reads.Add((http.Request.Query["limit"].ToString(), after));
                if (after.Length > 0 && after != cursor)
                {
                    return Task.FromResult<IResult?>(Results.Json(new { error = new { code = "invalid_cursor", message = "Invalid cursor." } }, statusCode: 400));
                }

                var ids = after.Length == 0 ? new[] { "01K78000000000000000000001" }
                    : new[] { "01K78000000000000000000002", "01K78000000000000000000003" };
                return Task.FromResult<IResult?>(Results.Json(new
                {
                    items = ids.Select(id => new { id, version = "v1", state = "value", document = new { text = after.Length == 0 ? "other" : "needle" } }),
                    next_cursor = after.Length == 0 ? cursor : null
                }));
            });
            using var client = scenario.CreateClient();
            var database = await CreateDatabaseAsync(client);
            var path = $"/api/v1/dashboard/data/documents?databaseId={database}&collection=items&search=needle";
            using var first = await client.GetAsync(path);
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var firstPage = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            await Assert.That(firstPage.RootElement.GetProperty("documents").GetArrayLength()).IsEqualTo(0);
            await Assert.That(firstPage.RootElement.GetProperty("nextCursor").GetString()).IsEqualTo(cursor);
            await Assert.That(firstPage.RootElement.GetProperty("hasMore").GetBoolean()).IsTrue();
            await Assert.That(firstPage.RootElement.GetProperty("limit").GetInt32()).IsEqualTo(100);
            using var second = await client.GetAsync(path + "&limit=2&after=" + Uri.EscapeDataString(cursor));
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var secondPage = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
            await Assert.That(secondPage.RootElement.GetProperty("documents").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .SequenceEqual(new[] { "01K78000000000000000000002", "01K78000000000000000000003" })).IsTrue();
            await Assert.That(secondPage.RootElement.GetProperty("nextCursor").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(secondPage.RootElement.GetProperty("hasMore").GetBoolean()).IsFalse();
            await Assert.That(secondPage.RootElement.GetProperty("limit").GetInt32()).IsEqualTo(2);
            await Assert.That(reads.SequenceEqual(new[] { ("100", ""), ("2", cursor) })).IsTrue();
            using var alias = await client.GetAsync(path + "&cursor=" + Uri.EscapeDataString(cursor));
            await Assert.That(alias.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(reads.Last().Cursor).IsEqualTo(cursor);
            using var invalid = await client.GetAsync(path + "&after=malformed");
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task InvalidLimitsAndAmbiguousCursorsAreRejectedBeforeDocumentReads()
        {
            var documentReads = 0;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            {
                if (http.Request.Method == "GET" && http.Request.Path.Value!.EndsWith("/collections/items/documents", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref documentReads);
                }

                return Task.FromResult<IResult?>(null);
            });
            using var client = scenario.CreateClient();
            var database = await CreateDatabaseAsync(client);
            var path = $"/api/v1/dashboard/data/documents?databaseId={database}&collection=items&";
            foreach (var query in new[] { "limit=", "limit=0", "limit=-1", "limit=101", "limit=1.5", "limit=abc", "limit=2147483648",
                "limit=%2B1", "limit=%201", "limit=1&limit=2", "after=", "after=%20", "after=%0A", "after=a&after=b", "after=a&cursor=b" })
            {
                using var response = await client.GetAsync(path + query);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }
            await Assert.That(documentReads).IsEqualTo(0);
        }

        private static async Task<string> CreateDatabaseAsync(HttpClient client)
        {
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "pagination", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            return database.RootElement.GetProperty("id").GetString()!;
        }
    }
}
