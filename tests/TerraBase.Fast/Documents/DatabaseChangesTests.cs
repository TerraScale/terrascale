using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Turso.Data.Sqlite;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Documents;
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
    public async Task An_anti_entropy_repair_is_filed_under_the_original_node_and_its_cursor_resumes()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        // A repair joins with origin 0 (R6), which no cursor can name; the event it brings is gru's.
        var gru = ReplicaId.Of(4518, 13, 1);
        var id = DocumentApi.NewId();
        var stamp = await JoinAsync(store, gru, DateTimeOffset.UtcNow, repair: true, id);
        using var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes");
        _ = await stream.NextAsync();
        var repaired = await stream.NextAsync();
        await Assert.That(repaired.Data.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(repaired.Data.GetProperty("origin").GetString()).IsEqualTo(gru.Value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(repaired.Data.GetProperty("version").GetString()).IsEqualTo(stamp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var after = DocumentApi.NewId();
        (await api.PutAsync(after, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        using var resumed = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(repaired.Id)}");
        _ = await resumed.NextAsync();
        await Assert.That((await resumed.NextAsync()).Data.GetProperty("id").GetString()).IsEqualTo(after);
    }

    [Test]
    public async Task A_repair_below_an_origins_newest_event_here_makes_a_cursor_past_it_410_on_this_machine()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var gru = ReplicaId.Of(4518, 14, 1);
        var now = DateTimeOffset.UtcNow;
        // gru's later event arrives on the stream first; a reader takes it.
        _ = await JoinAsync(store, gru, now, repair: false, DocumentApi.NewId());
        string past;
        using (var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes"))
        {
            _ = await stream.NextAsync();
            past = (await stream.ChangesAsync(1))[0].Id;
        }

        // Then a repair brings gru's earlier event: the reader's cursor is past it, so here it must not resume silently.
        var hole = DocumentApi.NewId();
        _ = await JoinAsync(store, gru, now - TimeSpan.FromSeconds(1), repair: true, hole);
        // A broken refill ordering guarantee pages: the hole is counted.
        await Assert.That(await api.Server.MetricAsync("terrabase_feed_holes_total")).IsEqualTo(1);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(past)}", UriKind.Relative)),
            HttpStatusCode.Gone, "cursor_expired", "events after the cursor are no longer retained here; start again with cursor=now and re-read");
        // A reader from the start of retention still gets the repaired event.
        using var fresh = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes");
        _ = await fresh.NextAsync();
        await Assert.That((await fresh.ChangesAsync(2)).Select(change => change.Data.GetProperty("id").GetString())).Contains(hole);
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
        await using var api = await DocumentApi.StartAsync();
        using var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor=now");
        var head = await stream.NextAsync();
        var started = TimeProvider.System.GetTimestamp();
        var beat = await stream.NextAsync();
        await Assert.That(beat.Event).IsEqualTo("cursor");
        // The same position, issued again at the read before the heartbeat.
        await Assert.That(ChangeCursor.Decode(beat.Id)!.Stamps).IsEquivalentTo(ChangeCursor.Decode(head.Id)!.Stamps);
        await Assert.That(TimeProvider.System.GetElapsedTime(started)).IsGreaterThan(DatabaseChanges.Heartbeat - TimeSpan.FromSeconds(1));
    }

    // Joins one document written by `origin` at `at` into the store, as a peer stream does; returns its stamp.
    private static Task<Hlc> JoinAsync(IDatabaseStore store, ReplicaId origin, DateTimeOffset at) => JoinAsync(store, origin, at, repair: false, DocumentApi.NewId());

    // Joins one document written by `origin` at `at` into the store: as a peer stream does, or as an anti-entropy repair
    // does (origin 0 on the join, R6); returns its stamp.
    private static async Task<Hlc> JoinAsync(IDatabaseStore store, ReplicaId origin, DateTimeOffset at, bool repair, string id)
    {
        var version = new EntryVersion(new Hlc((ulong)at.ToUnixTimeMilliseconds() << 16), origin);
        using var body = JsonDocument.Parse("""{"joined":true}""");
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        var delta = DocumentState.Bottom.Put(version, body.RootElement);
        _ = await transaction.JoinAsync(DocumentApi.Collection, id, repair ? delta : delta.DeltaAt(version.Stamp),
            repair ? new EntryVersion(version.Stamp, default) : version, CancellationToken.None);
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
