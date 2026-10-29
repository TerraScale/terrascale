using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Documents;
using TerraBase.Features.Shapes;
using TerraBase.Fast.Documents;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class ShapeEndToEndTests
{
    private const string Where = """{"eq":["/status","open"]}""";

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

    [Test]
    public async Task A_filtered_shape_converges_across_transitions_out_of_the_filter_and_deletes()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var moving = DocumentApi.NewId();
        var removed = DocumentApi.NewId();
        var version = (await PutAsync(api, moving, new { status = "open" })).Version;
        await PutAsync(api, removed, new { status = "open" });
        await PutAsync(api, DocumentApi.NewId(), new { status = "closed" });

        var client = new ShapeClient(api, Where);
        await client.SyncAsync();
        await AssertConvergedAsync(api, client, Where);
        var parked = client.LiveAsync(client.NextOffset);
        await ParkedAsync(api, readers: 1);
        await Assert.That((await PutAsync(api, moving, new { status = "closed" }, DocumentApi.IfMatch(version))).Status).IsEqualTo(HttpStatusCode.OK);
        var transition = await parked;
        await Assert.That(Ops(transition).Single(op => op.GetProperty("id").GetString() == moving).GetProperty("op").GetString()).IsEqualTo("delete");
        await AssertConvergedAsync(api, client, Where);

        var parkedAgain = client.LiveAsync(client.NextOffset);
        await ParkedAsync(api, readers: 1);
        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(removed));
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        var removal = await parkedAgain;
        await Assert.That(Ops(removal).Single(op => op.GetProperty("id").GetString() == removed).GetProperty("op").GetString()).IsEqualTo("delete");
        await AssertConvergedAsync(api, client, Where);
    }

    [Test]
    public async Task A_repeated_chunk_and_snapshot_page_answer_304_with_the_same_headers_and_no_body()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        var bootstrap = await client.Get($"{api.Collections}/{DocumentApi.Collection}/shape?offset=-1");
        await Assert.That(bootstrap.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await CacheControlAssert.EqualAsync(bootstrap.CacheControl);
        var page = await client.Get(bootstrap.Location!.OriginalString);
        await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
        await AssertWireBodyAsync(page);

        var cached = await client.Get(page.Uri!, ("If-None-Match", page.ETag!));
        await AssertNotModifiedAsync(cached, page);
        await client.SyncAsync();
        var closedAt = client.NextOffset;
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var chunk = await client.Get(client.Uri(closedAt, live: false));
        await Assert.That(chunk.Status).IsEqualTo(HttpStatusCode.OK);
        await AssertWireBodyAsync(chunk);
        var chunkCached = await client.Get(chunk.Uri!, ("If-None-Match", chunk.ETag!));
        await AssertNotModifiedAsync(chunkCached, chunk);
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var again = await client.Get(chunk.Uri!);
        await Assert.That(again.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(again.Body.AsSpan().SequenceEqual(chunk.Body)).IsTrue().Because("a sealed chunk never recomputes");
        await Assert.That(again.ETag).IsEqualTo(chunk.ETag);
        await AssertNotModifiedAsync(await client.Get(chunk.Uri!, ("If-None-Match", chunk.ETag!)), chunk);
    }

    private static async Task AssertNotModifiedAsync(Reply cached, Reply original)
    {
        await Assert.That(cached.Status).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(cached.Body.Length).IsEqualTo(0);
        await Assert.That(cached.ETag).IsEqualTo(original.ETag);
        await Assert.That(cached.Handle).IsEqualTo(original.Handle);
        await Assert.That(cached.NextOffset).IsEqualTo(original.NextOffset);
        await CacheControlAssert.EqualAsync(cached.CacheControl);
        await Assert.That(cached.Vary).Contains("Authorization");
    }

    private static async Task AssertWireBodyAsync(Reply reply)
    {
        string[] members = ["shape_id", "handle", "offset", "next_offset", "covered_offset", "values_at", "up_to_date", "cursor_resume", "changes"];
        await Assert.That(reply.Json.EnumerateObject().Select(member => member.Name).SequenceEqual(members)).IsTrue();
        var digest = Convert.ToHexStringLower(SHA256.HashData(reply.Body).AsSpan(0, 8));
        var body = reply.Json;
        var tag = $"\"{body.GetProperty("shape_id").GetString()}:{reply.Handle}:{body.GetProperty("offset").GetString()}:{reply.NextOffset}:{digest}\"";
        await Assert.That(reply.ETag).IsEqualTo(tag);
        var cursor = Base64UrlDecode(body.GetProperty("cursor_resume").GetString()!);
        await Assert.That(cursor[0]).IsEqualTo((byte)1);
        await Assert.That(ChangeCursor.Decode(System.Buffers.Text.Base64Url.EncodeToString(cursor.AsSpan(17)))).IsNotNull();
        await Assert.That(reply.ContentType).IsEqualTo("application/json");
        await CacheControlAssert.EqualAsync(reply.CacheControl);
        await Assert.That(reply.Vary).Contains("Authorization");
    }

    [Test]
    public async Task Fresh_bodies_carry_the_reached_r8_cursor_and_plaintext_metadata_headers()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        await PutAsync(api, DocumentApi.NewId(), new { status = "open" });
        var client = new ShapeClient(api, query: null);
        var synced = await client.SyncAsync();

        var at = await client.Get(client.Uri(synced.NextOffset!, live: false));

        await Assert.That(at.Status).IsEqualTo(HttpStatusCode.OK);
        await AssertWireBodyAsync(at);
        await Assert.That(at.UpToDate).IsEqualTo("true");
        await Assert.That(at.NextOffset).IsEqualTo(synced.NextOffset);
        var envelope = Base64UrlDecode(at.Json.GetProperty("cursor_resume").GetString()!);
        await Assert.That(envelope[0]).IsEqualTo((byte)1);
        var tenant = await api.TenantOfAsync();
        var collection = await api.SendAsync(HttpMethod.Get, api.Collections + "/" + DocumentApi.Collection);
        await Assert.That(collection.Status).IsEqualTo(HttpStatusCode.OK);
        var fields = string.Join('\0', tenant.OrganizationId, tenant.ProjectId, tenant.DatabaseId,
            DocumentApi.Collection, collection.Json.GetProperty("id").GetString(), "", "1");
        var expectedScope = SHA256.HashData(Encoding.UTF8.GetBytes("TB-SHAPE-RESUME-V1\0" + fields));
        await Assert.That(envelope.AsSpan(1, 16).SequenceEqual(expectedScope.AsSpan(0, 16))).IsTrue()
            .Because("the HTTP resume scope binds the tenant, collection generation, filter and wire version exactly");
        var reached = ChangeCursor.Decode(System.Buffers.Text.Base64Url.EncodeToString(envelope.AsSpan(17)));
        await Assert.That(reached).IsNotNull();
        var head = await FeedHeadAsync(api);
        await Assert.That(reached!.Stamps.Count).IsGreaterThan(0);
        await Assert.That(reached.Stamps).IsEquivalentTo(head.Stamps);
        await Assert.That(at.Body[0]).IsEqualTo((byte)'{');
        await Assert.That(at.ContentType).IsEqualTo("application/json");
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

    private static async Task<TerraBase.Engine.FeedCursor> FeedHeadAsync(DocumentApi api)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri($"{api.DatabasePath()}/changes?cursor=now&collections={DocumentApi.Collection}", UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        for (var line = 0; line < 64; line++)
        {
            if (await reader.ReadLineAsync(token) is { } value && value.StartsWith("id: ", StringComparison.Ordinal))
            {
                return ChangeCursor.Decode(value[4..]) ?? throw new InvalidDataException("the feed returned an invalid cursor");
            }
        }
        throw new InvalidDataException("the feed did not return its head cursor");
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

    private static byte[] Base64UrlDecode(string token) => System.Buffers.Text.Base64Url.DecodeFromChars(token);

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
