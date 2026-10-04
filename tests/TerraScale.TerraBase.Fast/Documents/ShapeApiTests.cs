using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Features.Documents;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Fast.Documents;

public sealed class ShapeApiTests
{
    private const string Open = """{"eq":["/status","open"]}""";
    // The origin sends "private, max-age=0, must-revalidate"; HttpClient reorders directives when it parses the
    // header, so compare the raw value.
    private const string Revalidate = "private, max-age=0, must-revalidate";

    private readonly Faker _faker = new();

    [Test]
    public async Task A_shape_syncs_a_snapshot_then_the_log_with_cacheable_headers()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var (a, b, c) = Ids3(Ids(3));
        await PutAsync(api, a, "open");
        await PutAsync(api, b, "closed");
        var head = await PutAsync(api, c, "open");

        using var start = await GetAsync(shapes, $"{api.Shape}?where={Uri.EscapeDataString(Open)}&offset=-1");
        var handle = Header(start, "TB-Shape-Handle");
        var location = start.Headers.Location!.OriginalString;
        using var snapshot = await GetAsync(shapes, location);
        var page = await JsonAsync(snapshot);
        var shapeId = page.GetProperty("shape_id").GetString();
        // Only the fresh read of the log from S says up to date, never a year-cacheable snapshot page.
        using var caughtUp = await GetAsync(shapes, $"{api.Shape}?where={Uri.EscapeDataString(Open)}&handle={handle}&offset={head}");
        var tail = await JsonAsync(caughtUp);

        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(CacheControl(start)).IsEqualTo(Revalidate);
        await Assert.That(location).IsEqualTo($"{api.Shape}?where={Uri.EscapeDataString(Open)}&handle={handle}&offset=snap.{head}.0");
        await Assert.That(handle).StartsWith("h1-");
        await Assert.That(snapshot.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(CacheControl(snapshot)).IsEqualTo(Revalidate);
        await Assert.That(snapshot.Headers.Vary.ToList()).IsEquivalentTo(["Authorization"]);
        await Assert.That(snapshot.Headers.ETag!.Tag).Matches($"^\"{shapeId}:{handle}:snap\\.{head}\\.0:{head}:[0-9a-f]{{16}}\"$");
        await Assert.That(snapshot.Headers.ETag.IsWeak).IsFalse();
        await Assert.That(Header(snapshot, "TB-Shape-Next-Offset")).IsEqualTo($"{head}");
        await Assert.That(Header(snapshot, "TB-Shape-Up-To-Date")).IsEqualTo("false");
        await Assert.That(page.GetProperty("next_offset").GetString()).IsEqualTo($"{head}");
        await Assert.That(page.GetProperty("up_to_date").GetBoolean()).IsFalse();
        await Assert.That(CacheControl(caughtUp)).IsEqualTo(Revalidate);
        await Assert.That(tail.GetProperty("up_to_date").GetBoolean()).IsTrue();
        await Assert.That(tail.GetProperty("changes").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Ops(page)).IsEquivalentTo(Sorted(("upsert", a), ("upsert", c)));
        await Assert.That(page.GetProperty("changes")[0].GetProperty("offset").GetString()).IsEqualTo($"{head}");

        // a leaves the shape, d enters it, c is deleted; b changes but never matched.
        var closedA = await PutAsync(api, a, "closed");
        await PutAsync(api, b, "closed");
        var added = DocumentApi.NewId();
        var openD = await PutAsync(api, added, "open");
        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(c));
        var last = long.Parse(deleted.Json.GetProperty("offset").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        var logUri = $"{api.Shape}?where={Uri.EscapeDataString(Open)}&handle={handle}&offset={head}";
        using var log = await GetAsync(shapes, logUri);
        var chunk = await JsonAsync(log);
        var commits = chunk.GetProperty("changes").EnumerateArray()
            .Select(commit => $"{commit.GetProperty("offset").GetString()}: {string.Join(", ", Ops(commit.GetProperty("ops")))}").ToList();
        using var revalidated = await GetAsync(shapes, logUri, log.Headers.ETag!.ToString());

        await Assert.That(log.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(CacheControl(log)).IsEqualTo(Revalidate);
        await Assert.That(log.Headers.Vary.ToList()).IsEquivalentTo(["Authorization"]);
        await Assert.That(chunk.GetProperty("offset").GetString()).IsEqualTo($"{head}");
        await Assert.That(chunk.GetProperty("next_offset").GetString()).IsEqualTo($"{last}");
        await Assert.That(chunk.GetProperty("up_to_date").GetBoolean()).IsTrue();
        // b never matched, yet a changed non-matching document still reads as a delete.
        await Assert.That(commits).IsEquivalentTo(
            [$"{closedA}: delete {a}", $"{closedA + 1}: delete {b}", $"{openD}: upsert {added}", $"{last}: delete {c}"]);
        await Assert.That(revalidated.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(revalidated.Headers.ETag!.Tag).IsEqualTo(log.Headers.ETag.Tag);
    }

    [Test]
    public async Task Cached_snapshots_keep_their_bytes_and_resume_to_the_latest_state_after_rewrites_and_deletes()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var ids = Ids(1001).Order(StringComparer.Ordinal).ToArray();
        await api.TransactAsync([.. ids.Select(id => new { op = "put", collection = DocumentApi.Collection, id, document = new { status = "original" } })]);
        await api.SendRawAsync(HttpMethod.Put, api.Document(ids[0]), """{"status":"original","html":"<b>&amp;</b>"}""", "application/json");
        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        var firstUri = start.Headers.Location!.OriginalString;
        var handle = Header(start, "TB-Shape-Handle");
        using var first = await GetAsync(shapes, firstUri);
        var firstText = await first.Content.ReadAsStringAsync();
        var firstPage = await JsonAsync(first);
        var secondUri = $"{api.Shape}?handle={handle}&offset={firstPage.GetProperty("next_offset").GetString()}";

        await api.PutAsync(ids[0], new { status = "intermediate" });
        await api.PutAsync(ids[0], new { status = "latest" });
        await api.SendAsync(HttpMethod.Delete, api.Document(ids[^1]));
        var added = DocumentApi.NewId();
        await api.PutAsync(added, new { status = "added" });
        // The second page has never been fetched: it must still include the document deleted after S.
        using var second = await GetAsync(shapes, secondUri);
        var secondPage = await JsonAsync(second);
        var secondText = await second.Content.ReadAsStringAsync();
        await api.PutAsync(ids[1], new { status = "also-latest" });
        using var firstReplay = await GetAsync(shapes, firstUri);
        using var secondReplay = await GetAsync(shapes, secondUri);
        using var conditional = await GetAsync(shapes, firstUri, first.Headers.ETag!.ToString());
        using var resumed = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={secondPage.GetProperty("next_offset").GetString()}");
        var log = await JsonAsync(resumed);
        var state = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Apply(state, firstPage);
        Apply(state, secondPage);
        Apply(state, log);
        var actual = state.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}:{pair.Value.GetProperty("status").GetString()}").ToList();
        var expected = ids.Take(1000).Select(id => $"{id}:{(id == ids[0] ? "latest" : id == ids[1] ? "also-latest" : "original")}")
            .Append($"{added}:added").Order(StringComparer.Ordinal).ToList();

        await Assert.That(firstText).Contains("<b>&amp;</b>");
        await Assert.That(await firstReplay.Content.ReadAsStringAsync()).IsEqualTo(firstText);
        await Assert.That(await secondReplay.Content.ReadAsStringAsync()).IsEqualTo(secondText);
        await Assert.That(firstReplay.Headers.ETag!.Tag).IsEqualTo(first.Headers.ETag.Tag);
        await Assert.That(secondReplay.Headers.ETag!.Tag).IsEqualTo(second.Headers.ETag!.Tag);
        await Assert.That(conditional.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(Ops(secondPage)).IsEquivalentTo(Sorted(("upsert", ids[^1])));
        await Assert.That(log.GetProperty("up_to_date").GetBoolean()).IsTrue();
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    public async Task A_live_poll_wakes_when_its_collection_is_dropped_and_recreated()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        using var metrics = new ShapeMetrics(api.Server.Services);
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");
        var collections = api.Collections;

        // Collection commits also advance the offset and wake the poll.
        var poll = GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}&live=true");
        await metrics.WaitingAsync(1);
        await api.SendAsync(HttpMethod.Delete, $"{collections}/{DocumentApi.Collection}");
        await api.SendAsync(HttpMethod.Post, collections, new { name = DocumentApi.Collection });
        using var response = await poll.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound).IsTrue();
    }

    [Test]
    public async Task A_client_resumes_from_its_last_offset_and_pages_through_a_large_snapshot()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var ids = Ids(1001);
        var written = await api.TransactAsync([.. ids.Select(id => new { op = "put", collection = DocumentApi.Collection, id, document = Document("open") })]);
        var head = written.Json.GetProperty("offset").GetString();

        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        var handle = Header(start, "TB-Shape-Handle");
        using var first = await GetAsync(shapes, start.Headers.Location!.OriginalString);
        var firstPage = await JsonAsync(first);
        using var second = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={firstPage.GetProperty("next_offset").GetString()}");
        var secondPage = await JsonAsync(second);

        var resumeAt = secondPage.GetProperty("next_offset").GetString();
        var later = DocumentApi.NewId();
        await PutAsync(api, later, "open");
        using var resumed = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={resumeAt}");
        var tail = await JsonAsync(resumed);

        await Assert.That(firstPage.GetProperty("changes")[0].GetProperty("ops").GetArrayLength()).IsEqualTo(1000);
        await Assert.That(firstPage.GetProperty("next_offset").GetString()).IsEqualTo($"snap.{head}.{Base64Url(ids.Order(StringComparer.Ordinal).ElementAt(999))}");
        await Assert.That(firstPage.GetProperty("up_to_date").GetBoolean()).IsFalse();
        await Assert.That(Header(first, "TB-Shape-Up-To-Date")).IsEqualTo("false");
        await Assert.That(CacheControl(second)).IsEqualTo(Revalidate);
        await Assert.That(Ops(secondPage)).IsEquivalentTo(Sorted(("upsert", ids.Order(StringComparer.Ordinal).Last())));
        await Assert.That(resumeAt).IsEqualTo(head);
        await Assert.That(Ops(tail)).IsEquivalentTo(Sorted(("upsert", later)));
    }

    [Test]
    public async Task A_live_poll_waits_at_the_head_and_wakes_on_a_write()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        using var metrics = new ShapeMetrics(api.Server.Services);
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?where={Uri.EscapeDataString(Open)}&offset=-1");

        var poll = GetAsync(shapes, $"{api.Shape}?where={Uri.EscapeDataString(Open)}&handle={handle}&offset={head}&live=true");
        await metrics.WaitingAsync(1);
        var id = DocumentApi.NewId();
        var written = await PutAsync(api, id, "open");
        using var response = await poll;
        var body = await JsonAsync(response);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(CacheControl(response)).IsEqualTo(Revalidate);
        await Assert.That(long.Parse(Header(response, "TB-Shape-Cursor"), System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 20).Within(1);
        await Assert.That(body.GetProperty("next_offset").GetString()).IsEqualTo($"{written}");
        await Assert.That(body.GetProperty("up_to_date").GetBoolean()).IsTrue();
        await Assert.That(Ops(body)).IsEquivalentTo(Sorted(("upsert", id)));
    }

    [Test]
    public async Task Concurrent_live_pollers_share_one_storage_read()
    {
        const int Pollers = 25;
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        using var metrics = new ShapeMetrics(api.Server.Services);
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");
        var before = metrics.Reads;

        var uri = $"{api.Shape}?handle={handle}&offset={head}&live=true";
        var polls = Enumerable.Range(0, Pollers).Select(_ => GetAsync(shapes, uri)).ToList();
        await metrics.WaitingAsync(Pollers);
        var readsWhileWaiting = metrics.Reads - before;
        await PutAsync(api, DocumentApi.NewId(), "open");
        var responses = await Task.WhenAll(polls);
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
        var readsAfterWake = metrics.Reads - before - readsWhileWaiting;
        foreach (var response in responses)
        {
            response.Dispose();
        }

        // Every poller joined one shared read before the commit, and one more read answered them all.
        await Assert.That(readsWhileWaiting).IsEqualTo(1);
        await Assert.That(readsAfterWake).IsEqualTo(1);
        await Assert.That(responses.Select(response => response.StatusCode).Distinct().ToList()).IsEquivalentTo([HttpStatusCode.OK]);
        await Assert.That(bodies.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Live_cursors_advance_in_a_frozen_bucket_for_late_joining_shared_pollers()
    {
        var clock = new ShapeClock { UnixSeconds = 20001 };
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        var server = api.Server;
        using var client = await api.ClientWithScopesAsync("documents:read", "documents:write", "shapes:read");
        using var metrics = new ShapeMetrics(server.Services);
        var (handle, head) = await SnapshotAsync(client, $"{api.Shape}?offset=-1");
        await DocumentApi.SendAsync(client, HttpMethod.Put, api.Document(DocumentApi.NewId()), JsonContent.Create(new { status = "first" }));
        using var first = await GetAsync(client, $"{api.Shape}?handle={handle}&offset={head}&live=true&cursor=1000");
        var cursor = Header(first, "TB-Shape-Cursor");
        var current = Header(first, "TB-Shape-Next-Offset");
        var before = metrics.Reads;
        var uri = $"{api.Shape}?handle={handle}&offset={current}&live=true&cursor={cursor}";
        var early = GetAsync(client, uri);
        await metrics.WaitingAsync(1);
        // A late requester joins the same wait after the clock moves backwards.
        clock.UnixSeconds = 19999;
        var late = GetAsync(client, uri);
        var ahead = GetAsync(client, $"{api.Shape}?handle={handle}&offset={current}&live=true&cursor=1010");
        await metrics.WaitingAsync(3);
        var waitingReads = metrics.Reads - before;
        await DocumentApi.SendAsync(client, HttpMethod.Put, api.Document(DocumentApi.NewId()), JsonContent.Create(new { status = "last" }));
        using var earlyResponse = await early;
        using var lateResponse = await late;
        using var aheadResponse = await ahead;
        using var overflow = await GetAsync(client, $"{api.Shape}?handle={handle}&offset={current}&live=true&cursor={long.MaxValue}");

        await Assert.That(cursor).IsEqualTo("1001");
        await Assert.That(Header(earlyResponse, "TB-Shape-Cursor")).IsEqualTo("1002");
        await Assert.That(Header(lateResponse, "TB-Shape-Cursor")).IsEqualTo("1002");
        await Assert.That(Header(aheadResponse, "TB-Shape-Cursor")).IsEqualTo("1011");
        await Assert.That(await earlyResponse.Content.ReadAsStringAsync()).IsEqualTo(await lateResponse.Content.ReadAsStringAsync());
        await Assert.That(waitingReads).IsEqualTo(1);
        await Assert.That(overflow.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Shape_admission_rejects_new_flights_but_shares_existing_reads_and_releases_cancelled_slots()
    {
        var clock = new ShapeClock { UnixSeconds = 20001 };
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        var server = api.Server;
        using var client = await api.ClientWithScopesAsync("documents:read", "documents:write", "shapes:read");
        using var metrics = new ShapeMetrics(server.Services);
        var uris = new List<string>();
        for (var slot = 0; slot <= ShapeService.MaxFlights; slot++)
        {
            var where = Uri.EscapeDataString($$"""{"eq":["/slot",{{slot}}]}""");
            var (handle, head) = await SnapshotAsync(client, $"{api.Shape}?where={where}&offset=-1");
            uris.Add($"{api.Shape}?where={where}&handle={handle}&offset={head}&live=true");
        }

        using var allCancel = new CancellationTokenSource();
        using var victimCancel = new CancellationTokenSource();
        var polls = new List<Task<HttpResponseMessage>>();
        var before = metrics.Reads;
        try
        {
            for (var slot = 0; slot < ShapeService.MaxFlights; slot++)
            {
                polls.Add(GetAsync(client, uris[slot], cancellationToken: slot == ShapeService.MaxFlights - 1 ? victimCancel.Token : allCancel.Token));
            }

            await metrics.WaitingAsync(ShapeService.MaxFlights);
            await metrics.FlightsAsync(ShapeService.MaxFlights);
            using var newFlight = await GetAsync(client, uris[^1]);
            await Assert.That(newFlight.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That((await JsonAsync(newFlight)).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("shape_busy");
            await Assert.That(newFlight.Headers.CacheControl!.ToString()).IsEqualTo("no-store");

            // Joining an occupied flight is still allowed at the distinct-flight limit.
            for (var joined = ShapeService.MaxFlights; joined < ShapeService.MaxRequesters; joined++)
            {
                polls.Add(GetAsync(client, uris[0], cancellationToken: allCancel.Token));
            }

            await metrics.WaitingAsync(ShapeService.MaxRequesters);
            await Assert.That(metrics.Reads - before).IsEqualTo(ShapeService.MaxFlights);
            using var excessWaiter = await GetAsync(client, uris[0]);
            await Assert.That(excessWaiter.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(excessWaiter.Headers.CacheControl!.ToString()).IsEqualTo("no-store");

            await victimCancel.CancelAsync();
            await Assert.ThrowsAsync<TaskCanceledException>(() => polls[ShapeService.MaxFlights - 1]);
            await metrics.FlightsAsync(ShapeService.MaxFlights - 1);
            await metrics.WaitingExactlyAsync(ShapeService.MaxRequesters - 1);
            polls.Add(GetAsync(client, uris[^1], cancellationToken: allCancel.Token));
            await metrics.FlightsAsync(ShapeService.MaxFlights);
            await metrics.WaitingAsync(ShapeService.MaxRequesters);
            await Assert.That(metrics.Reads - before).IsEqualTo(ShapeService.MaxFlights + 1);
            await allCancel.CancelAsync();
            await Assert.ThrowsAsync<TaskCanceledException>(() => Task.WhenAll(polls));
            await metrics.FlightsAsync(0);
            await metrics.WaitingExactlyAsync(0);

            // A thrown read error also releases admission; missing collections cannot consume slots.
            for (var error = 0; error <= ShapeService.MaxFlights; error++)
            {
                using var missing = await GetAsync(client, $"{api.Collections}/missing/shape?offset=-1");
                await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            using var restart = await GetAsync(client, $"{api.Shape}?offset=-1");
            await Assert.That(restart.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
            await metrics.FlightsAsync(0);
        }
        finally
        {
            await allCancel.CancelAsync();
            await victimCancel.CancelAsync();
        }
    }

    [Test]
    public async Task Escape_heavy_shape_filters_follow_compact_redirects_or_fail_before_an_unreachable_URL()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var value = new string('<', 2000);
        var id = DocumentApi.NewId();
        await api.PutAsync(id, new { value });
        var where = Uri.EscapeDataString($$"""{"eq":["/value","{{value}}"]}""");
        using var start = await GetAsync(shapes, $"{api.Shape}?where={where}&offset=-1");
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(start.Version).IsEqualTo(HttpVersion.Version11);
        var location = start.Headers.Location!.OriginalString;
        await Assert.That(location.Length).IsLessThan(8 * 1024);
        using var snapshot = await GetAsync(shapes, location);
        var body = await JsonAsync(snapshot);
        var next = body.GetProperty("next_offset").GetString();
        using var resumed = await GetAsync(shapes, location[..location.IndexOf("offset=", StringComparison.Ordinal)] + $"offset={next}");
        await Assert.That(snapshot.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ops(body)).IsEquivalentTo(Sorted(("upsert", id)));
        await Assert.That(resumed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await JsonAsync(resumed)).GetProperty("up_to_date").GetBoolean()).IsTrue();

        // U+0085 remains on the encoder's Unicode blocklist: compact JSON alone cannot ensure fit.
        var blocked = new string('\u0085', 1000);
        where = Uri.EscapeDataString($$"""{"eq":["/value","{{blocked}}"]}""");
        using var tooLong = await GetAsync(shapes, $"{api.Shape}?where={where}&offset=-1");
        await Assert.That(tooLong.StatusCode).IsEqualTo(HttpStatusCode.RequestUriTooLong);
        await Assert.That((await JsonAsync(tooLong)).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("uri_too_long");
        await Assert.That(tooLong.Headers.Location).IsNull();
        await Assert.That(tooLong.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
    }

    [Test]
    public async Task Cancelling_shared_pollers_preserves_the_remaining_wait_and_releases_the_last_flight()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        using var metrics = new ShapeMetrics(api.Server.Services);
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");
        var uri = $"{api.Shape}?handle={handle}&offset={head}&live=true";
        var before = metrics.Reads;
        using var cancelled = new CancellationTokenSource();
        var leaving = GetAsync(shapes, uri, cancellationToken: cancelled.Token);
        var remaining = GetAsync(shapes, uri);
        await metrics.WaitingAsync(2);
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => leaving);
        await metrics.WaitingExactlyAsync(1);
        await Assert.That(remaining.IsCompleted).IsFalse();
        await Assert.That(metrics.Reads - before).IsEqualTo(1);
        var id = DocumentApi.NewId();
        var written = await PutAsync(api, id, "open");
        using var response = await remaining.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ops(await JsonAsync(response))).IsEquivalentTo(Sorted(("upsert", id)));
        await metrics.WaitingExactlyAsync(0);

        uri = $"{api.Shape}?handle={handle}&offset={written}&live=true";
        using var firstCancel = new CancellationTokenSource();
        using var lastCancel = new CancellationTokenSource();
        var first = GetAsync(shapes, uri, cancellationToken: firstCancel.Token);
        var last = GetAsync(shapes, uri, cancellationToken: lastCancel.Token);
        await metrics.WaitingAsync(2);
        await firstCancel.CancelAsync();
        await lastCancel.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => first);
        await Assert.ThrowsAsync<TaskCanceledException>(() => last);
        await metrics.WaitingExactlyAsync(0);
        var freshBefore = metrics.Reads;
        var fresh = GetAsync(shapes, uri);
        await metrics.WaitingAsync(1);
        await Assert.That(metrics.Reads - freshBefore).IsEqualTo(1);
        var later = DocumentApi.NewId();
        await PutAsync(api, later, "open");
        using var freshResponse = await fresh.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(freshResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ops(await JsonAsync(freshResponse))).IsEquivalentTo(Sorted(("upsert", later)));
    }

    [Test]
    public async Task A_controlled_live_deadline_returns_a_quiet_tail_and_refetches_after_database_replacement()
    {
        var clock = new ShapeClock { UnixSeconds = 20001 };
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        var server = api.Server;
        using var client = await api.ClientWithScopesAsync("documents:read", "documents:write", "shapes:read");
        using var metrics = new ShapeMetrics(server.Services);
        var (handle, head) = await SnapshotAsync(client, $"{api.Shape}?offset=-1");
        var uri = $"{api.Shape}?handle={handle}&offset={head}&live=true&cursor=1000";
        var quiet = GetAsync(client, uri);
        await metrics.WaitingAsync(1);
        clock.ExpireLiveWait();
        using var quietResponse = await quiet.WaitAsync(TimeSpan.FromSeconds(10));
        var body = await JsonAsync(quietResponse);
        await Assert.That(quietResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("changes").GetArrayLength()).IsEqualTo(0);
        await Assert.That(body.GetProperty("next_offset").GetString()).IsEqualTo(head);
        await Assert.That(body.GetProperty("up_to_date").GetBoolean()).IsTrue();
        await Assert.That(Header(quietResponse, "TB-Shape-Cursor")).IsEqualTo("1001");
        await metrics.WaitingExactlyAsync(0);

        var stale = GetAsync(client, uri);
        await metrics.WaitingAsync(1);
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var database = await api.TenantOfAsync();

        await api.DeleteDatabaseAsync();
        (await DocumentApi.SendAsync(client, HttpMethod.Put, api.DatabasePath())).Status.ShouldBe(HttpStatusCode.Created);
        (await DocumentApi.SendAsync(client, HttpMethod.Post, api.Collections,
            JsonContent.Create(new { name = DocumentApi.Collection }))).Status.ShouldBe(HttpStatusCode.Created);
        using var restart = await GetAsync(client, $"{api.Shape}?offset=-1");
        await Assert.That(stale.IsCompleted).IsFalse();
        // No old-store commit wakes this flight: the explicit deadline must reopen the engine store.
        clock.ExpireLiveWait();
        using var staleResponse = await stale.WaitAsync(TimeSpan.FromSeconds(10));
        // A database recreated under the same name is a new database (TS-475): the old one is gone, and a
        // restart reaches the new one under a new handle.
        await Assert.That(staleResponse.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await JsonAsync(staleResponse)).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unknown_database");
        await Assert.That(staleResponse.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
        await Assert.That(restart.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(Header(restart, "TB-Shape-Handle")).IsNotEqualTo(handle);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Conditional_shape_reads_recheck_deleted_keys_and_removed_scopes(bool removeScope)
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync(ApiKeyScopes.ShapesRead);
        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var uri = start.Headers.Location!.OriginalString;
        using var snapshot = await GetAsync(shapes, uri);
        using var validated = await GetAsync(shapes, uri, snapshot.Headers.ETag!.ToString());
        await Assert.That(snapshot.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(validated.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
        foreach (var response in new[] { start, snapshot, validated })
        {
            await Assert.That(CacheControl(response)).IsEqualTo(Revalidate);
            await Assert.That(response.Headers.CacheControl!.Private).IsTrue();
            await Assert.That(response.Headers.CacheControl.MustRevalidate).IsTrue();
            await Assert.That(response.Headers.CacheControl.MaxAge).IsEqualTo(TimeSpan.Zero);
            await Assert.That(response.Headers.CacheControl.SharedMaxAge).IsNull();
        }

        // There is no API-key revocation listener; modify this test server's stored credential,
        // then drive authentication and the conditional shape request over the real client listener.
        var secret = shapes.DefaultRequestHeaders.Authorization!.Parameter!;
        var keys = api.Server.Services.GetRequiredService<IApiKeyStore>();
        var key = await keys.FindAsync(secret, CancellationToken.None)
            ?? throw new InvalidOperationException("missing test API key");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            if (removeScope)
            {
                var changed = key with { Scopes = [ApiKeyScopes.DocumentsRead] };
                await transaction.PutAsync("api_keys", hash, JsonSerializer.SerializeToUtf8Bytes(changed, TerraBaseJsonContext.Default.ApiKey), CancellationToken.None);
            }
            else
            {
                await transaction.DeleteAsync("api_keys", hash, CancellationToken.None);
            }

            await transaction.CommitAsync(CancellationToken.None);
        }

        using var denied = await GetAsync(shapes, uri, snapshot.Headers.ETag.ToString());
        await Assert.That(denied.StatusCode).IsEqualTo(removeScope ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
        await Assert.That(denied.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
    }

    [Test]
    public async Task Conditional_shape_reads_validate_complete_entity_tag_lists()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        var uri = start.Headers.Location!.OriginalString;
        using var page = await GetAsync(shapes, uri);
        var tag = page.Headers.ETag!.ToString();
        using var weak = await GetAsync(shapes, uri, $"W/{tag}");
        using var wildcard = await GetAsync(shapes, uri, "*");
        using var quotedWildcard = await GetAsync(shapes, uri, "\"*\"");
        foreach (var invalid in new[] { "unquoted", $"{tag},unquoted" })
        {
            using var response = await GetAsync(shapes, uri, invalid);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That((await JsonAsync(response)).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_request");
            await Assert.That(response.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
        }

        await Assert.That(weak.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(wildcard.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(quotedWildcard.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_live_poll_reopens_the_database_when_an_old_writer_commits_after_recreation()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        using var metrics = new ShapeMetrics(api.Server.Services);
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var database = await api.TenantOfAsync();

        var store = await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("missing test database");
        await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await writer.PutAsync(DocumentApi.Collection, DocumentApi.NewId(), "{\"status\":\"old-writer\"}"u8.ToArray(), CancellationToken.None);
        var poll = GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}&live=true");
        await metrics.WaitingAsync(1);
        await api.DeleteDatabaseAsync();
        await api.SendAsync(HttpMethod.Put, api.DatabasePath());
        await api.SendAsync(HttpMethod.Post, api.Collections, new { name = DocumentApi.Collection });
        using var restart = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        // This writer owns a connection to the unlinked old file and wakes the old store's waiters.
        await writer.CommitAsync(CancellationToken.None);
        using var stale = await poll.WaitAsync(TimeSpan.FromSeconds(10));

        // The wake reopens the engine: the poll's database (TS-475: a database id) was deleted, so 404; a
        // restart reaches the new database of the same name under a new handle.
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await JsonAsync(stale)).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unknown_database");
        await Assert.That(stale.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
        await Assert.That(Header(restart, "TB-Shape-Handle")).IsNotEqualTo(handle);
    }

    [Test]
    public async Task A_rotated_handle_or_another_database_answers_409_must_refetch()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        await PutAsync(api, DocumentApi.NewId(), "open");
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");
        var collections = api.Collections;
        await api.SendAsync(HttpMethod.Delete, $"{collections}/{DocumentApi.Collection}");
        await api.SendAsync(HttpMethod.Post, collections, new { name = DocumentApi.Collection });

        using var stale = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}");
        var error = (await JsonAsync(stale)).GetProperty("error");
        using var restart = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        var current = Header(restart, "TB-Shape-Handle");
        using var beyond = await GetAsync(shapes, $"{api.Shape}?handle={current}&offset={long.Parse(head, System.Globalization.CultureInfo.InvariantCulture) + 1000}");

        await api.SendAsync(HttpMethod.Put, api.DatabasePath("other"));
        await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath("other")}/collections", new { name = DocumentApi.Collection });
        using var otherStart = await GetAsync(shapes, $"{api.DatabasePath("other")}/collections/{DocumentApi.Collection}/shape?offset=-1");
        using var crossed = await GetAsync(shapes, $"{api.DatabasePath("other")}/collections/{DocumentApi.Collection}/shape?handle={current}&offset={head}");

        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(stale.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("must_refetch");
        await Assert.That(error.GetProperty("handle").GetString()).IsEqualTo(current);
        await Assert.That(Header(stale, "TB-Shape-Handle")).IsEqualTo(current);
        await Assert.That(current).IsNotEqualTo(handle);
        await Assert.That(beyond.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(Header(otherStart, "TB-Shape-Handle")).IsNotEqualTo(current);
        await Assert.That(crossed.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task A_database_recreated_under_the_same_name_rotates_the_handle()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        await PutAsync(api, DocumentApi.NewId(), "open");
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");

        // No listener deletes a database by its data-API name, so delete it through the engine; recreate it over HTTP.
        var engine = api.Server.Services.GetRequiredService<TerraScale.TerraBase.Engine.IStorageEngine>();
        await api.DeleteDatabaseAsync();

        await api.SendAsync(HttpMethod.Put, api.DatabasePath());
        await api.SendAsync(HttpMethod.Post, api.Collections, new { name = DocumentApi.Collection });
        long replacementHead;
        do
        {
            replacementHead = await PutAsync(api, DocumentApi.NewId(), "open");
        }
        while (replacementHead < long.Parse(head, System.Globalization.CultureInfo.InvariantCulture));

        // The replacement has reached the old offset; the old lineage must still be refused.
        using var stale = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}");
        using var restart = await GetAsync(shapes, $"{api.Shape}?offset=-1");

        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(Header(restart, "TB-Shape-Handle")).IsNotEqualTo(handle);
        await Assert.That(Header(stale, "TB-Shape-Handle")).IsEqualTo(Header(restart, "TB-Shape-Handle"));
    }

    [Test]
    public async Task A_log_chunk_stops_before_the_commit_that_would_pass_1_MiB_of_serialized_body()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");
        // Characters a serializer would escape six-fold, sent and stored literally.
        var padding = new string('<', 450 * 1024);
        var ids = Ids(3);
        foreach (var id in ids)
        {
            await api.SendRawAsync(HttpMethod.Put, api.Document(id), $$"""{"status":"open","padding":"{{padding}}"}""", "application/json");
        }

        using var first = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}");
        var firstText = await first.Content.ReadAsStringAsync();
        var firstBytes = (await first.Content.ReadAsByteArrayAsync()).Length;
        var firstChunk = await JsonAsync(first);
        using var second = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={firstChunk.GetProperty("next_offset").GetString()}");
        var secondChunk = await JsonAsync(second);

        await Assert.That(firstBytes).IsLessThanOrEqualTo(1024 * 1024);
        await Assert.That(firstChunk.GetProperty("changes").GetArrayLength()).IsEqualTo(2);
        await Assert.That(firstChunk.GetProperty("up_to_date").GetBoolean()).IsFalse();
        await Assert.That(CacheControl(first)).IsEqualTo(Revalidate);
        await Assert.That(Ops(secondChunk)).IsEquivalentTo(Sorted(("upsert", ids[2])));
        await Assert.That(secondChunk.GetProperty("up_to_date").GetBoolean()).IsTrue();

        await api.PutAsync(ids[0], new { status = "rewritten" });
        await api.SendAsync(HttpMethod.Delete, api.Document(ids[1]));
        using var replay = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}");
        using var conditional = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}", first.Headers.ETag!.ToString());
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo(firstText);
        await Assert.That(replay.Headers.ETag!.Tag).IsEqualTo(first.Headers.ETag.Tag);
        await Assert.That(conditional.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
    }

    [Test]
    public async Task A_snapshot_page_body_stays_within_1_MiB_with_framing()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        // 1000 documents of about 1 KiB: their JSON alone fits 1 MiB, with the per-op framing it does not.
        var padding = new string('x', 1000);
        var ids = Ids(1000);
        await api.TransactAsync([.. ids.Select(id => new { op = "put", collection = DocumentApi.Collection, id, document = new { padding } })]);

        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        using var first = await GetAsync(shapes, start.Headers.Location!.OriginalString);
        var bytes = (await first.Content.ReadAsByteArrayAsync()).Length;
        var page = await JsonAsync(first);

        await Assert.That(bytes).IsLessThanOrEqualTo(1024 * 1024);
        await Assert.That(page.GetProperty("changes")[0].GetProperty("ops").GetArrayLength()).IsLessThan(1000);
        await Assert.That(page.GetProperty("next_offset").GetString()).StartsWith("snap.");
        // Every op here is ASCII and the same size: one more must exceed the serialized limit.
        var opBytes = page.GetProperty("changes")[0].GetProperty("ops")[0].GetRawText().Length;
        await Assert.That(bytes + opBytes + 1).IsGreaterThan(1024 * 1024);
    }

    [Test]
    public async Task Bodyless_errors_are_never_cached()
    {
        await using var api = await DocumentApi.StartAsync();

        using var unknown = await GetAsync(api.Client, "/v1/no-such-route");

        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknown.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
    }

    [Test]
    public async Task Shape_reads_need_the_shapes_read_scope_and_valid_parameters()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        using var anonymous = await api.ClientWithScopesAsync("documents:read");
        anonymous.DefaultRequestHeaders.Authorization = null;

        using var wrongScope = await GetAsync(api.Client, $"{api.Shape}?offset=-1");
        using var noKey = await GetAsync(anonymous, $"{api.Shape}?offset=-1");
        using var noOffset = await GetAsync(shapes, api.Shape);
        using var noHandle = await GetAsync(shapes, $"{api.Shape}?offset=0");
        using var liveSnapshot = await GetAsync(shapes, $"{api.Shape}?handle=h1-x&offset=snap.0.0&live=true");
        using var badWhere = await GetAsync(shapes, $"{api.Shape}?where=%7B&offset=-1");
        using var unknownOp = await GetAsync(shapes, $"{api.Shape}?where={Uri.EscapeDataString("""{"near":["/a",1]}""")}&offset=-1");
        using var unknownCollection = await GetAsync(shapes, $"{api.Collections}/missing/shape?offset=-1");

        await Assert.That(wrongScope.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(noKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(noOffset.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(noOffset.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
        await Assert.That(noHandle.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(liveSnapshot.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(badWhere.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unknownOp.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unknownCollection.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_transaction_cannot_write_a_commit_larger_than_a_log_chunk()
    {
        await using var api = await DocumentApi.StartAsync();
        var ids = Ids(8);
        var padding = new string('x', 950 * 1024);
        foreach (var id in ids)
        {
            await api.PutAsync(id, new { padding, n = 0 });
        }

        // Eight small patches would commit about 7.4 MiB of documents.
        var patched = await api.TransactAsync([.. ids.Select(id => new { op = "patch", collection = DocumentApi.Collection, id, merge = new { n = 1 } })]);
        var single = await api.TransactAsync([.. ids.Take(7).Select(id => new { op = "patch", collection = DocumentApi.Collection, id, merge = new { n = 1 } })]);

        await Assert.That(patched.Status).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(patched.Code).IsEqualTo("payload_too_large");
        await Assert.That(patched.Op).IsEqualTo(7);
        await Assert.That(single.Status).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_max_size_document_is_an_allowed_singleton_above_1_MiB_with_framing()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");
        var padding = new string('<', (1024 * 1024) - "{\"padding\":\"\"}".Length);
        var json = $$"""{"padding":"{{padding}}"}""";
        var written = await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), json, "application/json");
        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        using var snapshot = await GetAsync(shapes, start.Headers.Location!.OriginalString);
        using var log = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}");
        foreach (var response in new[] { snapshot, log })
        {
            var bytes = (await response.Content.ReadAsByteArrayAsync()).Length;
            var body = await JsonAsync(response);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(bytes).IsGreaterThan(1024 * 1024);
            await Assert.That(bytes).IsLessThanOrEqualTo(8 * 1024 * 1024);
            await Assert.That(Ops(body).Count).IsEqualTo(1);
            await Assert.That(body.GetProperty("changes")[0].GetProperty("ops")[0].GetProperty("document").GetRawText()).IsEqualTo(json);
        }

        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task A_log_emits_each_document_once_per_commit_with_its_final_state_and_version()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");
        var id = DocumentApi.NewId();
        // Client transactions reject duplicate targets; internal engine callers can write them.
        // Arrange that commit on the running server, then verify its log and snapshot over HTTP.
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var database = await api.TenantOfAsync();

        var store = await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("missing test database");
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await transaction.PutAsync(DocumentApi.Collection, id, "{\"status\":\"first\"}"u8.ToArray(), CancellationToken.None);
            await transaction.DeleteAsync(DocumentApi.Collection, id, CancellationToken.None);
            await transaction.PutAsync(DocumentApi.Collection, id, "{\"status\":\"last\"}"u8.ToArray(), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        using var response = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}");
        var body = await JsonAsync(response);
        var ops = body.GetProperty("changes")[0].GetProperty("ops");
        var committedVersion = (await api.GetAsync(id)).Version;
        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        await api.PutAsync(id, new { status = "later" });
        using var snapshot = await GetAsync(shapes, start.Headers.Location!.OriginalString);
        var snapshotBody = await JsonAsync(snapshot);

        // Every write of one commit shares the commit's version, so only the final state may carry it: an
        // intermediate body under the same version would validate the final one (If-None-Match, If-Match).
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("changes").GetArrayLength()).IsEqualTo(1);
        await Assert.That(string.Join(",", ops.EnumerateArray().Select(op => op.GetProperty("op").GetString()))).IsEqualTo("upsert");
        await Assert.That(ops[0].GetProperty("document").GetProperty("status").GetString()).IsEqualTo("last");
        await Assert.That(ops[0].GetProperty("version").GetString()).IsEqualTo(committedVersion);
        await Assert.That(Ops(snapshotBody)).IsEquivalentTo(Sorted(("upsert", id)));
        await Assert.That(snapshotBody.GetProperty("changes")[0].GetProperty("ops")[0].GetProperty("document").GetProperty("status").GetString()).IsEqualTo("last");
    }

    private object Document(string status) => new { status, title = _faker.Commerce.ProductName(), price = _faker.Random.Int(1, 500) };

    [Test]
    public async Task An_uncommitted_offset_stays_invisible_until_its_commit_wakes_the_poll()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync(ApiKeyScopes.ShapesRead);
        using var metrics = new ShapeMetrics(api.Server.Services);
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var database = await api.TenantOfAsync();

        var store = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
        var id = DocumentApi.NewId();
        await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        // The writer has allocated offset head + 1 and holds it uncommitted.
        var pending = await writer.PutAsync(DocumentApi.Collection, id, Encoding.UTF8.GetBytes("""{"status":"open"}"""), CancellationToken.None);
        using var meanwhile = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}");
        var before = await JsonAsync(meanwhile);
        var poll = GetAsync(shapes, $"{api.Shape}?handle={handle}&offset={head}&live=true");
        await metrics.WaitingAsync(1);
        await Assert.That(poll.IsCompleted).IsFalse();

        await writer.CommitAsync(CancellationToken.None);
        using var woken = await poll.WaitAsync(TimeSpan.FromSeconds(10));
        var after = await JsonAsync(woken);

        await Assert.That(pending).IsEqualTo(long.Parse(head, System.Globalization.CultureInfo.InvariantCulture) + 1);
        await Assert.That(before.GetProperty("next_offset").GetString()).IsEqualTo(head);
        await Assert.That(before.GetProperty("changes").GetArrayLength()).IsEqualTo(0);
        await Assert.That(after.GetProperty("next_offset").GetString()).IsEqualTo($"{pending}");
        await Assert.That(Ops(after)).IsEquivalentTo(Sorted(("upsert", id)));
    }

    [Test]
    public async Task Deleting_or_dropping_and_recreating_never_reuses_a_version()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var first = await api.PutAsync(id, new { n = 1 });
        var tombstone = await api.SendAsync(HttpMethod.Delete, api.Document(id));
        var recreated = await api.PutAsync(id, new { n = 1 });
        var collections = api.Collections;
        (await api.SendAsync(HttpMethod.Delete, $"{collections}/{DocumentApi.Collection}")).Status.ShouldBe(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Post, collections, new { name = DocumentApi.Collection })).Status.ShouldBe(HttpStatusCode.Created);
        var afterDrop = await api.PutAsync(id, new { n = 1 });
        var conditional = await api.GetAsync(id, DocumentApi.IfNoneMatch(first.Version));

        // Identical bodies, yet every lifetime of the document has its own version and ETag.
        string[] versions = [first.Version, tombstone.Version, recreated.Version, afterDrop.Version];
        await Assert.That(versions.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(4);
        await Assert.That(conditional.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(conditional.ETag).IsEqualTo(afterDrop.Version);
    }

    [Test]
    public async Task A_snapshot_page_boundary_at_a_legacy_id_resumes()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync(ApiKeyScopes.ShapesRead);
        // Schema-1 databases keep non-UUID ids; no listener writes one, so store them through the engine.
        string[] ids = ["Customer-1@Legacy+Tag?#.x", "Customer-2/../é"];
        var padding = new string('x', 700 * 1024);
        var store = await StoreAsync(api);
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            foreach (var id in ids)
            {
                await writer.PutAsync(DocumentApi.Collection, id, Encoding.UTF8.GetBytes($$"""{"padding":"{{padding}}"}"""), CancellationToken.None);
            }

            await writer.CommitAsync(CancellationToken.None);
        }

        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        using var first = await GetAsync(shapes, start.Headers.Location!.OriginalString);
        var firstPage = await JsonAsync(first);
        var next = firstPage.GetProperty("next_offset").GetString()!;
        using var second = await GetAsync(shapes, $"{api.Shape}?handle={Header(start, "TB-Shape-Handle")}&offset={next}");
        var secondPage = await JsonAsync(second);

        // Each 700 KiB document fills a page; the second page resumes after the legacy id.
        await Assert.That(Ops(firstPage)).IsEquivalentTo([$"upsert {ids[0]}"]);
        await Assert.That(next).IsEqualTo($"snap.{firstPage.GetProperty("changes")[0].GetProperty("offset").GetString()}.{Base64Url(ids[0])}");
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ops(secondPage)).IsEquivalentTo([$"upsert {ids[1]}"]);
    }

    [Test]
    public async Task A_snapshot_of_a_document_with_a_long_history_reads_only_its_last_write()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync(ApiKeyScopes.ShapesRead);
        var id = DocumentApi.NewId();
        var store = await StoreAsync(api);
        // 50000 retained writes of one document: a lookup that walks its history would exhaust the query budget.
        for (var batch = 0; batch < 50; batch++)
        {
            await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            for (var n = 0; n < 1000; n++)
            {
                await writer.PutAsync(DocumentApi.Collection, id, Encoding.UTF8.GetBytes($$"""{"n":{{(batch * 1000) + n}}}"""), CancellationToken.None);
            }

            await writer.CommitAsync(CancellationToken.None);
        }

        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        using var snapshot = await GetAsync(shapes, start.Headers.Location!.OriginalString);
        var page = await JsonAsync(snapshot);

        await Assert.That(snapshot.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page.GetProperty("changes")[0].GetProperty("ops")[0].GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(49_999);
    }

    [Test]
    public async Task A_filter_with_a_lone_surrogate_escape_is_an_invalid_query()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync(ApiKeyScopes.ShapesRead);

        using var response = await GetAsync(shapes, $"{api.Shape}?where={Uri.EscapeDataString("""{"eq":["/status","\uD800"]}""")}&offset=-1");
        var error = (await JsonAsync(response)).GetProperty("error");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("invalid_query");
    }

    [Test]
    public async Task The_longest_accepted_filter_still_resumes_after_a_1024_byte_legacy_id()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync(ApiKeyScopes.ShapesRead);
        static string Where(int length)
        {
            return Uri.EscapeDataString($$"""{"ne":["/pad","{{new string('p', length)}}"]}""");
        }

        // The longest filter the shape accepts: one more character answers 414.
        int low = 0, high = 8192;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            using var probe = await GetAsync(shapes, $"{api.Shape}?where={Where(middle)}&offset=-1");
            if (probe.StatusCode == HttpStatusCode.TemporaryRedirect)
            {
                low = middle;
            }
            else
            {
                await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.RequestUriTooLong);
                high = middle - 1;
            }
        }

        // Ids of the longest length storage admits; each 700 KiB document fills a page.
        string[] ids = [new string('a', 1024), new string('b', 1024)];
        var padding = new string('x', 700 * 1024);
        var store = await StoreAsync(api);
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            foreach (var id in ids)
            {
                await writer.PutAsync(DocumentApi.Collection, id, Encoding.UTF8.GetBytes($$"""{"padding":"{{padding}}"}"""), CancellationToken.None);
            }

            await writer.CommitAsync(CancellationToken.None);
        }

        using var start = await GetAsync(shapes, $"{api.Shape}?where={Where(low)}&offset=-1");
        var location = start.Headers.Location!.OriginalString;
        using var first = await GetAsync(shapes, location);
        var next = (await JsonAsync(first)).GetProperty("next_offset").GetString()!;
        var resume = location[..location.LastIndexOf("offset=", StringComparison.Ordinal)] + "offset=" + next;
        using var second = await GetAsync(shapes, resume);

        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(next).EndsWith(Base64Url(ids[0]));
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ops(await JsonAsync(second))).IsEquivalentTo([$"upsert {ids[1]}"]);
    }

    [Test]
    public async Task A_snapshot_replay_skips_a_later_document_with_deep_history_in_one_seek()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync(ApiKeyScopes.ShapesRead);
        await PutAsync(api, DocumentApi.NewId(), "open");
        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        using var first = await GetAsync(shapes, start.Headers.Location!.OriginalString);
        var firstBody = await first.Content.ReadAsStringAsync();

        // A document first written after S, ordering before the kept one, with 50000 revisions: walking them
        // would exhaust the query budget; the step skips the id by its first entry.
        var later = "00000000-0000-7000-8000-000000000000";
        var store = await StoreAsync(api);
        for (var batch = 0; batch < 50; batch++)
        {
            await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            for (var n = 0; n < 1000; n++)
            {
                await writer.PutAsync(DocumentApi.Collection, later, Encoding.UTF8.GetBytes($$"""{"n":{{(batch * 1000) + n}}}"""), CancellationToken.None);
            }

            await writer.CommitAsync(CancellationToken.None);
        }

        using var replay = await GetAsync(shapes, start.Headers.Location!.OriginalString);

        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo(firstBody);
    }

    [Test]
    public async Task Snapshot_pages_read_the_state_at_their_offset_whatever_happens_between_pages()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync(ApiKeyScopes.ShapesRead);
        // Three pages of two documents each: 700 KiB bodies fill a page with one, so pages are one document.
        var padding = new string('x', 700 * 1024);
        string[] ids = ["00000000-0000-7000-8000-000000000010", "00000000-0000-7000-8000-000000000020", "00000000-0000-7000-8000-000000000030"];
        foreach (var id in ids)
        {
            await api.SendRawAsync(HttpMethod.Put, api.Document(id), $$"""{"padding":"{{padding}}"}""", "application/json");
        }

        using var start = await GetAsync(shapes, $"{api.Shape}?offset=-1");
        var location = start.Headers.Location!.OriginalString;
        var prefix = location[..location.LastIndexOf("offset=", StringComparison.Ordinal)];
        async Task<List<string>> PagesAsync()
        {
            var bodies = new List<string>();
            var next = location[(prefix.Length + "offset=".Length)..];
            while (next.StartsWith("snap.", StringComparison.Ordinal))
            {
                using var page = await GetAsync(shapes, prefix + "offset=" + next);
                var text = await page.Content.ReadAsStringAsync();
                bodies.Add(text);
                next = JsonElement.Parse(text).GetProperty("next_offset").GetString()!;
            }

            return bodies;
        }

        var before = await PagesAsync();
        // (a) ids first written after the offset, ordering before, between and after the snapshot's ids;
        // (b) the snapshot's own documents rewritten and deleted.
        foreach (var later in new[] { "00000000-0000-7000-8000-000000000005", "00000000-0000-7000-8000-000000000015", "00000000-0000-7000-8000-000000000035" })
        {
            await api.SendRawAsync(HttpMethod.Put, api.Document(later), $$"""{"padding":"{{padding}}"}""", "application/json");
        }

        await api.PutAsync(ids[0], new { padding = "rewritten" });
        await api.SendAsync(HttpMethod.Delete, api.Document(ids[1]));
        var after = await PagesAsync();

        await Assert.That(before.Count).IsEqualTo(3);
        await Assert.That(after).IsEquivalentTo(before, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(string.Join(" ", before.SelectMany(page => Ops(JsonElement.Parse(page))))).IsEqualTo(string.Join(" ", ids.Select(id => $"upsert {id}")));
    }

    [Test]
    public async Task A_snapshot_offset_past_the_head_is_refused_with_must_refetch()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync(ApiKeyScopes.ShapesRead);
        await PutAsync(api, DocumentApi.NewId(), "open");
        var (handle, head) = await SnapshotAsync(shapes, $"{api.Shape}?offset=-1");

        // (c) checked against the head of the same read transaction before any history is read.
        using var future = await GetAsync(shapes, $"{api.Shape}?handle={handle}&offset=snap.{long.Parse(head, System.Globalization.CultureInfo.InvariantCulture) + 1}.0");
        var error = (await JsonAsync(future)).GetProperty("error");

        await Assert.That(future.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("must_refetch");
        await Assert.That(error.GetProperty("handle").GetString()).IsEqualTo(handle);
        await Assert.That(CacheControl(future)).IsEqualTo("no-store");
    }

    private static async Task<IDatabaseStore> StoreAsync(DocumentApi api)
    {
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        return (await engine.OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
    }

    private static string Base64Url(string id) => System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(id));

    private async Task<long> PutAsync(DocumentApi api, string id, string status)
    {
        var response = await api.PutAsync(id, Document(status));
        return long.Parse(response.Json.GetProperty("offset").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Follows -1 through every snapshot page; returns the handle and the log position after the snapshot.
    private static async Task<(string Handle, string Head)> SnapshotAsync(HttpClient client, string start)
    {
        using var redirect = await GetAsync(client, start);
        var handle = Header(redirect, "TB-Shape-Handle");
        var next = redirect.Headers.Location!.OriginalString;
        while (true)
        {
            using var page = await GetAsync(client, next);
            var offset = (await JsonAsync(page)).GetProperty("next_offset").GetString()!;
            if (!offset.StartsWith("snap.", StringComparison.Ordinal))
            {
                return (handle, offset);
            }

            next = next[..next.IndexOf("offset=", StringComparison.Ordinal)] + "offset=" + offset;
        }
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string uri, string? ifNoneMatch = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.Relative));
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        var response = await client.SendAsync(request, cancellationToken);
        await response.Content.LoadIntoBufferAsync(cancellationToken);
        return response;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static string Header(HttpResponseMessage response, string name) => response.Headers.GetValues(name).Single();

    private static string[] Ids(int count) => [.. Enumerable.Range(0, count).Select(_ => DocumentApi.NewId())];

    private static (string, string, string) Ids3(string[] ids) => (ids[0], ids[1], ids[2]);

    private static void Apply(Dictionary<string, JsonElement> state, JsonElement body)
    {
        foreach (var op in body.GetProperty("changes").EnumerateArray().SelectMany(commit => commit.GetProperty("ops").EnumerateArray()))
        {
            var id = op.GetProperty("id").GetString()!;
            if (op.GetProperty("op").GetString() == "delete")
            {
                state.Remove(id);
            }
            else
            {
                state[id] = op.GetProperty("document").Clone();
            }
        }
    }

    // "op id" for every op of a body's changes, or of one ops array.
    private static List<string> Ops(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? [.. element.EnumerateArray().Select(op => $"{op.GetProperty("op").GetString()} {op.GetProperty("id").GetString()}")]
            : [.. element.GetProperty("changes").EnumerateArray().SelectMany(commit => Ops(commit.GetProperty("ops")))];

    private static List<string> Sorted(params (string Op, string Id)[] ops) => [.. ops.OrderBy(op => op.Id, StringComparer.Ordinal).Select(op => $"{op.Op} {op.Id}")];

    private sealed class ShapeClock : TimeProvider
    {
        public long UnixSeconds
        {
            get => Interlocked.Read(ref field);
            set => Interlocked.Exchange(ref field, value);
        }

        private ITimer? LiveDeadline { get; set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(UnixSeconds);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Park live deadlines; tests that explicitly fire one have a single active flight.
            // Unrelated service timers retain their ordinary behavior.
            if (dueTime == TimeSpan.FromSeconds(20) && period == Timeout.InfiniteTimeSpan)
            {
                return LiveDeadline = base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
            }

            return base.CreateTimer(callback, state, dueTime, period);
        }

        public void ExpireLiveWait()
        {
            var deadline = LiveDeadline ?? throw new InvalidOperationException("no live wait has started");
            if (!deadline.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan))
            {
                throw new InvalidOperationException("the live deadline could not be fired");
            }
        }
    }

    /// <summary>Observes this server's own shape instruments: its meter factory scopes them, so parallel servers never mix in.</summary>
    private sealed class ShapeMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<(long Count, bool Exact, bool Flights, TaskCompletionSource Reached)> _waits = [];
        private long _reads;
        private long _waiting;
        private long _flights;

        public ShapeMetrics(IServiceProvider services)
        {
            var scope = services.GetRequiredService<IMeterFactory>();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Scope == scope && instrument.Name is "terrabase.shape.reads" or "terrabase.shape.requests.waiting" or "terrabase.shape.flights.active")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                lock (_gate)
                {
                    if (instrument.Name == "terrabase.shape.reads")
                    {
                        _reads += value;
                        return;
                    }

                    if (instrument.Name == "terrabase.shape.flights.active")
                    {
                        _flights += value;
                    }
                    else
                    {
                        _waiting += value;
                    }

                    foreach (var (expected, exact, flights, reached) in _waits)
                    {
                        var count = flights ? _flights : _waiting;
                        if (exact ? count == expected : count >= expected)
                        {
                            reached.TrySetResult();
                        }
                    }
                }
            });
            _listener.Start();
        }

        public long Reads
        {
            get
            {
                lock (_gate)
                {
                    return _reads;
                }
            }
        }

        /// <summary>Completes once at least <paramref name="count"/> shape requests wait on a shared read.</summary>
        public Task WaitingAsync(int count) => WaitAsync(count, exact: false);

        public Task WaitingExactlyAsync(int count) => WaitAsync(count, exact: true);

        public Task FlightsAsync(int count) => WaitAsync(count, exact: true, flights: true);

        private Task WaitAsync(int count, bool exact, bool flights = false)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                var current = flights ? _flights : _waiting;
                if (exact ? current == count : current >= count)
                {
                    return Task.CompletedTask;
                }

                _waits.Add((count, exact, flights, reached));
            }

            return reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public void Dispose() => _listener.Dispose();
    }

    private static string CacheControl(HttpResponseMessage response) =>
        response.Headers.NonValidated.TryGetValues("Cache-Control", out var values) ? string.Join(", ", values) : "";
}
