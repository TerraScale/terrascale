using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    // A customer's collection lifecycle through the dashboard: no phantom default collection, TerraBase's reason on a
    // refusal, and a typed-confirmation drop through the official SDK.
    [ClassDataSource<DashboardRootFixture>]
    public sealed class CollectionLifecycleJourneyTests(DashboardRootFixture fixture)
    {
        private const string Refusal = "Aggregate functions are not supported.";

        [Test]
        public async Task CollectionsAreCreatedRefusedWithAReasonAndDroppedAfterConfirmation()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
                Task.FromResult<IResult?>(http.Request.Method == "POST" && (http.Request.Path.Value!.EndsWith("/sql", StringComparison.Ordinal) || http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal))
                    ? Results.Json(new { error = new { code = "invalid_request", message = Refusal } }, statusCode: 400) : null));
            using var client = scenario.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "lifecycle", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = database.RootElement.GetProperty("id").GetString()!;

            foreach (var section in new[] { "documents", "sql" })
            {
                var empty = await client.GetStringAsync($"/databases/{id}/{section}");
                await Assert.That(empty.Contains("users", StringComparison.Ordinal)).IsFalse().Because("A database without collections selects none.");
            }

            Dictionary<string, string> Fields(params (string Key, string Value)[] fields) =>
                fields.Append(("_csrf", csrf)).Append(("databaseId", id)).ToDictionary(field => field.Item1, field => field.Item2);
            using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", Fields(("name", "items"))))
            {
                await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            var opened = await client.GetStringAsync($"/databases/{id}/sql");
            await Assert.That(opened).DoesNotContain("SELECT * FROM items");
            await Assert.That(opened).DoesNotContain("SQL must not be empty.").Because("A freshly opened editor is not an error.");
            using (var emptyCheck = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql/check", Fields(("sql", " "))))
            {
                await Assert.That(await emptyCheck.Content.ReadAsStringAsync()).Contains("SQL must not be empty.");
            }

            using (var emptyPage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/data/sql/check") { Content = new FormUrlEncodedContent(Fields(("sql", " "))) })
            {
                emptyPage.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
                emptyPage.Headers.Accept.ParseAdd("text/html");
                using var checkedPage = await client.SendAsync(emptyPage);
                await Assert.That(checkedPage.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                await Assert.That(await checkedPage.Content.ReadAsStringAsync()).Contains("SQL must not be empty.").Because("A submitted check of an empty editor says why.");
            }
            using (var duplicate = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", Fields(("name", "items"))))
            {
                await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                await Assert.That(await duplicate.Content.ReadAsStringAsync()).Contains("Protocol producer rejected the request");
            }
            using (var sql = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql", Fields(("collection", "items"), ("sql", "SELECT count(*) FROM items"))))
            {
                await Assert.That(sql.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                await Assert.That(await sql.Content.ReadAsStringAsync()).Contains(Refusal);
            }

            await Assert.That(await client.GetStringAsync($"/databases/{id}/documents/items")).Contains("id=\"delete-collection\"");
            using (var unconfirmed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections/delete", Fields(("collection", "items"), ("confirmName", "item"))))
            {
                await Assert.That(unconfirmed.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                await Assert.That(await unconfirmed.Content.ReadAsStringAsync()).Contains("Type the collection name to confirm deletion.");
            }
            using (var dropped = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections/delete", Fields(("collection", "items"), ("confirmName", "items"))))
            {
                await Assert.That(dropped.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            await Assert.That((await client.GetStringAsync($"/databases/{id}/documents")).Contains("data-collection=\"items\"", StringComparison.Ordinal)).IsFalse();
            using (var again = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections/delete", Fields(("collection", "items"), ("confirmName", "items"))))
            {
                await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
        }
    }
}
