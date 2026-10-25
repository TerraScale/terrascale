using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardRootFixture>]
public sealed class DocumentChangesHttpJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task ChangesHttpBindsAuthorityFramesResumesAndRefusesUnsafeCheckpoints()
    {
        TestAuth auth = null!;
        var organizationId = "";
        var projectId = "";
        var mode = "normal";
        var invalidCheckpoint = "";
        var checkpoint = "";
        var calls = 0;
        var upstreamResume = "";
        var upstreamQuery = "";
        TaskCompletionSource? release = null;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root,
            DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), async http =>
            {
                if (http.Request.Method != "GET" || !http.Request.Path.Value!.EndsWith("/changes", StringComparison.Ordinal)) return null;
                DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organizationId, projectId, "documents:read");
                Interlocked.Increment(ref calls);
                upstreamResume = http.Request.Headers["Last-Event-ID"].ToString();
                upstreamQuery = http.Request.QueryString.ToString();
                var current = Volatile.Read(ref mode);
                if (current == "normal") return null;
                if (current == "unavailable")
                    return Results.Json(new { error = new { code = "storage_busy", message = "Secret vendor payload" } }, statusCode: 503);
                http.Response.ContentType = "text/event-stream";
                http.Response.Headers["X-TerraBase-Region"] = "gru";
                if (current == "invalid-after-headers")
                {
                    await http.Response.WriteAsync(Cursor(checkpoint), http.RequestAborted);
                    await http.Response.Body.FlushAsync(http.RequestAborted);
                    await release!.Task.WaitAsync(http.RequestAborted);
                }
                await http.Response.WriteAsync(Cursor(invalidCheckpoint), http.RequestAborted);
                return Results.Empty;
            });
        using var client = scenario.CreateClient();
        using var anonymous = scenario.CreateClient();
        using var foreign = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using (var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me")))
        {
            organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
            projectId = identity.RootElement.GetProperty("projectId").GetString()!;
        }
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        var id = await CreateDatabaseAsync(client, csrf, "changes-http");
        var route = "/v1/dashboard/databases/" + id + "/changes?collection=items";
        using (var refused = await anonymous.GetAsync(route))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await ProblemAsync(client, "/v1/dashboard/databases/" + new TerraScale.DotNet.TerraScaleIdGenerator().NewId() + "/changes?collection=items", HttpStatusCode.NotFound);
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(foreign);
        var foreignId = await CreateDatabaseAsync(foreign, await HttpJourney.CsrfAsync(foreign, "/databases"), "foreign-changes");
        await ProblemAsync(client, "/v1/dashboard/databases/" + foreignId + "/changes?collection=items", HttpStatusCode.NotFound);
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(0);
        using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "items" }))
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await WriteAsync("01K78D0C000000000000000005");
        var page = await client.GetStringAsync("/databases/" + id + "/documents?collection=items");
        await Assert.That(page).Contains("id=\"document-changes\"");
        await Assert.That(page).Contains("id=\"document-changes-status\"");
        await Assert.That(page).Contains("id=\"document-changes-items\"");
        await Assert.That(page).Contains("Changes connection not established.");
        using (var response = await client.GetAsync(route))
        {
            await StreamHeadersAsync(response);
            var events = Frames(await response.Content.ReadAsStringAsync());
            await Assert.That(string.Join(',', events.Select(value => value.State))).IsEqualTo("cursor,change,cursor,reconnect");
            await Assert.That(events.Single(value => value.State == "change").Data.GetProperty("change").GetProperty("id").GetString()).IsEqualTo("01K78D0C000000000000000005");
            checkpoint = events.Last().Id;
            await Assert.That(checkpoint.Length).IsGreaterThan(0);
        }
        await WriteAsync("01K78D0C000000000000000006");
        using (var request = new HttpRequestMessage(HttpMethod.Get, route + "&cursor=deliberately-invalid"))
        {
            request.Headers.Add("Last-Event-ID", checkpoint);
            using var response = await client.SendAsync(request);
            await StreamHeadersAsync(response);
            var events = Frames(await response.Content.ReadAsStringAsync());
            await Assert.That(events.Single(value => value.State == "change").Data.GetProperty("change").GetProperty("id").GetString()).IsEqualTo("01K78D0C000000000000000006");
            await Assert.That(events.First().Id).IsEqualTo(checkpoint);
            await Assert.That(events.Last().Id == checkpoint).IsFalse();
            await Assert.That(upstreamResume).IsEqualTo(checkpoint);
            await Assert.That(upstreamQuery).IsEqualTo("?collections=items");
        }
        await ProblemAsync(client, route.Replace("collection=items", "collection=bad%2Fname", StringComparison.Ordinal), HttpStatusCode.UnprocessableEntity);
        Volatile.Write(ref mode, "unavailable");
        await ProblemAsync(client, route, HttpStatusCode.ServiceUnavailable);
        // Exercise actual SDK wire validation and the endpoint's pre/post-header failure paths.
        foreach (var control in new[] { '\r', '\n', '\0' })
        {
            invalidCheckpoint = "unsafe" + control + "checkpoint";
            Volatile.Write(ref mode, "invalid-before-headers");
            await ProblemAsync(client, route, HttpStatusCode.ServiceUnavailable);
            var sloBefore = await Platform.ServiceSloJourneyTests.ReadAsync(client);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref mode, "invalid-after-headers");
            using var deadline = new CancellationTokenSource(client.Timeout);
            try
            {
                using var response = await client.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                await StreamHeadersAsync(response);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
                var accepted = Frames(await FrameAsync(reader, deadline.Token));
                await Assert.That(accepted.Single().State).IsEqualTo("cursor");
                await Assert.That(accepted.Single().Id).IsEqualTo(checkpoint);
                release.TrySetResult();
                var rejected = new StringBuilder();
                var aborted = false;
                try
                {
                    while (await reader.ReadLineAsync(deadline.Token) is { } line) rejected.Append(line).Append('\n');
                }
                catch (Exception error) when (error is IOException or HttpRequestException) { aborted = true; }
                await Assert.That(aborted).IsTrue();
                await Assert.That(rejected.Length).IsEqualTo(0);
            }
            finally { release.TrySetResult(); }
            await Platform.ServiceSloJourneyTests.CountsAsync(client, sloBefore.Good, sloBefore.Bad + 1);
        }

        async Task WriteAsync(string documentId)
        {
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "items", ["operation"] = "create", ["id"] = documentId, ["document"] = "{\"value\":1}" });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    private static string Cursor(string checkpoint) =>
        "event: cursor\nid: " + checkpoint + "\ndata: " + JsonSerializer.Serialize(new { cursor = checkpoint }) + "\n\n";

    private static async Task<string> CreateDatabaseAsync(HttpClient client, string csrf, string name)
    {
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task ProblemAsync(HttpClient client, string route, HttpStatusCode status)
    {
        using var response = await client.GetAsync(route);
        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        await Assert.That(body.RootElement.GetProperty("status").GetInt32()).IsEqualTo((int)status);
        await Assert.That(text.Contains("Secret vendor payload", StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains("event:", StringComparison.Ordinal)).IsFalse();
    }

    private static async Task StreamHeadersAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
        await Assert.That(response.Headers.CacheControl?.NoStore is true).IsTrue();
        await Assert.That(response.Headers.GetValues("X-Accel-Buffering").Single()).IsEqualTo("no");
    }

    private static List<(string State, string Id, JsonElement Data)> Frames(string text)
    {
        var result = new List<(string State, string Id, JsonElement Data)>();
        foreach (var frame in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = frame.Split('\n');
            var state = lines.Single(line => line.StartsWith("event: ", StringComparison.Ordinal))[7..];
            var id = lines.Single(line => line.StartsWith("id: ", StringComparison.Ordinal))[4..];
            using var json = JsonDocument.Parse(lines.Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]);
            if (lines.Length != 3 || id.Any(char.IsControl) || json.RootElement.GetProperty("state").GetString() != state ||
                json.RootElement.GetProperty("cursor").GetString() != id) throw new InvalidOperationException("Invalid dashboard SSE framing.");
            result.Add((state, id, json.RootElement.Clone()));
        }
        return result;
    }

    private static async Task<string> FrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var frame = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            frame.Append(line).Append('\n');
            if (line.Length == 0) return frame.ToString();
        }
        throw new EndOfStreamException("The dashboard ended before its first SSE event.");
    }
}
