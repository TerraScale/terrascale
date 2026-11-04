using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class ShapeEndToEndTests
{
    [Test]
    public async Task A_client_syncs_snapshot_then_chunks_then_parks_live_and_converges_on_writes_and_deletes()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var faker = new Faker();
        for (var index = 0; index < 5; index++)
        {
            await PutAsync(api, DocumentApi.NewId(), new { title = faker.Lorem.Word(), status = "open" });
        }

        var client = new ShapeClient(api, query: null);
        await client.SyncAsync();
        await AssertConvergedAsync(api, client, query: null);

        var chunkId = DocumentApi.NewId();
        await PutAsync(api, chunkId, new { title = "chunk", status = "open" });
        var chunk = await client.ReadAsync(client.NextOffset);
        await Assert.That(chunk.UpToDate).IsEqualTo("false");
        await Assert.That(Ops(chunk).Select(op => op.GetProperty("id").GetString())).Contains(chunkId);
        await AssertConvergedAsync(api, client, query: null);
        var parked = client.LiveAsync(client.NextOffset);
        await ParkedAsync(api, readers: 1);
        var written = DocumentApi.NewId();
        await Assert.That((await PutAsync(api, written, new { title = "live", status = "open" })).Status).IsEqualTo(HttpStatusCode.Created);
        var answer = await parked;
        await Assert.That(answer.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(answer.UpToDate).IsEqualTo("true");
        await Assert.That(Ops(answer).Select(op => op.GetProperty("id").GetString())).Contains(written);
        await AssertConvergedAsync(api, client, query: null);

        var parkedAgain = client.LiveAsync(client.NextOffset);
        await ParkedAsync(api, readers: 1);
        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(written));
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        var removal = await parkedAgain;
        await Assert.That(Ops(removal).Single(op => op.GetProperty("id").GetString() == written).GetProperty("op").GetString()).IsEqualTo("delete");
        await AssertConvergedAsync(api, client, query: null);
    }

    private static async Task AssertConvergedAsync(DocumentApi api, ShapeClient client, string? query)
    {
        var truth = await Truth(api, query);
        await Assert.That(client.State.Keys.Order(StringComparer.Ordinal).SequenceEqual(truth.Keys.Order(StringComparer.Ordinal))).IsTrue()
            .Because("the client holds exactly the ids the collection matches");
        foreach (var (id, document) in truth)
        {
            await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(client.State[id].GetRawText()), JsonNode.Parse(document.GetRawText()))).IsTrue()
                .Because($"document {id} matches the collection");
        }
    }

    private static async Task<Dictionary<string, JsonElement>> Truth(DocumentApi api, string? query)
    {
        var response = await api.SendRawAsync(HttpMethod.Post, api.Query, query is null ? "{}" : $$"""{"filter":{{query}}}""", "application/json");
        return response.Json.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, item => item.GetProperty("document").Clone(), StringComparer.Ordinal);
    }

    private static async Task<PutResult> PutAsync(DocumentApi api, string id, object document, Action<HttpRequestMessage>? configure = null)
    {
        var reply = await api.PutAsync(id, document, configure);
        return new PutResult(reply.Status, reply.Version);
    }

    private static Task ParkedAsync(DocumentApi api, int readers) =>
        TerraBaseTestServer.EventuallyAsync(async ct => Sample(await Scrape(api.Server, ct), "terrabase_shape_requests_waiting") >= readers);

    private static IEnumerable<JsonElement> Ops(Reply answer) =>
        answer.Json.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray());

    private static double Total(string scrape, string name, string? label) => scrape.Split('\n')
        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#') && MetricName(line) == name && (label is null || line.Contains(label, StringComparison.Ordinal)))
        .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture));

    private static double Sample(string scrape, string name) => Total(scrape, name, null);

    private static string MetricName(string line) => line[..line.IndexOfAny(['{', ' '])];

    private static Task<string> Scrape(TerraBaseTestServer server, CancellationToken token = default) =>
        server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token);

    private sealed record PutResult(HttpStatusCode Status, string Version);

    private sealed record Reply(HttpStatusCode Status, byte[] Body, JsonElement Json, string? Handle, string? ETag, string? CacheControl,
        string? Vary, string? NextOffset, string? UpToDate, string? ContentType, Uri? Location, Uri? Uri)
    {
        public string? Code => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString()
            : null;
    }

    private sealed class ShapeClient(DocumentApi api, string? query)
    {
        private readonly HttpClient _http = api.Client;

        public Dictionary<string, JsonElement> State { get; } = [with(StringComparer.Ordinal)];

        public string NextOffset { get; private set; } = "0";

        public string? Handle { get; private set; }

        public async Task<Reply> SyncAsync()
        {
            var bootstrap = await Get($"{api.Collections}/{DocumentApi.Collection}/shape?offset=-1" + Where(query));
            if (bootstrap.Status != HttpStatusCode.TemporaryRedirect)
            {
                throw new InvalidOperationException($"bootstrap answered {bootstrap.Status}");
            }
            Handle = bootstrap.Handle;
            var page = await Get(bootstrap.Location!.OriginalString);
            Apply(page);
            while (NextOffset.StartsWith("snap.", StringComparison.Ordinal))
            {
                Apply(await Get(Uri(NextOffset, live: false)));
            }
            Reply chunk;
            do
            {
                chunk = await Get(Uri(NextOffset, live: false));
                Apply(chunk);
            }
            while (chunk.UpToDate != "true");
            return chunk;
        }

        public async Task<Reply> ReadAsync(string offset)
        {
            var reply = await Get(Uri(offset, live: false));
            Apply(reply);
            return reply;
        }
        public async Task<Reply> LiveAsync(string offset)
        {
            var reply = await Get(Uri(offset, live: true));
            Apply(reply);
            return reply;
        }

        public Uri Uri(string offset, bool live, string? handle = null) =>
            new($"{api.Collections}/{DocumentApi.Collection}/shape?handle={handle ?? Handle}&offset={offset}" + (live ? "&live=true" : "") + Where(query), UriKind.Relative);

        public async Task<Reply> Get(string target, params (string Name, string Value)[] headers) =>
            await Get(new Uri(target, UriKind.Relative), headers);

        public async Task<Reply> Get(Uri target, params (string Name, string Value)[] headers)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            var token = TestContext.Current!.Execution.CancellationToken;
            using var response = await _http.SendAsync(request, token);
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            JsonElement json = default;
            if (bytes.Length > 0 && bytes[0] == '{')
            {
                using var document = JsonDocument.Parse(bytes);
                json = document.RootElement.Clone();
            }

            return new Reply(
                response.StatusCode, bytes, json,
                Header(response, "TB-Shape-Handle"), response.Headers.ETag?.ToString(), Header(response, "Cache-Control"),
                Header(response, "Vary"), Header(response, "TB-Shape-Next-Offset"), Header(response, "TB-Shape-Up-To-Date"),
                response.Content.Headers.ContentType?.MediaType, response.Headers.Location, target);
        }
        private void Apply(Reply reply)
        {
            if (reply.Status != HttpStatusCode.OK)
            {
                return;
            }

            using var document = JsonDocument.Parse(reply.Body);
            foreach (var change in document.RootElement.GetProperty("changes").EnumerateArray())
            {
                foreach (var op in change.GetProperty("ops").EnumerateArray())
                {
                    var id = op.GetProperty("id").GetString()!;
                    if (op.GetProperty("op").GetString() == "upsert")
                    {
                        State[id] = op.GetProperty("document").Clone();
                    }
                    else
                    {
                        State.Remove(id);
                    }
                }
            }

            NextOffset = reply.NextOffset ?? NextOffset;
            Handle = reply.Handle ?? Handle;
        }

        private static string Where(string? query) => query is null ? "" : "&where=" + System.Uri.EscapeDataString(query);

        private static string? Header(HttpResponseMessage response, string name) =>
            response.Headers.NonValidated.TryGetValues(name, out var values) ? values.SingleOrDefault()
            : response.Content.Headers.NonValidated.TryGetValues(name, out var content) ? content.SingleOrDefault() : null;
    }
}
