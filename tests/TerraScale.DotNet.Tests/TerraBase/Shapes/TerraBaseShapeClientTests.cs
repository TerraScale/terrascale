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

    [Test]
    public async Task WhereTravelsOnEveryRequestIncludingRedirectAndLivePolls()
    {
        var filtered = Root.GetProperty("identity_vectors").EnumerateArray().Single(vector => vector.GetProperty("id").GetString() == "filtered");
        var handle = filtered.GetProperty("expected").GetProperty("handle").GetString()!;
        var shapeId = filtered.GetProperty("expected").GetProperty("shape_id").GetString()!;
        var cursor = Http("snapshot-first-page").GetProperty("response").GetProperty("body").GetProperty("cursor_resume").GetString()!;
        var live = new TaskCompletionSource();
        using var server = new ShapeFakeServer((request, token) => request switch
        {
            { IsPlainBootstrap: true } => Task.FromResult(Redirect($"{ShapePath}?where={Uri.EscapeDataString(Filter)}&handle={handle}&offset=snap.2.0", handle)),
            { Offset: "snap.2.0" } => Task.FromResult(Ok(Body(shapeId, handle, "snap.2.0", "2", "2", false, cursor, "[{\"offset\":\"2\",\"ops\":[]}]"), handle, "2", false)),
            { Offset: "2", Live: false } => Task.FromResult(Ok(Fresh(shapeId, handle, "2", cursor), handle, "2", true)),
            { Live: true } => ShapeFakeServer.Hold(live, token),
            _ => Task.FromResult(Unexpected(request)),
        });
        using var http = new HttpClient(server);

        var states = await RunUntil(token => Client(http).SubscribeAsync(Project, Database, Collection, Filter, cancellationToken: token), live.Task);

        var requests = server.Requests;
        await Assert.That(requests.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(requests.All(request => request.Where == Filter)).IsTrue();
        await Assert.That(requests.All(request => request.Pairs.Count(pair => pair.Key == "where") == 1)).IsTrue();
        await Assert.That(requests.Skip(1).All(request => request.Handle == handle)).IsTrue();
        await Assert.That(states[^1].Documents.Count).IsEqualTo(0);
        await Assert.That(states[^1].Handle).IsEqualTo(handle);
    }

    // ---- apply vectors ---------------------------------------------------------------------------------------------

    [Test]
    [Arguments("commit-is-atomic")]
    [Arguments("delete-for-unseen-document")]
    public async Task ApplyVectorProducesExpectedStateAndOffset(string id)
    {
        var vector = Apply(id);
        var body = vector.GetProperty("body");
        var offset = body.GetProperty("offset").GetString()!;
        var cursor = body.GetProperty("cursor_resume").GetString()!;
        var expectedOffset = vector.GetProperty("expected_offset").GetString()!;
        var live = new TaskCompletionSource();
        using var server = new ShapeFakeServer((request, token) => request switch
        {
            { IsPlainBootstrap: true } => Task.FromResult(Redirect($"{ShapePath}?handle={SourceHandle}&offset=snap.{offset}.0", SourceHandle)),
            { Offset: var o } when o == $"snap.{offset}.0" => Task.FromResult(Ok(
                Snapshot(SourceShapeId, SourceHandle, offset, cursor, vector.GetProperty("initial_state")), SourceHandle, offset, false)),
            { Offset: var o, Live: false } when o == offset => Task.FromResult(Ok(body.GetRawText(), SourceHandle, expectedOffset, false)),
            { Offset: var o, Live: false } when o == expectedOffset => Task.FromResult(Ok(Fresh(SourceShapeId, SourceHandle, expectedOffset, cursor), SourceHandle, expectedOffset, true)),
            { Live: true } => ShapeFakeServer.Hold(live, token),
            _ => Task.FromResult(Unexpected(request)),
        });
        using var http = new HttpClient(server);

        var states = await RunUntil(token => Client(http).SubscribeAsync(Project, Database, Collection, cancellationToken: token), live.Task);

        await AssertDocuments(states.First(state => state.Offset == offset), vector.GetProperty("initial_state"));
        var applied = states.First(state => state.Offset == expectedOffset);
        await AssertDocuments(applied, vector.GetProperty("expected_state"));
        await AssertDocuments(states[^1], vector.GetProperty("expected_state"));
        await Assert.That(applied.CursorResume).IsEqualTo(cursor);
    }

    // ---- must-refetch and cross-machine resume ---------------------------------------------------------------------

    [Test]
    public async Task ChangedMachineResumesOnceKeepsStateAndAppliesRepeatedEventsNewerValue()
    {
        var vector = Apply("cross-lineage-repeat-has-newer-materialized-value");
        var saved = Http("changed-machine-continuation-tries-resume-once").GetProperty("fixture").GetProperty("saved_cursor_resume").GetString()!;
        var destinationCursor = vector.GetProperty("body").GetProperty("cursor_resume").GetString()!;
        var live = new TaskCompletionSource();
        using var server = new ShapeFakeServer((request, token) => request switch
        {
            { IsPlainBootstrap: true } => Task.FromResult(Redirect($"{ShapePath}?handle={SourceHandle}&offset=snap.2.0", SourceHandle)),
            { Handle: var h, Offset: "snap.2.0" } when h == SourceHandle => Task.FromResult(Ok(
                Snapshot(SourceShapeId, SourceHandle, "2", saved, vector.GetProperty("initial_state")), SourceHandle, "2", false)),
            { Handle: var h, Offset: "2" } when h == SourceHandle => Task.FromResult(Response("changed-machine-continuation-tries-resume-once")),
            { IsResume: true } => Task.FromResult(Response("proven-cursor-resume-query")),
            { Handle: var h, Offset: "2", Live: false } when h == DestinationHandle => Task.FromResult(Ok(vector.GetProperty("body").GetRawText(), DestinationHandle, "4", false)),
            { Handle: var h, Offset: "4", Live: false } when h == DestinationHandle => Task.FromResult(FreshAt4(DestinationShapeId, DestinationHandle, destinationCursor)),
            { Live: true } => ShapeFakeServer.Hold(live, token),
            _ => Task.FromResult(Unexpected(request)),
        });
        using var http = new HttpClient(server);

        var states = await RunUntil(token => Client(http).SubscribeAsync(Project, Database, Collection, cancellationToken: token), live.Task);

        var requests = server.Requests;
        var resume = requests.Single(request => request.IsResume);
        await AssertQuery(resume, ("offset", "-1"), ("cursor", saved));
        await Assert.That(resume.Resume.Count).IsEqualTo(0);
        await Assert.That(requests.Count(request => request.IsPlainBootstrap)).IsEqualTo(1);
        var followUp = requests[requests.ToList().IndexOf(resume) + 1];
        await AssertQuery(followUp, ("handle", DestinationHandle), ("offset", "2"));

        // State survived the lineage change: no empty state between the snapshot and the destination chunk.
        await Assert.That(states.All(state => state.Documents.ContainsKey(B0))).IsTrue();
        var applied = states.First(state => state.Handle == DestinationHandle);
        await AssertDocuments(applied, vector.GetProperty("expected_state"));
        await Assert.That(applied.Offset).IsEqualTo(vector.GetProperty("expected_offset").GetString());
        await Assert.That(states[^1].Handle).IsEqualTo(DestinationHandle);
    }

    [Test]
    [Arguments("resume-unproven-prefix")]
    [Arguments("resume-behind-destination")]
    [Arguments("resume-expired")]
    [Arguments("resume-other-collection")]
    [Arguments("resume-other-filter")]
    [Arguments("resume-other-generation")]
    [Arguments("resume-target-below-retention-floor")]
    [Arguments("resume-followup-second-machine-refetch-does-not-loop")]
    public async Task RefusedResumeDiscardsStateAndBootstrapsWithoutASecondAttempt(string refusal)
    {
        var followUpRefused = refusal == "resume-followup-second-machine-refetch-does-not-loop";
        var bootstraps = 0;
        var live = new TaskCompletionSource();
        using var server = new ShapeFakeServer((request, token) =>
        {
            if (request.IsPlainBootstrap)
            {
                return Task.FromResult(++bootstraps == 1 ? Response("bootstrap")
                    : Redirect($"{ShapePath}?handle={DestinationHandle}&offset=snap.6.0", DestinationHandle));
            }
            return request switch
            {
                { IsResume: true } => Task.FromResult(followUpRefused ? Response("proven-cursor-resume-query") : Response(refusal)),
                { Handle: var h, Offset: "2" } when h == DestinationHandle => Task.FromResult(Response(refusal)),
                { Offset: "snap.2.0" } => Task.FromResult(Response("snapshot-first-page")),
                { Offset: SnapLast } => Task.FromResult(Response("snapshot-last-page")),
                { Handle: var h, Offset: "2" } when h == SourceHandle => Task.FromResult(Response("changed-machine-continuation-tries-resume-once")),
                { Offset: "snap.6.0" } => Task.FromResult(NewLineageSnapshot()),
                { Offset: "6", Live: false } => Task.FromResult(Ok(Fresh(DestinationShapeId, DestinationHandle, "6", Token), DestinationHandle, "6", true)),
                { Live: true } => ShapeFakeServer.Hold(live, token),
                _ => Task.FromResult(Unexpected(request)),
            };
        });
        using var http = new HttpClient(server);

        var states = await RunUntil(token => Client(http).SubscribeAsync(Project, Database, Collection, cancellationToken: token), live.Task);

        var requests = server.Requests;
        await Assert.That(requests.Count(request => request.IsResume)).IsEqualTo(1);
        await Assert.That(requests.Count(request => request.IsPlainBootstrap)).IsEqualTo(2);
        var restart = requests.Where(request => request.IsPlainBootstrap).Last();
        await Assert.That(restart.Pairs.Select(pair => pair.Key)).IsEquivalentTo(new[] { "offset" });
        await Assert.That(restart.Resume.Count).IsEqualTo(0);
        // Old documents are gone; only the new lineage's snapshot remains.
        await Assert.That(states[^1].Documents.Keys).IsEquivalentTo(new[] { FreshB2 });
        await Assert.That(states[^1].Handle).IsEqualTo(DestinationHandle);
        await Assert.That(states.Any(state => state.Documents.ContainsKey(FreshB2) && state.Documents.ContainsKey(B0))).IsFalse();
    }

    [Test]
    [Arguments("changed-machine-continuation-tries-resume-once", true)]
    [Arguments("snapshot-page-missing", true)]
    [Arguments("expired-offset", false)]
    [Arguments("future-offset", false)]
    [Arguments("stale-handle", false)]
    public async Task MustRefetchWithoutAUsableCursorRestartsWithoutResume(string refusal, bool duringSnapshot)
    {
        var bootstraps = 0;
        var live = new TaskCompletionSource();
        using var server = new ShapeFakeServer((request, token) =>
        {
            if (request.IsPlainBootstrap)
            {
                return Task.FromResult(++bootstraps == 1 ? Response("bootstrap")
                    : Redirect($"{ShapePath}?handle={DestinationHandle}&offset=snap.6.0", DestinationHandle));
            }
            return request switch
            {
                { Offset: "snap.2.0" } => Task.FromResult(Response("snapshot-first-page")),
                { Offset: SnapLast } => Task.FromResult(duringSnapshot ? Response(refusal) : Response("snapshot-last-page")),
                { Handle: var h, Offset: "2" } when h == SourceHandle && !duringSnapshot => Task.FromResult(Response(refusal)),
                { Offset: "snap.6.0" } => Task.FromResult(NewLineageSnapshot()),
                { Offset: "6", Live: false } => Task.FromResult(Ok(Fresh(DestinationShapeId, DestinationHandle, "6", Token), DestinationHandle, "6", true)),
                { Live: true } => ShapeFakeServer.Hold(live, token),
                _ => Task.FromResult(Unexpected(request)),
            };
        });
        using var http = new HttpClient(server);

        var states = await RunUntil(token => Client(http).SubscribeAsync(Project, Database, Collection, cancellationToken: token), live.Task);

        var requests = server.Requests;
        // A partial snapshot's cursor is unusable and a same-handle 409 offers no other lineage: never resume.
        await Assert.That(requests.Any(request => request.IsResume)).IsFalse();
        await Assert.That(requests.Any(request => request.Resume.Count != 0)).IsFalse();
        await Assert.That(requests.Count(request => request.IsPlainBootstrap)).IsEqualTo(2);
        await Assert.That(states[^1].Documents.Keys).IsEquivalentTo(new[] { FreshB2 });
    }

    [Test]
    public async Task LongResumeTokenTravelsVerbatimInTheHeader()
    {
        var token = RequestToken("large-resume-token-uses-header");
        var initial = Apply("commit-is-atomic").GetProperty("initial_state");
        var live = new TaskCompletionSource();
        using var server = new ShapeFakeServer((request, cancellation) => request switch
        {
            { IsPlainBootstrap: true } => Task.FromResult(Redirect($"{ShapePath}?handle={SourceHandle}&offset=snap.2.0", SourceHandle)),
            { Handle: var h, Offset: "snap.2.0" } when h == SourceHandle => Task.FromResult(Ok(Snapshot(SourceShapeId, SourceHandle, "2", token, initial), SourceHandle, "2", false)),
            { Handle: var h, Offset: "2" } when h == SourceHandle => Task.FromResult(Response("changed-machine-continuation-tries-resume-once")),
            { IsResume: true } => Task.FromResult(Response("large-resume-token-uses-header")),
            { Handle: var h, Offset: "4", Live: false } when h == DestinationHandle => Task.FromResult(FreshAt4(DestinationShapeId, DestinationHandle)),
            { Live: true } => ShapeFakeServer.Hold(live, cancellation),
            _ => Task.FromResult(Unexpected(request)),
        });
        using var http = new HttpClient(server);

        var states = await RunUntil(token => Client(http).SubscribeAsync(Project, Database, Collection, cancellationToken: token), live.Task);

        var resume = server.Requests.Single(request => request.IsResume);
        await Assert.That(resume.Pairs.Select(pair => pair.Key)).IsEquivalentTo(new[] { "offset" });
        await Assert.That(resume.Offset).IsEqualTo("-1");
        await Assert.That(resume.Resume.Count).IsEqualTo(1);
        await Assert.That(resume.Resume[0]).IsEqualTo(token);
        await Assert.That(System.Buffers.Text.Base64Url.DecodeFromChars(resume.Resume[0]).Length).IsEqualTo(Http("large-resume-token-uses-header").GetProperty("fixture").GetProperty("token_bytes").GetInt32());
        await Assert.That(states[^1].Handle).IsEqualTo(DestinationHandle);
        await AssertDocuments(states[^1], initial);
    }

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

    [Test]
    [Arguments(503, "shape_too_large")]
    [Arguments(503, "unavailable")]
    [Arguments(503, "auth_unavailable")]
    [Arguments(429, "shape_busy")]
    public async Task DocumentedTransientFailureWaitsRetryAfterAndKeepsStateAndOffset(int status, string code)
    {
        var clock = new ShapeManualClock();
        var failed = 0;
        var live = new TaskCompletionSource();
        using var server = new ShapeFakeServer((request, token) => request switch
        {
            { IsPlainBootstrap: true } => Task.FromResult(Response("bootstrap")),
            { Offset: "snap.2.0" } => Task.FromResult(Response("snapshot-first-page")),
            { Offset: SnapLast } => Task.FromResult(Response("snapshot-last-page")),
            { Offset: "2", Live: false } when Interlocked.Increment(ref failed) == 1 => Task.FromResult(Transient(status, code, TimeSpan.FromSeconds(7))),
            { Offset: "2", Live: false } => Task.FromResult(Response("closed-commit-upsert-delete")),
            { Offset: "4", Live: false } => Task.FromResult(FreshAt4(SourceShapeId, SourceHandle)),
            { Live: true } => ShapeFakeServer.Hold(live, token),
            _ => Task.FromResult(Unexpected(request)),
        });
        using var http = new HttpClient(server);
        using var stop = new CancellationTokenSource();
        var states = new List<TerraBaseShapeState>();
        var run = Collect(Client(http, clock: clock).SubscribeAsync(Project, Database, Collection, cancellationToken: stop.Token), states);

        await WaitUntil(() => failed == 1 && clock.Pending.Any(due => due > TimeSpan.Zero && due <= TimeSpan.FromSeconds(7)) || run.IsCompleted);
        await Assert.That(run.IsCompleted).IsFalse();
        clock.Advance(TimeSpan.FromSeconds(7) - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        // Retry-After is a floor: nothing is resent early.
        await Assert.That(server.Requests.Count(request => request.Offset == "2")).IsEqualTo(1);
        for (var step = 0; step < 600 && !live.Task.IsCompleted && !run.IsCompleted; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
        await live.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await Assert.That(async () => await run).Throws<OperationCanceledException>();

        var retries = server.Requests.Where(request => request.Offset == "2").ToList();
        await Assert.That(retries.Count).IsEqualTo(2);
        await Assert.That(retries[1].Uri).IsEqualTo(retries[0].Uri);
        await Assert.That(server.Requests.Count(request => request.IsPlainBootstrap)).IsEqualTo(1);
        await AssertDocuments(states[^1], Apply("commit-is-atomic").GetProperty("expected_state"));
    }

    [Test]
    [Arguments(401, "unauthorized", typeof(TerraBaseAuthenticationException))]
    [Arguments(401, "key_revoked", typeof(TerraBaseKeyRevokedException))]
    [Arguments(401, "key_expired", typeof(TerraBaseKeyExpiredException))]
    [Arguments(403, "forbidden", typeof(TerraBaseForbiddenException))]
    [Arguments(400, "invalid_request", typeof(TerraBaseValidationException))]
    [Arguments(404, "unknown_collection", typeof(TerraBaseNotFoundException))]
    [Arguments(409, "deterministic_conflict", typeof(TerraBaseConflictException))]
    [Arguments(500, "internal", typeof(TerraBaseException))]
    [Arguments(503, "maintenance", typeof(TerraBaseUnavailableException))]
    public async Task UndocumentedFailureSurfacesOnceWithoutRetry(int status, string code, Type expected)
    {
        using var server = new ShapeFakeServer(request => request switch
        {
            { IsPlainBootstrap: true } => Response("bootstrap"),
            { Offset: "snap.2.0" } => Response("snapshot-first-page"),
            { Offset: SnapLast } => Response("snapshot-last-page"),
            { Offset: "2" } => Transient(status, code, TimeSpan.FromSeconds(1)),
            _ => Unexpected(request),
        });
        using var http = new HttpClient(server);
        var states = new List<TerraBaseShapeState>();

        Exception? error = null;
        try
        {
            await Collect(Client(http).SubscribeAsync(Project, Database, Collection), states).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception caught)
        {
            error = caught;
        }

        await Assert.That(error).IsNotNull();
        await Assert.That(expected.IsInstanceOfType(error)).IsTrue();
        await Assert.That(server.Requests.Count(request => request.Offset == "2")).IsEqualTo(1);
        await Assert.That(states[^1].Offset).IsEqualTo("2");
    }

    [Test]
    public async Task RetryAfterBeyondTheFixedWaitLimitSurfacesWithoutRetryingEarly()
    {
        var clock = new ShapeManualClock();
        using var server = new ShapeFakeServer(_ => Transient(503, "unavailable", TimeSpan.FromSeconds(61)));
        using var http = new HttpClient(server);
        await Assert.That(async () =>
        {
            await using var reader = Client(http, clock).SubscribeAsync(Project, Database, Collection).GetAsyncEnumerator();
            await reader.MoveNextAsync();
        }).Throws<TerraBaseUnavailableException>();
        clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(server.Requests.Count).IsEqualTo(1);
        await Assert.That(clock.Pending).IsEmpty();
    }

    // ---- credentials, cancellation and resources -------------------------------------------------------------------

    [Test]
    public async Task AccessTokenProviderRunsForEveryRequestIncludingRedirectFollowUps()
    {
        var issued = 0;
        var live = new TaskCompletionSource();
        using var server = new ShapeFakeServer((request, token) => request switch
        {
            { IsPlainBootstrap: true } => Task.FromResult(Response("bootstrap")),
            { Offset: "snap.2.0" } => Task.FromResult(Response("snapshot-first-page")),
            { Offset: SnapLast } => Task.FromResult(Response("snapshot-last-page")),
            { Offset: "2", Live: false } => Task.FromResult(Response("closed-commit-upsert-delete")),
            { Offset: "4", Live: false } => Task.FromResult(FreshAt4(SourceShapeId, SourceHandle)),
            { Live: true } => ShapeFakeServer.Hold(live, token),
            _ => Task.FromResult(Unexpected(request)),
        });
        using var http = new HttpClient(server);
        var client = new TerraBaseShapeClient(http, new TerraBaseShapeClientOptions
        {
            BaseAddress = new Uri(Origin),
            AccessTokenProvider = _ => ValueTask.FromResult("token-" + Interlocked.Increment(ref issued)),
        });

        await RunUntil(token => client.SubscribeAsync(Project, Database, Collection, cancellationToken: token), live.Task);

        var requests = server.Requests;
        await Assert.That(issued).IsEqualTo(requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            await Assert.That(requests[index].Authorization).IsEqualTo("Bearer token-" + (index + 1));
        }
    }

    [Test]
    public async Task CancellationStopsAPendingLivePollAndLeavesTheCallerHttpClientUsable()
    {
        CancellationToken observed = default;
        var live = new TaskCompletionSource();
        using var server = new ShapeFakeServer((request, token) =>
        {
            if (request.Uri.AbsolutePath == "/probe") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return request switch
            {
                { IsPlainBootstrap: true } => Task.FromResult(Response("bootstrap")),
                { Offset: "snap.2.0" } => Task.FromResult(Response("snapshot-first-page")),
                { Offset: SnapLast } => Task.FromResult(Response("snapshot-last-page")),
                { Offset: "2", Live: false } => Task.FromResult(Response("closed-commit-upsert-delete")),
                { Offset: "4", Live: false } => Task.FromResult(FreshAt4(SourceShapeId, SourceHandle)),
                { Live: true } => HoldObserved(token),
                _ => Task.FromResult(Unexpected(request)),
            };
        });
        using var http = new HttpClient(server);
        using var stop = new CancellationTokenSource();
        var run = Collect(Client(http).SubscribeAsync(Project, Database, Collection, cancellationToken: stop.Token), []);

        await live.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();

        await Assert.That(async () => await run.WaitAsync(TimeSpan.FromSeconds(10))).Throws<OperationCanceledException>();
        await Assert.That(observed.IsCancellationRequested).IsTrue();
        await Assert.That(server.Disposed).IsFalse();
        using var probe = await http.GetAsync(Origin + "/probe");
        await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        Task<HttpResponseMessage> HoldObserved(CancellationToken token)
        {
            observed = token;
            return ShapeFakeServer.Hold(live, token);
        }
    }

    [Test]
    public async Task OversizedResponseIsRejectedAfterABoundedReadAndAppliesNothing()
    {
        var endless = new EndlessJsonStream();
        using var server = new ShapeFakeServer(request => request switch
        {
            { IsPlainBootstrap: true } => Response("bootstrap"),
            { Offset: "snap.2.0" } => Response("snapshot-first-page"),
            { Offset: SnapLast } => Response("snapshot-last-page"),
            { Offset: "2" } => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) },
            _ => Unexpected(request),
        });
        using var http = new HttpClient(server);
        var states = new List<TerraBaseShapeState>();

        await Assert.That(async () => await Collect(Client(http).SubscribeAsync(Project, Database, Collection), states).WaitAsync(TimeSpan.FromSeconds(60)))
            .Throws<Exception>();

        await Assert.That(endless.BytesRead).IsLessThan(EndlessJsonStream.Cap);
        await Assert.That(states[^1].Offset).IsEqualTo("2");
        await AssertDocuments(states[^1], Apply("commit-is-atomic").GetProperty("initial_state"));
    }

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

    [Test]
    [Arguments("{\"handle\":\"h1-d90dddd41bd8282d\",\"epoch\":\"100\",\"key_id\":\"01ARZ3NDEKTSV4RRFFQ69G5FC0\",\"key\":\"hunfuJAccVHoXLOVd_t4sE3RV9_6Y0IAlZ4G139hEAU\"}")]
    [Arguments("{\"handle\":\"h1-f3171a2230109328\",\"epoch\":\"99\",\"key_id\":\"01ARZ3NDEKTSV4RRFFQ69G5FC0\",\"key\":\"hunfuJAccVHoXLOVd_t4sE3RV9_6Y0IAlZ4G139hEAU\"}")]
    [Arguments("{\"handle\":\"h1-f3171a2230109328\",\"epoch\":\"100\",\"key_id\":\"01ARZ3NDEKTSV4RRFFQ69G5FC1\",\"key\":\"hunfuJAccVHoXLOVd_t4sE3RV9_6Y0IAlZ4G139hEAU\"}")]
    [Arguments("{\"handle\":\"h1-f3171a2230109328\",\"epoch\":\"100\",\"key_id\":\"01ARZ3NDEKTSV4RRFFQ69G5FC0\",\"key\":\"hunfuJAccVHoXLOVd_t4sE3RV9_6Y0IAlZ4G139hE\"}")]
    [Arguments("{\"handle\":\"h1-f3171a2230109328\",\"epoch\":\"100\",\"key_id\":\"01ARZ3NDEKTSV4RRFFQ69G5FC0\",\"key\":\"hunfuJAccVHoXLOVd_t4sE3RV9_6Y0IAlZ4G139hEAU=\"}")]
    [Arguments("{\"handle\":\"h1-f3171a2230109328\",\"epoch\":\"100\",\"key_id\":\"01ARZ3NDEKTSV4RRFFQ69G5FC0\"}")]
    public async Task EpochKeyResponseForAnotherPairOrMalformedKeyIsRejected(string body)
    {
        using var server = new ShapeFakeServer(_ => Response(Http("epoch-key").GetProperty("response"), body));
        using var http = new HttpClient(server);

        await Assert.That(async () => await Client(http).GetEpochKeyAsync(Project, Database, Collection, SourceHandle, 100, "01ARZ3NDEKTSV4RRFFQ69G5FC0"))
            .Throws<Exception>();
    }

    [Test]
    [Arguments("private, max-age=60")]
    [Arguments(null)]
    public async Task CacheableEpochKeyResponseIsRejected(string? cacheControl)
    {
        using var server = new ShapeFakeServer(_ =>
        {
            var response = Response("epoch-key");
            response.Headers.CacheControl = null;
            if (cacheControl is not null) response.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);
            return response;
        });
        using var http = new HttpClient(server);

        await Assert.That(async () => await Client(http).GetEpochKeyAsync(Project, Database, Collection, SourceHandle, 100, "01ARZ3NDEKTSV4RRFFQ69G5FC0"))
            .Throws<Exception>();
    }

    [Test]
    [Arguments("future-key-refused", typeof(TerraBaseValidationException))]
    [Arguments("key-id-required", typeof(TerraBaseValidationException))]
    [Arguments("retired-epoch-key-pair", typeof(TerraBaseConflictException))]
    public async Task EpochKeyRefusalSurfacesWithoutRetry(string id, Type expected)
    {
        using var server = new ShapeFakeServer(_ => Response(id));
        using var http = new HttpClient(server);
        var query = Http(id).GetProperty("request").GetProperty("query");

        Exception? error = null;
        try
        {
            await Client(http).GetEpochKeyAsync(Project, Database, Collection, SourceHandle,
                ulong.Parse(query.GetProperty("epoch").GetString()!, System.Globalization.CultureInfo.InvariantCulture), "01ARZ3NDEKTSV4RRFFQ69G5FC0");
        }
        catch (Exception caught)
        {
            error = caught;
        }

        await Assert.That(expected.IsInstanceOfType(error)).IsTrue();
        await Assert.That(server.Requests.Count).IsEqualTo(1);
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
