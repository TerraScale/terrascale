using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;
using static TerraScale.DotNet.Tests.TerraBase.Shapes.ShapeVectors;

namespace TerraScale.DotNet.Tests.TerraBase.Shapes;

public sealed class TerraBaseShapeClientTests
{
    private const string SnapLast = "snap.2.MDFBUlozTkRFS1RTVjRSUkZGUTY5RzVGQjA";
    private const string Filter = "{\"eq\":[\"/read\",false]}";
    private const string FreshB2 = "01ARZ3NDEKTSV4RRFFQ69G5FB2";

    // ---- bootstrap, pages, chunks and live -------------------------------------------------------------------------

    [Test]
    public async Task BootstrapFollowsRedirectPagesSnapshotAppliesChunkAndPollsWithServerCursor()
    {
        var secondLive = new TaskCompletionSource();
        using var server = new ShapeFakeServer((request, token) => request switch
        {
            { IsPlainBootstrap: true } => Task.FromResult(Response("bootstrap")),
            { Offset: "snap.2.0" } => Task.FromResult(Response("snapshot-first-page")),
            { Offset: SnapLast } => Task.FromResult(Response("snapshot-last-page")),
            { Offset: "2", Live: false } => Task.FromResult(Response("closed-commit-upsert-delete")),
            { Offset: "4", Live: false } => Task.FromResult(FreshAt4(SourceShapeId, SourceHandle)),
            { Offset: "4", Live: true, Cursor: "51" } => ShapeFakeServer.Hold(secondLive, token),
            { Offset: "4", Live: true } => Task.FromResult(Response("live-timeout")),
            _ => Task.FromResult(Unexpected(request)),
        });
        using var http = new HttpClient(server);

        var states = await RunUntil(token => Client(http).SubscribeAsync(Project, Database, Collection, cancellationToken: token), secondLive.Task);

        var requests = server.Requests;
        await Assert.That(requests[0].Pairs.Select(pair => pair.Key)).IsEquivalentTo(new[] { "offset" });
        await AssertQuery(requests[1], ("handle", SourceHandle), ("offset", "snap.2.0"));
        await AssertQuery(requests[2], ("handle", SourceHandle), ("offset", SnapLast));
        await AssertQuery(requests[3], ("handle", SourceHandle), ("offset", "2"));
        await Assert.That(requests.All(request => request.Uri.AbsolutePath == ShapePath && request.Uri.Host == "terrabase.example.test")).IsTrue();
        await Assert.That(requests.All(request => request.Authorization == "Bearer tb_fixture")).IsTrue();
        await Assert.That(requests.Count(request => request.IsPlainBootstrap)).IsEqualTo(1);
        await Assert.That(requests.Any(request => request.IsResume)).IsFalse();

        var live = requests.Where(request => request.Live).ToList();
        await Assert.That(live.Count).IsEqualTo(2);
        await Assert.That(live.All(request => request.Offset == "4" && request.Handle == SourceHandle)).IsTrue();
        // The second poll carries the bucket TerraBase returned in TB-Shape-Cursor, never a client clock.
        await Assert.That(live[1].Cursor).IsEqualTo("51");

        // The complete snapshot holds both page documents at offset 2.
        var snapshot = states.First(state => state.Offset == "2");
        await AssertDocuments(snapshot, Apply("commit-is-atomic").GetProperty("initial_state"));
        await Assert.That(snapshot.Handle).IsEqualTo(SourceHandle);

        var last = states[^1];
        await AssertDocuments(last, Apply("commit-is-atomic").GetProperty("expected_state"));
        await Assert.That(last.Offset).IsEqualTo("4");
        await Assert.That(last.UpToDate).IsTrue();
        await Assert.That(last.CursorResume).IsEqualTo(Http("live-timeout").GetProperty("response").GetProperty("body").GetProperty("cursor_resume").GetString());

        // A commit is applied whole: no state shows the updated B0 together with the deleted B1.
        await Assert.That(states.Any(state => state.Documents.ContainsKey(B1)
            && state.Documents.TryGetValue(B0, out var document) && document.Version == "tbv1-def52df94d5598d237cf1184a4f5cb00")).IsFalse();
        // Only fresh responses can be up to date; sealed snapshot and chunk states never are.
        await Assert.That(states.Where(state => state.Offset != "4").All(state => !state.UpToDate)).IsTrue();
        // States are immutable: the snapshot captured earlier still holds B1 after it was deleted.
        await Assert.That(snapshot.Documents.ContainsKey(B1)).IsTrue();
        // A partial snapshot never exposes a resume cursor.
        await Assert.That(states.Where(state => state.Offset.StartsWith("snap.", StringComparison.Ordinal)).All(state => state.CursorResume is null)).IsTrue();
    }

    // ---- apply vectors ---------------------------------------------------------------------------------------------

    // ---- must-refetch and cross-machine resume ---------------------------------------------------------------------

    // ---- redirects -------------------------------------------------------------------------------------------------

    [Test]
    [Arguments("https://evil.example.test" + ShapePath + "?handle=h1-f3171a2230109328&offset=snap.2.0", null)]
    [Arguments("//evil.example.test" + ShapePath + "?handle=h1-f3171a2230109328&offset=snap.2.0", null)]
    [Arguments("http://terrabase.example.test" + ShapePath + "?handle=h1-f3171a2230109328&offset=snap.2.0", null)]
    [Arguments("/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAW/databases/demo/collections/other/shape?handle=h1-f3171a2230109328&offset=snap.2.0", null)]
    [Arguments("/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAW/databases/other/collections/notes/shape?handle=h1-f3171a2230109328&offset=snap.2.0", null)]
    [Arguments(ShapePath + "/keys?handle=h1-f3171a2230109328&offset=snap.2.0", null)]
    [Arguments(ShapePath + "?handle=h1-f3171a2230109328&offset=snap.2.0&evil=1", null)]
    [Arguments(ShapePath + "?handle=h1-f3171a2230109328", null)]
    [Arguments(ShapePath + "?offset=snap.2.0", null)]
    [Arguments(ShapePath + "?handle=h1-f3171a2230109328&offset=snap.2.0", Filter)]
    public async Task UnsafeRedirectIsRefusedWithoutFollowing(string location, string? where)
    {
        using var server = new ShapeFakeServer(request => request.IsPlainBootstrap ? Redirect(location, SourceHandle) : Unexpected(request));
        using var http = new HttpClient(server);

        await Assert.That(async () =>
        {
            await foreach (var _ in Client(http).SubscribeAsync(Project, Database, Collection, where)) { }
        }).Throws<Exception>();

        await Assert.That(server.Requests.Count).IsEqualTo(1);
        await Assert.That(server.Disposed).IsFalse();
    }

    // ---- documented retry ------------------------------------------------------------------------------------------

    // ---- credentials, cancellation and resources -------------------------------------------------------------------

    [Test]
    [Arguments("unknown-op")]
    [Arguments("upsert-without-version")]
    [Arguments("upsert-without-document")]
    [Arguments("delete-without-id")]
    [Arguments("other-handle")]
    [Arguments("other-offset")]
    [Arguments("non-canonical-next-offset")]
    [Arguments("truncated")]
    [Arguments("not-an-object")]
    public async Task InvalidResponseIsRejectedWholeAndAppliesNothing(string mutation)
    {
        var body = BodyText("closed-commit-upsert-delete");
        body = mutation switch
        {
            "unknown-op" => body.Replace("\"op\":\"delete\"", "\"op\":\"merge\"", StringComparison.Ordinal),
            "upsert-without-version" => body.Replace("\"version\":\"tbv1-def52df94d5598d237cf1184a4f5cb00\",", string.Empty, StringComparison.Ordinal),
            "upsert-without-document" => body.Replace(",\"document\":{\"title\":\"Updated\",\"read\":true}", string.Empty, StringComparison.Ordinal),
            "delete-without-id" => body.Replace("{\"op\":\"delete\",\"id\":\"01ARZ3NDEKTSV4RRFFQ69G5FB1\"}", "{\"op\":\"delete\"}", StringComparison.Ordinal),
            "other-handle" => body.Replace($"\"handle\":\"{SourceHandle}\"", "\"handle\":\"h1-0000000000000000\"", StringComparison.Ordinal),
            "other-offset" => ReplaceFirst(body, "\"offset\":\"2\"", "\"offset\":\"3\""),
            "non-canonical-next-offset" => body.Replace("\"next_offset\":\"4\"", "\"next_offset\":\"04\"", StringComparison.Ordinal),
            "truncated" => body[..^10],
            "not-an-object" => "[]",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        await Assert.That(body).IsNotEqualTo(BodyText("closed-commit-upsert-delete"));
        using var server = new ShapeFakeServer(request => request switch
        {
            { IsPlainBootstrap: true } => Response("bootstrap"),
            { Offset: "snap.2.0" } => Response("snapshot-first-page"),
            { Offset: SnapLast } => Response("snapshot-last-page"),
            { Offset: "2" } => Ok(body, SourceHandle, "4", false),
            _ => Unexpected(request),
        });
        using var http = new HttpClient(server);
        var states = new List<TerraBaseShapeState>();

        await Assert.That(async () => await Collect(Client(http).SubscribeAsync(Project, Database, Collection), states).WaitAsync(TimeSpan.FromSeconds(10)))
            .Throws<Exception>();

        await Assert.That(server.Requests.Count(request => request.Offset == "2")).IsEqualTo(1);
        await Assert.That(states.Any(state => state.Offset == "4")).IsFalse();
        await Assert.That(states[^1].Offset).IsEqualTo("2");
        await AssertDocuments(states[^1], Apply("commit-is-atomic").GetProperty("initial_state"));
    }

    // ---- epoch keys ------------------------------------------------------------------------------------------------

    [Test]
    public async Task EpochKeyIsFetchedWithNoStoreAndReturnsTheVectorKeyBytes()
    {
        var vector = Http("epoch-key");
        using var server = new ShapeFakeServer(_ => Response("epoch-key"));
        using var http = new HttpClient(server);

        var key = await Client(http).GetEpochKeyAsync(Project, Database, Collection, SourceHandle, 100, "01ARZ3NDEKTSV4RRFFQ69G5FC0");

        var tbs1 = Root.GetProperty("tbs1_vectors").EnumerateArray().First(item => item.GetProperty("handle").GetString() == SourceHandle);
        await Assert.That(Convert.ToHexStringLower(key)).IsEqualTo(tbs1.GetProperty("epoch_key_hex").GetString());
        var request = server.Requests.Single();
        await Assert.That(request.Uri.AbsolutePath).IsEqualTo(vector.GetProperty("request").GetProperty("path").GetString());
        await AssertQuery(request, ("handle", SourceHandle), ("epoch", "100"), ("key_id", "01ARZ3NDEKTSV4RRFFQ69G5FC0"));
        await Assert.That(request.Authorization).IsEqualTo("Bearer tb_fixture");
        await Assert.That(request.CacheControl ?? string.Empty).Contains("no-store");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static string Token => Http("snapshot-first-page").GetProperty("response").GetProperty("body").GetProperty("cursor_resume").GetString()!;

    private static TerraBaseShapeClient Client(HttpClient http, TimeProvider? clock = null)
    {
        var options = new TerraBaseShapeClientOptions { BaseAddress = new Uri(Origin), ApiKey = "tb_fixture" };
        return clock is null ? new TerraBaseShapeClient(http, options) : new TerraBaseShapeClient(http, options, clock);
    }

    private static HttpResponseMessage FreshAt4(string shapeId, string handle, string? cursor = null) =>
        Ok(Fresh(shapeId, handle, "4", cursor ?? Http("live-timeout").GetProperty("response").GetProperty("body").GetProperty("cursor_resume").GetString()!), handle, "4", true);

    private static HttpResponseMessage NewLineageSnapshot()
    {
        var state = JsonDocument.Parse($"{{\"{FreshB2}\":{{\"version\":\"tbv1-00000000000000000000000000000002\",\"document\":{{\"title\":\"Fresh\"}}}}}}").RootElement;
        return Ok(Snapshot(DestinationShapeId, DestinationHandle, "6", Token, state), DestinationHandle, "6", false);
    }

    private static HttpResponseMessage Transient(int status, string code, TimeSpan retryAfter)
    {
        var message = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"fixture\"}}}}", Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
        message.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        return message;
    }

    private static HttpResponseMessage Unexpected(ShapeRequest request) =>
        throw new InvalidOperationException($"Unexpected shape request {request.Uri}");

    private static string ReplaceFirst(string text, string from, string to)
    {
        var index = text.IndexOf(from, StringComparison.Ordinal);
        return text[..index] + to + text[(index + from.Length)..];
    }

    private static async Task Collect(IAsyncEnumerable<TerraBaseShapeState> source, List<TerraBaseShapeState> into)
    {
        await foreach (var state in source.ConfigureAwait(false))
        {
            lock (into) into.Add(state);
        }
    }

    /// <summary>Enumerates until <paramref name="reached"/> completes, then cancels and returns every state seen.</summary>
    private static async Task<List<TerraBaseShapeState>> RunUntil(Func<CancellationToken, IAsyncEnumerable<TerraBaseShapeState>> source, Task reached)
    {
        using var stop = new CancellationTokenSource();
        var states = new List<TerraBaseShapeState>();
        var run = Collect(source(stop.Token), states);
        var first = await Task.WhenAny(run, reached.WaitAsync(TimeSpan.FromSeconds(20)));
        if (first == run) await run; // surfaces the client's failure instead of a timeout
        await reached;
        await stop.CancelAsync();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }
        lock (states) return [.. states];
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 2000 && !condition(); attempt++) await Task.Delay(5);
    }

    private static async Task AssertQuery(ShapeRequest request, params (string Name, string Value)[] expected)
    {
        await Assert.That(request.Pairs.Count).IsEqualTo(expected.Length);
        foreach (var (name, value) in expected)
        {
            await Assert.That(request.Pairs.Count(pair => pair.Key == name)).IsEqualTo(1);
            await Assert.That(request.Query[name]).IsEqualTo(value);
        }
    }

    private static async Task AssertDocuments(TerraBaseShapeState state, JsonElement expected)
    {
        await Assert.That(state.Documents.Keys).IsEquivalentTo(expected.EnumerateObject().Select(property => property.Name).ToList());
        foreach (var property in expected.EnumerateObject())
        {
            var actual = state.Documents[property.Name];
            await Assert.That(actual.Version).IsEqualTo(property.Value.GetProperty("version").GetString());
            await Assert.That(JsonElement.DeepEquals(actual.Document, property.Value.GetProperty("document"))).IsTrue();
        }
    }

    /// <summary>An endless JSON prefix that counts what the client reads and stops at a hard test cap.</summary>
    private sealed class EndlessJsonStream : Stream
    {
        public const long Cap = 256L * 1024 * 1024;
        private static readonly byte[] Prefix = Encoding.UTF8.GetBytes("{\"shape_id\":\"");
        private long read;

        public long BytesRead => Interlocked.Read(ref read);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (read >= Cap) return 0;
            var span = buffer.AsSpan(offset, (int)Math.Min(count, Cap - read));
            for (var index = 0; index < span.Length; index++)
            {
                var position = read + index;
                span[index] = position < Prefix.Length ? Prefix[position] : (byte)'a';
            }
            Interlocked.Add(ref read, span.Length);
            return span.Length;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
