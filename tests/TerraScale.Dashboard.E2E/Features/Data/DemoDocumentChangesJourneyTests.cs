using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    // The DEMO=true host's document changes: the dashboard's SDK stream reads the demo producer, which is fed by the
    // demo document store's own writes and admits only a documents:read project bearer for the path's project.
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DemoDocumentChangesJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task DemoWritesStreamAsLiveChangesThatResumeAndRefuseAnonymousRequests()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            await using var app = environment.CreateTestHost(fixture.Root, 0, "https://127.0.0.1", PublicDemo.Configure);
            await app.StartAsync();
            var origin = new Uri(app.Urls.Single());
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
            using var anonymous = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate }) { BaseAddress = origin };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await HttpJourney.LoginAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            string databaseId;
            using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "demo-changes", ["region"] = "GRU" }))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
                using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                databaseId = body.RootElement.GetProperty("id").GetString()!;
            }
            foreach (var name in new[] { "items", "other" })
            {
                using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["name"] = name }))
                {
                    await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
                }
            }

            var route = "/v1/dashboard/databases/" + databaseId + "/changes?collection=items";
            using (var refused = await anonymous.GetAsync(route))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }

            await WriteAsync("items", "create", "01K78D0C000000000000000001");
            await WriteAsync("other", "create", "01K78D0C000000000000000002");
            await WriteAsync("items", "delete", "01K78D0C000000000000000001");
            string checkpoint;
            using (var response = await client.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
                var (_, first) = await ChangeAsync(reader);
                await Assert.That(first.GetProperty("id").GetString()).IsEqualTo("01K78D0C000000000000000001").Because("Retained history replays without a cursor; the other collection is filtered out.");
                await Assert.That(first.GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo(1).Because("History retains the written document even after deletion.");
                var (_, historicalDelete) = await ChangeAsync(reader);
                await Assert.That(historicalDelete.GetProperty("op").GetString()).IsEqualTo("delete");
                // The stream stays open: later writes arrive live, deletes carry no document.
                await WriteAsync("items", "create", "01K78D0C000000000000000003");
                await WriteAsync("items", "delete", "01K78D0C000000000000000003");
                var (_, second) = await ChangeAsync(reader);
                await Assert.That(second.GetProperty("op").GetString()).IsEqualTo("upsert");
                await Assert.That(second.GetProperty("id").GetString()).IsEqualTo("01K78D0C000000000000000003");
                await Assert.That(second.GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo(1);
                (checkpoint, var deleted) = await ChangeAsync(reader);
                await Assert.That(deleted.GetProperty("op").GetString()).IsEqualTo("delete");
                await Assert.That(deleted.GetProperty("id").GetString()).IsEqualTo("01K78D0C000000000000000003");
                await Assert.That(deleted.GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
            }
            await WriteAsync("items", "create", "01K78D0C000000000000000004");
            using (var request = new HttpRequestMessage(HttpMethod.Get, route))
            {
                request.Headers.Add("Last-Event-ID", checkpoint);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
                var (state, id, _) = await FrameAsync(reader);
                await Assert.That(state).IsEqualTo("cursor");
                await Assert.That(id).IsEqualTo(checkpoint);
                var (_, third) = await ChangeAsync(reader);
                await Assert.That(third.GetProperty("id").GetString()).IsEqualTo("01K78D0C000000000000000004").Because("Last-Event-ID resumes after the delete without replaying it.");
            }

            async Task WriteAsync(string collection, string operation, string id)
            {
                using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
                { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["collection"] = collection, ["operation"] = operation, ["id"] = id, ["document"] = "{\"value\":1}" });
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            async Task<(string Id, JsonElement Change)> ChangeAsync(StreamReader reader)
            {
                while (true)
                {
                    var (state, id, data) = await FrameAsync(reader);
                    if (state != "change")
                    {
                        continue;
                    }

                    var change = data.GetProperty("change");
                    if (change.GetProperty("collection").GetString() != "items")
                    {
                        throw new InvalidOperationException("A filtered collection reached the stream.");
                    }

                    return (id, change);
                }
            }

            async Task<(string State, string Id, JsonElement Data)> FrameAsync(StreamReader reader)
            {
                string state = "", id = "";
                JsonElement data = default;
                while (await reader.ReadLineAsync(deadline.Token) is { } line)
                {
                    if (line.Length == 0 && state.Length > 0)
                    {
                        return (state, id, data);
                    }

                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                    {
                        state = line[7..];
                    }
                    else if (line.StartsWith("id: ", StringComparison.Ordinal))
                    {
                        id = line[4..];
                    }
                    else if (line.StartsWith("data: ", StringComparison.Ordinal)) { using var json = JsonDocument.Parse(line[6..]); data = json.RootElement.Clone(); }
                }
                throw new EndOfStreamException("The changes stream ended early.");
            }
        }
    }
}
