using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Turso.Data.Sqlite;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Documents;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

/// <summary>
/// <c>GET …/databases/{database}/changes</c> (docs/terrabase/multi-region-replication.md section 14) on one node: events,
/// cursors and their resumption, the collection filter, joined remote writes, retention, limits and errors.
/// </summary>
public sealed class DatabaseChangesTests
{
    private static readonly Faker Fake = new();

    [Test]
    public async Task A_write_after_cursor_now_streams_with_its_document_and_its_id_resumes_after_it()
    {
        await using var api = await DocumentApi.StartAsync();
        var before = DocumentApi.NewId();
        (await api.PutAsync(before, new { name = Fake.Name.FirstName() })).Status.ShouldBe(HttpStatusCode.Created);
        var name = Fake.Name.FirstName();
        var id = DocumentApi.NewId();
        string resume;
        using (var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor=now"))
        {
            var head = await stream.NextAsync();
            await Assert.That(head.Event).IsEqualTo("cursor");
            await Assert.That(head.Data.GetProperty("cursor").GetString()).IsEqualTo(head.Id);
            (await api.PutAsync(id, new { name })).Status.ShouldBe(HttpStatusCode.Created);
            var change = await stream.NextAsync();
            await Assert.That(change.Event).IsEqualTo("change");
            await Assert.That(change.Data.GetProperty("collection").GetString()).IsEqualTo(DocumentApi.Collection);
            await Assert.That(change.Data.GetProperty("id").GetString()).IsEqualTo(id);
            await Assert.That(change.Data.GetProperty("op").GetString()).IsEqualTo("upsert");
            await Assert.That(change.Data.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
            await Assert.That(change.Data.GetProperty("origin").GetString()).Matches("^[0-9a-f]{16}$");
            await Assert.That(ulong.Parse(change.Data.GetProperty("version").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).IsGreaterThan(0UL);
            await Assert.That(change.Id).IsNotEqualTo(head.Id);
            resume = change.Id;
        }

        (await api.SendAsync(HttpMethod.Delete, api.Document(id))).Status.ShouldBe(HttpStatusCode.OK);
        // Last-Event-ID wins over the cursor parameter, as an EventSource reconnects with its original URL.
        using var resumed = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor=now", resume);
        await Assert.That((await resumed.NextAsync()).Event).IsEqualTo("cursor");
        var deleted = await resumed.NextAsync();
        await Assert.That(deleted.Data.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(deleted.Data.GetProperty("op").GetString()).IsEqualTo("delete");
        await Assert.That(deleted.Data.GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Without_a_cursor_the_feed_replays_retention_in_order_and_the_filter_keeps_only_its_collections()
    {
        await using var api = await DocumentApi.StartAsync();
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "other" })).Status.ShouldBe(HttpStatusCode.Created);
        var ids = Enumerable.Range(0, 5).Select(_ => DocumentApi.NewId()).ToArray();
        foreach (var id in ids)
        {
            (await api.PutAsync(id, new { name = Fake.Name.FirstName() })).Status.ShouldBe(HttpStatusCode.Created);
            (await api.SendAsync(HttpMethod.Put, $"{api.Collections}/other/documents/by-key?id={id}", new { other = true })).Status.ShouldBe(HttpStatusCode.Created);
        }

        using var all = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes");
        _ = await all.NextAsync();
        var every = await all.ChangesAsync(10);
        await Assert.That(string.Join(' ', every.Select(change => $"{change.Data.GetProperty("collection").GetString()}/{change.Data.GetProperty("id").GetString()}")))
            .IsEqualTo(string.Join(' ', ids.SelectMany(id => new[] { $"{DocumentApi.Collection}/{id}", $"other/{id}" })));

        using var filtered = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?collections={DocumentApi.Collection}");
        _ = await filtered.NextAsync();
        var items = await filtered.ChangesAsync(5);
        await Assert.That(string.Join(' ', items.Select(change => change.Data.GetProperty("id").GetString()))).IsEqualTo(string.Join(' ', ids));
        // The filtered stream's cursor passes the other collection's events too: resuming from it delivers nothing old.
        var last = DocumentApi.NewId();
        (await api.PutAsync(last, new { name = "last" })).Status.ShouldBe(HttpStatusCode.Created);
        var next = await filtered.NextAsync();
        await Assert.That(next.Data.GetProperty("id").GetString()).IsEqualTo(last);
        using var resumed = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?collections={DocumentApi.Collection}&cursor={Uri.EscapeDataString(items[^1].Id)}");
        _ = await resumed.NextAsync();
        await Assert.That((await resumed.NextAsync()).Data.GetProperty("id").GetString()).IsEqualTo(last);
    }

    [Test]
    public async Task A_joined_remote_write_streams_once_under_its_origin_and_a_duplicate_join_adds_nothing()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var remote = ReplicaId.Of(4518, 7, 1);
        var at = new EntryVersion(new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16), remote);
        var id = DocumentApi.NewId();
        using var body = JsonDocument.Parse("""{"from":"gru"}""");
        var delta = DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp);
        for (var copy = 0; copy < 2; copy++)
        {
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            _ = await transaction.JoinAsync(DocumentApi.Collection, id, delta, at, CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        var local = DocumentApi.NewId();
        (await api.PutAsync(local, new { from = "here" })).Status.ShouldBe(HttpStatusCode.Created);
        using var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes");
        _ = await stream.NextAsync();
        var changes = await stream.ChangesAsync(2);
        await Assert.That(changes[0].Data.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(changes[0].Data.GetProperty("origin").GetString()).IsEqualTo(remote.Value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(changes[0].Data.GetProperty("version").GetString()).IsEqualTo(at.Stamp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(changes[0].Data.GetProperty("document").GetProperty("from").GetString()).IsEqualTo("gru");
        await Assert.That(changes[1].Data.GetProperty("id").GetString()).IsEqualTo(local);
        await Assert.That(changes[1].Data.GetProperty("origin").GetString()).IsNotEqualTo(changes[0].Data.GetProperty("origin").GetString());
    }

    [Test]
    public async Task A_cursor_below_retention_is_410_cursor_expired_and_one_at_the_floor_resumes()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var first = DocumentApi.NewId();
        var second = DocumentApi.NewId();
        (await api.PutAsync(first, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(second, new { n = 2 })).Status.ShouldBe(HttpStatusCode.Created);
        using var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes");
        _ = await stream.NextAsync();
        var changes = await stream.ChangesAsync(2);
        var afterFirst = changes[0].Id;
        var afterSecond = changes[1].Id;
        // Retention deletes both events: the floor is the second one's stamp.
        var trimmed = new Hlc(ulong.Parse(changes[1].Data.GetProperty("version").GetString()!, System.Globalization.CultureInfo.InvariantCulture) + 1);
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.That(await transaction.TrimFeedAsync(trimmed, 1024, CancellationToken.None)).IsEqualTo(2);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(afterFirst)}", UriKind.Relative)),
            HttpStatusCode.Gone, "cursor_expired", "events after the cursor are no longer retained here; start again with cursor=now and re-read");
        var third = DocumentApi.NewId();
        (await api.PutAsync(third, new { n = 3 })).Status.ShouldBe(HttpStatusCode.Created);
        using var resumed = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(afterSecond)}");
        _ = await resumed.NextAsync();
        await Assert.That((await resumed.NextAsync()).Data.GetProperty("id").GetString()).IsEqualTo(third);
    }

    [Test]
    public async Task A_cursor_that_never_saw_an_origin_is_410_once_retention_deleted_that_origins_events()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        string resume;
        using (var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes"))
        {
            _ = await stream.NextAsync();
            resume = (await stream.ChangesAsync(1))[0].Id;
        }

        // A replaced machine (a new origin) writes after the cursor was issued, and retention deletes its event.
        var joined = await JoinAsync(store, ReplicaId.Of(4518, 9, 1), DateTimeOffset.UtcNow);
        await Assert.That(await TrimAsync(store, new Hlc(joined.Value + 1))).IsEqualTo(2);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(resume)}", UriKind.Relative)),
            HttpStatusCode.Gone, "cursor_expired", "events after the cursor are no longer retained here; start again with cursor=now and re-read");
    }

    [Test]
    public async Task An_issued_cursor_with_no_origin_is_410_after_retention_deleted_a_later_event_and_no_cursor_starts_at_retention()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        string empty;
        using (var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor=now"))
        {
            empty = (await stream.NextAsync()).Id;
        }

        var joined = await JoinAsync(store, ReplicaId.Of(4518, 10, 1), DateTimeOffset.UtcNow);
        var kept = DocumentApi.NewId();
        (await api.PutAsync(kept, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That(await TrimAsync(store, new Hlc(joined.Value + 1))).IsEqualTo(1);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(empty)}", UriKind.Relative)),
            HttpStatusCode.Gone, "cursor_expired", "events after the cursor are no longer retained here; start again with cursor=now and re-read");
        // Without a cursor the client asks for what retention still holds, so the same deletion is no gap.
        using var fresh = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes");
        _ = await fresh.NextAsync();
        await Assert.That((await fresh.NextAsync()).Data.GetProperty("id").GetString()).IsEqualTo(kept);
    }

    [Test]
    public async Task An_origin_whose_events_all_aged_out_is_forgotten_and_leaves_resumed_cursors_and_an_old_cursor_is_410()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var now = DateTimeOffset.UtcNow;
        var gone = ReplicaId.Of(4518, 11, 1);
        var old = await JoinAsync(store, gone, now - FeedCursor.Forget - TimeSpan.FromDays(1));
        var kept = DocumentApi.NewId();
        (await api.PutAsync(kept, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        // One retention pass deletes the old event, and its origin's floor is older than the forget horizon.
        await Assert.That(await TrimAsync(store, FeedCursor.At(now - FeedCursor.Retention))).IsEqualTo(1);
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync(), readOnly: true)))
        {
            await connection.OpenAsync();
            await using var floors = connection.CreateCommand();
            floors.CommandText = "SELECT count(*) FROM _tb_feed_floor WHERE origin = $origin;";
            floors.Parameters.AddWithValue("$origin", unchecked((long)gone.Value));
            await Assert.That((long)(await floors.ExecuteScalarAsync())!).IsEqualTo(0);
        }

        // A client that still names the forgotten origin gets it dropped from the next cursor.
        var named = ChangeCursor.Encode(new FeedCursor(new Dictionary<ReplicaId, Hlc> { [gone] = old }, FeedCursor.At(now)));
        using (var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(named)}"))
        {
            _ = await stream.NextAsync();
            var change = await stream.NextAsync();
            await Assert.That(change.Data.GetProperty("id").GetString()).IsEqualTo(kept);
            await Assert.That(ChangeCursor.Decode(change.Id)!.Stamps.Keys).DoesNotContain(gone);
        }

        // A cursor issued longer ago than the retention could lack such an origin: expired.
        var stale = ChangeCursor.Encode(new FeedCursor(new Dictionary<ReplicaId, Hlc>(), FeedCursor.At(now - FeedCursor.Retention - TimeSpan.FromMinutes(1))));
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(stale)}", UriKind.Relative)),
            HttpStatusCode.Gone, "cursor_expired", "events after the cursor are no longer retained here; start again with cursor=now and re-read");
    }

    [Test]
    public async Task An_anti_entropy_repair_streams_under_this_machine_and_its_cursor_resumes()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        // A repair joins with origin 0 (R6), which no cursor can name.
        await JoinAsync(store, default, DateTimeOffset.UtcNow);
        using var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes");
        _ = await stream.NextAsync();
        var repaired = await stream.NextAsync();
        await Assert.That(repaired.Data.GetProperty("origin").GetString()).IsNotEqualTo(new string('0', 16));
        var after = DocumentApi.NewId();
        (await api.PutAsync(after, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        using var resumed = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(repaired.Id)}");
        _ = await resumed.NextAsync();
        await Assert.That((await resumed.NextAsync()).Data.GetProperty("id").GetString()).IsEqualTo(after);
    }

    [Test]
    public async Task A_cursor_issued_in_the_future_counts_as_issued_now()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var future = ChangeCursor.Encode(new FeedCursor(new Dictionary<ReplicaId, Hlc>(), FeedCursor.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(30))));
        var joined = await JoinAsync(store, ReplicaId.Of(4518, 12, 1), DateTimeOffset.UtcNow);
        await Assert.That(await TrimAsync(store, new Hlc(joined.Value + 1))).IsEqualTo(1);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(future)}", UriKind.Relative)),
            HttpStatusCode.Gone, "cursor_expired", "events after the cursor are no longer retained here; start again with cursor=now and re-read");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task A_cursor_of_the_first_format_without_an_issue_instant_is_400(int origins)
    {
        await using var api = await DocumentApi.StartAsync();
        // #118's layout: the format byte, then (origin, stamp) pairs, with no issue instant.
        byte[] old = [1, .. Enumerable.Range(1, origins).SelectMany(origin => Origin((ulong)origin, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16))];
        var text = Convert.ToBase64String(old).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var changes = $"{api.DatabasePath()}/changes";
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{changes}?cursor={text}", UriKind.Relative)),
            HttpStatusCode.BadRequest, "invalid_cursor", "the cursor is not one this API issued");
        using var resume = new HttpRequestMessage(HttpMethod.Get, new Uri(changes, UriKind.Relative));
        resume.Headers.Add("Last-Event-ID", text);
        await ErrorAssert.ErrorAsync(api.Client.SendAsync(resume), HttpStatusCode.BadRequest, "invalid_cursor", "the cursor is not one this API issued");
    }

    [Test]
    public async Task Malformed_cursors_and_filters_are_400_a_missing_scope_403_and_an_unknown_database_404()
    {
        await using var api = await DocumentApi.StartAsync();
        var changes = $"{api.DatabasePath()}/changes";
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{changes}?cursor=AQ-not-a-cursor", UriKind.Relative)),
            HttpStatusCode.BadRequest, "invalid_cursor", "the cursor is not one this API issued");
        // A canonical cursor names each origin once, in ascending order.
        var unordered = Convert.ToBase64String([1, .. Origin(0, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16)[8..], .. Origin(2, 5), .. Origin(1, 5)])
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{changes}?cursor={unordered}", UriKind.Relative)),
            HttpStatusCode.BadRequest, "invalid_cursor", "the cursor is not one this API issued");
        using var lastEventId = new HttpRequestMessage(HttpMethod.Get, new Uri(changes, UriKind.Relative));
        lastEventId.Headers.Add("Last-Event-ID", "garbage");
        await ErrorAssert.ErrorAsync(api.Client.SendAsync(lastEventId), HttpStatusCode.BadRequest, "invalid_cursor", "the cursor is not one this API issued");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{changes}?collections=Bad", UriKind.Relative)),
            HttpStatusCode.BadRequest, "invalid_request", "collection must be a lowercase name of at most 63 characters");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{changes}?since=1", UriKind.Relative)),
            HttpStatusCode.BadRequest, "invalid_request", "query parameters are limited to: cursor, collections, each once and non-empty");
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{api.DatabasePath("nope")}/changes", UriKind.Relative)),
            HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        using var writer = await api.ClientWithScopesAsync("documents:write");
        using var forbidden = await writer.GetAsync(new Uri(changes, UriKind.Relative));
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task A_database_holds_at_most_32_streams_per_machine_and_a_closed_one_frees_its_place()
    {
        await using var api = await DocumentApi.StartAsync();
        var changes = $"{api.DatabasePath()}/changes?cursor=now";
        var streams = new List<Sse>();
        try
        {
            for (var i = 0; i < DatabaseChanges.MaxPerDatabase; i++)
            {
                var stream = await Sse.OpenAsync(api.Client, changes);
                streams.Add(stream);
                _ = await stream.NextAsync();
            }

            await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri(changes, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead),
                HttpStatusCode.TooManyRequests, "changes_busy", "the database has as many change streams as it allows; retry later");
            streams[0].Dispose();
            streams.RemoveAt(0);
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
            {
                using var response = await api.Client.GetAsync(new Uri(changes, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                return response.StatusCode == HttpStatusCode.OK;
            });
        }
        finally
        {
            streams.ForEach(stream => stream.Dispose());
        }
    }

    [Test]
    public async Task An_idle_stream_sends_its_cursor_every_15_seconds()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var clock = new HeartbeatClock();
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        using var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor=now");
        var head = await stream.NextAsync();
        await Assert.That(head.Event).IsEqualTo("cursor");
        for (var interval = 0; interval < 2; interval++)
        {
            var next = stream.NextAsync();
            // The server arms the interval after flushing its cursor; the client can read that cursor much later.
            await Assert.That(await clock.NextIntervalAsync(token)).IsEqualTo(TimeSpan.FromSeconds(15));
            clock.Advance(TimeSpan.FromSeconds(14));
            await Assert.That(next.IsCompleted).IsFalse();
            clock.Advance(TimeSpan.FromSeconds(1));
            var beat = await next.WaitAsync(token);
            await Assert.That(beat.Event).IsEqualTo("cursor");
            // The same position, issued again at the read before the heartbeat.
            await Assert.That(ChangeCursor.Decode(beat.Id)!.Stamps).IsEquivalentTo(ChangeCursor.Decode(head.Id)!.Stamps);
            await Assert.That(beat.Data.GetProperty("cursor").GetString()).IsEqualTo(beat.Id);
        }
    }

    private sealed class HeartbeatClock() : FakeTimeProvider(DateTimeOffset.UtcNow)
    {
        private readonly Channel<TimeSpan> _intervals = Channel.CreateBounded<TimeSpan>(1);

        public ValueTask<TimeSpan> NextIntervalAsync(CancellationToken token) => _intervals.Reader.ReadAsync(token);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (period == Timeout.InfiniteTimeSpan && dueTime == DatabaseChanges.Heartbeat && !_intervals.Writer.TryWrite(dueTime))
            {
                timer.Dispose();
                throw new InvalidOperationException("the previous heartbeat interval was not observed");
            }

            return timer;
        }
    }

    // REVIEW PROBE (not committed): sjc's origin o writes e then p. fra's stream from o lost e (retention cut) and fra
    // refilled e through RepairAsync (origin 0) before p arrived; gru received e and p on o's stream. A reader on fra
    // gets p, then leaves before fra's repair row of e, and resumes on gru. Expected: e is delivered on gru, or 410.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Review_probe_a_cursor_taken_on_a_refilled_machine_never_skips_the_repaired_event_on_another_region(bool repairAfterReaderPassedP)
    {
        await using var fra = await DocumentApi.StartAsync(values => values["region"] = "fra");
        await using var gru = await DocumentApi.StartAsync(values => values["region"] = "gru");
        var (fraRegion, _) = await IdentityAsync(fra);
        var (gruRegion, _) = await IdentityAsync(gru);
        await Assert.That((fraRegion, gruRegion)).IsEqualTo(("fra", "gru"));
        var o = ReplicaId.Of(4518, 20, 1);
        var hex = o.Value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow;
        var (e, eStamp) = (DocumentApi.NewId(), Stamp(now - TimeSpan.FromMinutes(2)));
        var (p, pStamp) = (DocumentApi.NewId(), Stamp(now - TimeSpan.FromMinutes(1)));
        var sink = (IChangeGroupSink)fra.Server.Services.GetRequiredService<IChangeGroupSink>();
        // gru: o's stream, e then p.
        _ = await gru.Server.Services.GetRequiredService<IChangeGroupSink>().ApplyAsync(await gru.TenantOfAsync(), o, [Group(1, e, eStamp, o), Group(2, p, pStamp, o)], CancellationToken.None);
        // Refill order: fra repairs e (origin 0, R6's RepairAsync), then o's stream resumes past the cut with p. Lost-apply
        // order (anti-entropy): fra applied p without e, a reader on fra took the cursor after p, then fra repairs e.
        var issued = new List<(string Id, string After, HashSet<string> Got)>();
        if (repairAfterReaderPassedP)
        {
            _ = await sink.ApplyAsync(await fra.TenantOfAsync(), o, [Group(3, p, pStamp, o)], CancellationToken.None);
            using var early = await Sse.OpenAsync(fra.Client, $"{fra.DatabasePath()}/changes");
            issued.Add(((await early.NextAsync()).Id, "start(before repair)", []));
            var first = (await early.ChangesAsync(1))[0];
            await Assert.That(first.Data.GetProperty("id").GetString()).IsEqualTo(p);
            issued.Add((first.Id, "p(before repair)", [p]));
        }

        await Assert.That(await sink.RepairAsync(await fra.TenantOfAsync(), [new RepairState(DocumentApi.Collection, e, Full(eStamp, o))], CancellationToken.None)).IsEqualTo(1);
        if (!repairAfterReaderPassedP)
        {
            _ = await sink.ApplyAsync(await fra.TenantOfAsync(), o, [Group(3, p, pStamp, o)], CancellationToken.None);
        }

        // Each machine's own write last: it bounds every resumed read there.
        var (fraSentinel, gruSentinel) = (DocumentApi.NewId(), DocumentApi.NewId());
        (await fra.PutAsync(fraSentinel, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        (await gru.PutAsync(gruSentinel, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        string Name(string? id)
        {
            return id == e ? "e" : id == p ? "p" : id == fraSentinel ? "fra-sentinel" : id == gruSentinel ? "gru-sentinel" : id ?? "?";
        }

        string Origin(string? origin)
        {
            return origin == hex ? "o" : "own";
        }

        // A reader on fra from the start of retention: every id fra issues, with what the reader holds when it gets it.
        var sequence = new List<string>();
        using (var stream = await Sse.OpenAsync(fra.Client, $"{fra.DatabasePath()}/changes"))
        {
            var got = new HashSet<string>(StringComparer.Ordinal);
            issued.Add(((await stream.NextAsync()).Id, "start", [.. got]));
            while (true)
            {
                var change = (await stream.ChangesAsync(1))[0];
                var id = change.Data.GetProperty("id").GetString()!;
                sequence.Add($"{Name(id)}({Origin(change.Data.GetProperty("origin").GetString())})");
                if (id == fraSentinel)
                {
                    break;
                }

                _ = got.Add(id);
                if (change.Id.Length > 0)
                {
                    issued.Add((change.Id, Name(id), [.. got]));
                }
            }
        }

        Console.WriteLine($"REVIEW-PROBE lost-apply={repairAfterReaderPassedP} o={hex} fra-feed=[{string.Join(", ", sequence)}]");
        var failures = new List<string>();
        foreach (var (cursor, after, got) in issued)
        {
            foreach (var (region, api, sentinel) in new[] { ("fra", fra, fraSentinel), ("gru", gru, gruSentinel) })
            {
                var uri = $"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(cursor)}";
                using (var probe = await api.Client.GetAsync(new Uri(uri, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead))
                {
                    if (probe.StatusCode != HttpStatusCode.OK)
                    {
                        Console.WriteLine($"REVIEW-PROBE lost-apply={repairAfterReaderPassedP} cursor-after={after} resumed-on={region} -> {(int)probe.StatusCode}");
                        if (probe.StatusCode != HttpStatusCode.Gone)
                        {
                            failures.Add($"after {after} on {region}: {(int)probe.StatusCode}");
                        }

                        continue;
                    }
                }

                using var resumed = await Sse.OpenAsync(api.Client, uri);
                _ = await resumed.NextAsync();
                var seen = new List<string>();
                while (seen.LastOrDefault() != sentinel)
                {
                    seen.Add((await resumed.ChangesAsync(1))[0].Data.GetProperty("id").GetString()!);
                }

                var missing = new[] { e, p }.Where(id => !got.Contains(id) && !seen.Contains(id)).Select(Name).ToList();
                Console.WriteLine($"REVIEW-PROBE lost-apply={repairAfterReaderPassedP} cursor-after={after} resumed-on={region} -> 200 [{string.Join(", ", seen.Select(Name))}] missing=[{string.Join(", ", missing)}]");
                if (missing.Count > 0)
                {
                    failures.Add($"after {after} on {region}: 200 without {string.Join(", ", missing)}");
                }
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    private static Hlc Stamp(DateTimeOffset at) => new((ulong)at.ToUnixTimeMilliseconds() << 16);

    private static DocumentState Full(Hlc stamp, ReplicaId origin)
    {
        using var body = JsonDocument.Parse("""{"joined":true}""");
        return DocumentState.Bottom.Put(new EntryVersion(stamp, origin), body.RootElement);
    }

    private static OriginGroup Group(long changeId, string id, Hlc stamp, ReplicaId origin) =>
        new(changeId, [new ChangeDelta(DocumentApi.Collection, id, stamp, Full(stamp, origin).DeltaAt(stamp))]);

    private static async Task<(string? Region, string? Machine)> IdentityAsync(DocumentApi api)
    {
        using var health = await api.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        using var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        return (body.RootElement.TryGetProperty("region", out var region) ? region.GetString() : null,
            body.RootElement.TryGetProperty("machine", out var machine) ? machine.GetString() : null);
    }

    // Joins one document written by `origin` at `at` into the store, as a peer stream does; returns its stamp.
    private static async Task<Hlc> JoinAsync(IDatabaseStore store, ReplicaId origin, DateTimeOffset at)
    {
        var version = new EntryVersion(new Hlc((ulong)at.ToUnixTimeMilliseconds() << 16), origin);
        using var body = JsonDocument.Parse("""{"joined":true}""");
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        _ = await transaction.JoinAsync(DocumentApi.Collection, DocumentApi.NewId(), DocumentState.Bottom.Put(version, body.RootElement).DeltaAt(version.Stamp), version, CancellationToken.None);
        _ = await transaction.CommitAsync(CancellationToken.None);
        return version.Stamp;
    }

    // One retention pass deleting the feed events stamped below `before`; returns how many it deleted.
    private static async Task<int> TrimAsync(IDatabaseStore store, Hlc before)
    {
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        var deleted = await transaction.TrimFeedAsync(before, 1024, CancellationToken.None);
        _ = await transaction.CommitAsync(CancellationToken.None);
        return deleted;
    }

    private static byte[] Origin(ulong origin, ulong stamp)
    {
        var bytes = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, origin);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), stamp);
        return bytes;
    }

    /// <summary>One SSE event: its name, id and JSON data.</summary>
    private sealed record SseEvent(string Event, string Id, JsonElement Data);

    /// <summary>An open SSE response read one event at a time.</summary>
    private sealed class Sse : IDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;

        private Sse(HttpResponseMessage response, StreamReader reader)
        {
            _response = response;
            _reader = reader;
        }

        public static async Task<Sse> OpenAsync(HttpClient client, string uri, string? lastEventId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.Relative));
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (lastEventId is not null)
            {
                request.Headers.Add("Last-Event-ID", lastEventId);
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                var text = await response.Content.ReadAsStringAsync();
                response.Dispose();
                throw new InvalidOperationException($"the stream answered {response.StatusCode}: {text}");
            }

            return new Sse(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
        }

        public async Task<SseEvent> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? name = null, id = null, data = null;
            while (await _reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    using var json = JsonDocument.Parse(data ?? throw new InvalidDataException("an event without data"));
                    return new SseEvent(name ?? "message", id ?? "", json.RootElement.Clone());
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var (field, value) = (line[..colon], line[(colon + 1)..].TrimStart(' '));
                (name, id, data) = field switch
                {
                    "event" => (value, id, data),
                    "id" => (name, value, data),
                    "data" => (name, id, data + value),
                    _ => (name, id, data),
                };
            }

            throw new EndOfStreamException("the stream ended");
        }

        // The next `count` change events, skipping heartbeats.
        public async Task<List<SseEvent>> ChangesAsync(int count)
        {
            var changes = new List<SseEvent>();
            while (changes.Count < count)
            {
                if (await NextAsync() is { Event: "change" } change)
                {
                    changes.Add(change);
                }
            }

            return changes;
        }

        public void Dispose()
        {
            _reader.Dispose();
            _response.Dispose();
        }
    }
}
