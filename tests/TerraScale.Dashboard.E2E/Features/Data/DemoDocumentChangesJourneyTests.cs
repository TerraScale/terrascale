using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace TerraScale.Dashboard.E2E.Features.Data;

// The DEMO=true host's document changes: the dashboard's SDK stream reads the demo producer, which is fed by the
// demo document store's own writes and admits only a documents:read project bearer for the path's project.
[ClassDataSource<DashboardRootFixture>]
public sealed class DemoDocumentChangesJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task DemoWritesStreamAsLiveChangesThatResumeAndRefuseUnscopedBearers()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
        await app.StartAsync();
        var origin = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
        using var anonymous = new HttpClient { BaseAddress = origin };
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
            using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["name"] = name }))
                await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var route = "/v1/dashboard/databases/" + databaseId + "/changes?collection=items";
        using (var refused = await anonymous.GetAsync(route))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        await WriteAsync("items", "create", "first");
        await WriteAsync("other", "create", "filtered");
        await WriteAsync("items", "delete", "first");
        string checkpoint;
        using (var response = await client.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            var (_, first) = await ChangeAsync(reader);
            await Assert.That(first.GetProperty("id").GetString()).IsEqualTo("first").Because("Retained history replays without a cursor; the other collection is filtered out.");
            await Assert.That(first.GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo(1).Because("History retains the written document even after deletion.");
            var (_, historicalDelete) = await ChangeAsync(reader);
            await Assert.That(historicalDelete.GetProperty("op").GetString()).IsEqualTo("delete");
            // The stream stays open: later writes arrive live, deletes carry no document.
            await WriteAsync("items", "create", "second");
            await WriteAsync("items", "delete", "second");
            var (_, second) = await ChangeAsync(reader);
            await Assert.That(second.GetProperty("op").GetString()).IsEqualTo("upsert");
            await Assert.That(second.GetProperty("id").GetString()).IsEqualTo("second");
            await Assert.That(second.GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo(1);
            (checkpoint, var deleted) = await ChangeAsync(reader);
            await Assert.That(deleted.GetProperty("op").GetString()).IsEqualTo("delete");
            await Assert.That(deleted.GetProperty("id").GetString()).IsEqualTo("second");
            await Assert.That(deleted.GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
        }
        await WriteAsync("items", "create", "third");
        using (var request = new HttpRequestMessage(HttpMethod.Get, route))
        {
            request.Headers.Add("Last-Event-ID", checkpoint);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            var (state, id, _) = await FrameAsync(reader);
            await Assert.That(state).IsEqualTo("cursor");
            await Assert.That(id).IsEqualTo(checkpoint);
            var (_, third) = await ChangeAsync(reader);
            await Assert.That(third.GetProperty("id").GetString()).IsEqualTo("third").Because("Last-Event-ID resumes after the delete without replaying it.");
        }

        // The producer itself refuses bearers without documents:read for this project.
        string organizationId, projectId;
        using (var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me")))
        {
            organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
            projectId = identity.RootElement.GetProperty("projectId").GetString()!;
        }
        var session = await environment.Auth.PasswordAsync("user@example.com", "test-password", "127.0.0.1", deadline.Token);
        var producer = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("platform");
        var feed = "https://gru.api.terrascale.tech/v1/projects/" + projectId + "/databases/demo-changes/changes?collections=items";
        foreach (var (bearer, status) in new[]
        {
            ((string?)null, HttpStatusCode.Unauthorized),
            (session.AccessToken, HttpStatusCode.Unauthorized),
            (await environment.Auth.GetAsync(session, organizationId, projectId, ["databases:read"], deadline.Token), HttpStatusCode.Forbidden)
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, feed);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var refused = await producer.SendAsync(request, deadline.Token);
            await Assert.That(refused.StatusCode).IsEqualTo(status);
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
                if (state != "change") continue;
                var change = data.GetProperty("change");
                if (change.GetProperty("collection").GetString() != "items") throw new InvalidOperationException("A filtered collection reached the stream.");
                return (id, change);
            }
        }

        async Task<(string State, string Id, JsonElement Data)> FrameAsync(StreamReader reader)
        {
            string state = "", id = "";
            JsonElement data = default;
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                if (line.Length == 0 && state.Length > 0) return (state, id, data);
                if (line.StartsWith("event: ", StringComparison.Ordinal)) state = line[7..];
                else if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) { using var json = JsonDocument.Parse(line[6..]); data = json.RootElement.Clone(); }
            }
            throw new EndOfStreamException("The changes stream ended early.");
        }
    }
}
