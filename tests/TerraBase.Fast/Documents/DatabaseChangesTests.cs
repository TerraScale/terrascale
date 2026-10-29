using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Documents;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

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
        var last = DocumentApi.NewId();
        (await api.PutAsync(last, new { name = "last" })).Status.ShouldBe(HttpStatusCode.Created);
        var next = await filtered.NextAsync();
        await Assert.That(next.Data.GetProperty("id").GetString()).IsEqualTo(last);
        using var resumed = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?collections={DocumentApi.Collection}&cursor={Uri.EscapeDataString(items[^1].Id)}");
        _ = await resumed.NextAsync();
        await Assert.That((await resumed.NextAsync()).Data.GetProperty("id").GetString()).IsEqualTo(last);
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
    public async Task A_cursor_issued_in_the_future_counts_as_issued_now()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var future = ChangeCursor.Encode(new FeedCursor(new Dictionary<ReplicaId, Hlc>(), FeedCursor.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(30))), ChangeCursor.Scope(await api.TenantOfAsync(), null));
        var joined = await PutStampAsync(store);
        await Assert.That(await TrimAsync(store, new Hlc(joined.Value + 1))).IsEqualTo(1);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(future)}", UriKind.Relative)),
            HttpStatusCode.Gone, "cursor_expired", "events after the cursor are no longer retained here; start again with cursor=now and re-read");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task A_cursor_that_does_not_decode_is_410(int origins)
    {
        await using var api = await DocumentApi.StartAsync();
        byte[] old = [0, .. Enumerable.Range(1, origins).SelectMany(origin => Origin((ulong)origin, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16))];
        var text = Convert.ToBase64String(old).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var changes = $"{api.DatabasePath()}/changes";
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{changes}?cursor={text}", UriKind.Relative)),
            HttpStatusCode.Gone, "cursor_expired", "the cursor cannot be resumed; start again with cursor=now and re-read");
        using var resume = new HttpRequestMessage(HttpMethod.Get, new Uri(changes, UriKind.Relative));
        resume.Headers.Add("Last-Event-ID", text);
        await ErrorAssert.ErrorAsync(api.Client.SendAsync(resume), HttpStatusCode.Gone, "cursor_expired", "the cursor cannot be resumed; start again with cursor=now and re-read");
    }

    [Test]
    public async Task Undecodable_cursors_are_410_invalid_filters_400_a_missing_scope_403_and_an_unknown_database_404()
    {
        await using var api = await DocumentApi.StartAsync();
        var changes = $"{api.DatabasePath()}/changes";
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{changes}?cursor=AQ-not-a-cursor", UriKind.Relative)),
            HttpStatusCode.Gone, "cursor_expired", "the cursor cannot be resumed; start again with cursor=now and re-read");
        var canonical = ChangeCursor.Encode(new FeedCursor(new Dictionary<ReplicaId, Hlc> { [new ReplicaId(1)] = new Hlc(5), [new ReplicaId(2)] = new Hlc(5) }, FeedCursor.At(DateTimeOffset.UtcNow)));
        var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(canonical);
        var unordered = System.Buffers.Text.Base64Url.EncodeToString([.. bytes[..18], .. bytes[74..], .. bytes[18..74]]);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{changes}?cursor={unordered}", UriKind.Relative)),
            HttpStatusCode.Gone, "cursor_expired", "the cursor cannot be resumed; start again with cursor=now and re-read");
        foreach (var machine in new[] { "invalid/name", new string('a', 64), "é" })
        {
            var machineBytes = Encoding.UTF8.GetBytes(machine);
            var invalidMachine = System.Buffers.Text.Base64Url.EncodeToString([.. bytes[..17], (byte)machineBytes.Length, .. machineBytes, .. bytes[18..]]);
            await ErrorAssert.ErrorAsync(api.Client.GetAsync(new Uri($"{changes}?cursor={invalidMachine}", UriKind.Relative)),
                HttpStatusCode.Gone, "cursor_expired", "the cursor cannot be resumed; start again with cursor=now and re-read");
        }

        using var lastEventId = new HttpRequestMessage(HttpMethod.Get, new Uri(changes, UriKind.Relative));
        lastEventId.Headers.Add("Last-Event-ID", "garbage");
        await ErrorAssert.ErrorAsync(api.Client.SendAsync(lastEventId), HttpStatusCode.Gone, "cursor_expired", "the cursor cannot be resumed; start again with cursor=now and re-read");
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
    public async Task A_database_bounds_streams_per_machine_and_a_closed_one_frees_its_place()
    {
        await using var api = await DocumentApi.StartAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "pro");
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
    public async Task A_hundred_readers_share_a_write_and_one_leaving_does_not_cancel_the_rest()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "pro", token);
        var streams = new List<Sse>();
        try
        {
            string? cursor = null;
            var scope = ChangeCursor.Scope(await api.TenantOfAsync(cancellationToken: token), null);
            for (var i = 0; i < 100; i++)
            {
                var resume = cursor is null ? "now" : ChangeCursor.Encode(ChangeCursor.Decode(cursor)! with
                {
                    Issued = FeedCursor.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(i)),
                }, scope);
                var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(resume)}");
                streams.Add(stream);
                cursor = (await stream.NextAsync()).Id;
            }

            var before = await api.Server.MetricAsync("terrabase_storage_reads_total", "path=\"feed\"", token);
            streams[0].Dispose();
            streams.RemoveAt(0);
            var id = DocumentApi.NewId();
            (await api.PutAsync(id, new { shared = true })).Status.ShouldBe(HttpStatusCode.Created);
            var changes = await Task.WhenAll(streams.Select(stream => stream.NextAsync()));
            foreach (var change in changes)
            {
                await Assert.That(change.Event).IsEqualTo("change");
                await Assert.That(change.Data.GetProperty("id").GetString()).IsEqualTo(id);
            }

            var reads = await api.Server.MetricAsync("terrabase_storage_reads_total", "path=\"feed\"", token) - before;
            await Assert.That(reads).IsLessThan(20);
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
        using var initial = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor=now");
        var head = await initial.NextAsync();
        using var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(head.Id)}");
        _ = await stream.NextAsync();
        await Assert.That(head.Event).IsEqualTo("cursor");
        for (var interval = 0; interval < 2; interval++)
        {
            var next = stream.NextAsync();
            await Assert.That(await clock.NextIntervalAsync(token)).IsEqualTo(TimeSpan.FromSeconds(15));
            await Assert.That(await clock.NextIntervalAsync(token)).IsEqualTo(TimeSpan.FromSeconds(15));
            clock.Advance(TimeSpan.FromSeconds(14));
            await Assert.That(next.IsCompleted).IsFalse();
            clock.Advance(TimeSpan.FromSeconds(1));
            var beat = await next.WaitAsync(token);
            await Assert.That(beat.Event).IsEqualTo("cursor");
            await Assert.That(ChangeCursor.Decode(beat.Id)!.Stamps).IsEquivalentTo(ChangeCursor.Decode(head.Id)!.Stamps);
            await Assert.That(beat.Data.GetProperty("cursor").GetString()).IsEqualTo(beat.Id);
            if (interval > 0)
            {
                await Assert.That(ChangeCursor.Decode(beat.Id)!.Issued.Value).IsGreaterThan(ChangeCursor.Decode(head.Id)!.Issued.Value);
            }
        }
    }

    private sealed class HeartbeatClock() : FakeTimeProvider(DateTimeOffset.UtcNow)
    {
        private readonly Channel<TimeSpan> _intervals = Channel.CreateBounded<TimeSpan>(2);

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

    private static async Task<Hlc> PutStampAsync(IDatabaseStore store)
    {
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        var stamp = await transaction.PutAsync(DocumentApi.Collection, DocumentApi.NewId(), """{"n":1}"""u8.ToArray(), CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return new Hlc(unchecked((ulong)stamp));
    }

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

    private sealed record SseEvent(string Event, string Id, JsonElement Data);
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
