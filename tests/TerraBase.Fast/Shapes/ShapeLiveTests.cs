using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Errors;
using TerraBase.Features.Qos;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;
using TerraBase.Fast.Documents;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Shapes;

/// <summary>
/// Live tail and request collapsing (FR-14, free-reads.md 3.6, shape-reads.md 4) on a real server: documents are written
/// over HTTP and commit through SQLite, which wakes live waiters. The seal and the shape read are fakes injected through
/// DI, and the clock is a fake. Waits are released by barriers, by a real commit, or by advancing time, never by sleeps.
/// </summary>
public sealed class ShapeLiveTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    // A client head no store ever reports: the probe finds the shape changed, so the flight reads at once.
    private const long StaleHead = -1;

    [Test]
    public async Task Parked_live_waiters_wake_on_one_real_commit_with_exactly_one_seal_and_one_read()
    {
        var sealer = new FakeSealer();
        await using var api = await StartAsync(new FakeTimeProvider(Start), sealer);
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        var head = await HeadAsync(api, database);
        var shape = new FakeShape(Sealer(api), head);

        var waiters = Enumerable.Range(0, 8).Select(_ => ReadFlightAsync(flights, LiveRequest(database), shape)).ToArray();
        await shape.ProbeEntered.WaitAsync(Deadline);
        await Assert.That(shape.Reads).IsEqualTo(0).Because("the flight is parked at the head, not reading");

        var written = await api.PutAsync(DocumentApi.NewId(), new { text = new Faker().Lorem.Sentence() });
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
        var answers = (await Task.WhenAll(waiters).WaitAsync(Deadline)).Select(Flight).ToArray();

        try
        {
            await Assert.That(sealer.Calls).IsEqualTo(1).Because("the commit wake takes one shared seal");
            await Assert.That(shape.Reads).IsEqualTo(1).Because("exactly one read per flight");
            await Assert.That(answers.All(answer => ReferenceEquals(answer.Value, answers[0].Value))).IsTrue()
                .Because("every requester gets the same answer instance");
            await Assert.That(answers[0].Value.Head).IsEqualTo(await HeadAsync(api, database));
            await Assert.That(answers[0].Value.Head).IsGreaterThan(head);
            await Assert.That(answers[0].Value.UpToDate).IsTrue().Because("the fresh page delivers through the sealed head");
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    [Test]
    public async Task Two_shapes_parked_at_the_head_wake_on_one_commit_and_share_one_seal()
    {
        var sealer = new FakeSealer();
        await using var api = await StartAsync(new FakeTimeProvider(Start), sealer);
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        var head = await HeadAsync(api, database);
        using var hold = new Latch();
        var first = new FakeShape(Sealer(api), head, hold);
        var second = new FakeShape(Sealer(api), head);

        var waitingFirst = ReadFlightAsync(flights, LiveRequest(database, "h-first"), first);
        await first.ProbeEntered.WaitAsync(Deadline);
        var waitingSecond = ReadFlightAsync(flights, LiveRequest(database, "h-second"), second);
        await second.ProbeEntered.WaitAsync(Deadline);

        var written = await api.PutAsync(DocumentApi.NewId(), new { text = new Faker().Lorem.Sentence() });
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);

        // The first shape holds its read until the second has read too, so the seal's watermark still covers the head.
        await second.ReadEntered.WaitAsync(Deadline);
        hold.Open();
        var answers = (await Task.WhenAll(waitingFirst, waitingSecond).WaitAsync(Deadline)).Select(Flight).ToArray();

        try
        {
            await Assert.That(sealer.Calls).IsEqualTo(1).Because("one seal covers every shape of the database at the commit");
            await Assert.That(first.Reads).IsEqualTo(1);
            await Assert.That(second.Reads).IsEqualTo(1);
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    [Test]
    public async Task A_late_wake_seals_commits_newer_than_the_previous_shared_seal()
    {
        var sealer = new FakeSealer();
        await using var api = await StartAsync(new FakeTimeProvider(Start), sealer);
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        var head = await HeadAsync(api, database);
        using var holdRead = new Latch();
        using var holdProbe = new Latch();
        var first = new FakeShape(sealer, head, holdRead);
        var late = new FakeShape(sealer, head);
        var firstWaiting = ReadFlightAsync(flights, LiveRequest(database, "h-first"), first);
        await first.ProbeEntered.WaitAsync(Deadline);
        var lateWaiting = flights.ReadAsync(LiveRequest(database, "h-late"), async (store, token) =>
        {
            var atHead = await late.Probe(store, token);
            await holdProbe.Opened.WaitAsync(token);
            return atHead;
        }, late.Read, CancellationToken.None).AsTask();
        await late.ProbeEntered.WaitAsync(Deadline);

        try
        {
            var written = await api.PutAsync(DocumentApi.NewId(), new { text = new Faker().Lorem.Sentence() });
            await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
            await first.ReadEntered.WaitAsync(Deadline);
            await Assert.That(sealer.Calls).IsEqualTo(1);
            written = await api.PutAsync(DocumentApi.NewId(), new { text = new Faker().Lorem.Sentence() });
            await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);

            // The late flight captured the same old sequence, but its wake must seal the head that exists now.
            holdProbe.Open();
            var lateAnswer = Flight(await lateWaiting.WaitAsync(Deadline));
            await Assert.That(sealer.Calls).IsEqualTo(2);
            await Assert.That(lateAnswer.Value.Head).IsEqualTo(await HeadAsync(api, database));
            await Assert.That(lateAnswer.Value.UpToDate).IsTrue();
        }
        finally
        {
            holdProbe.Open();
            holdRead.Open();
            try
            {
                _ = await Task.WhenAll(firstWaiting, lateWaiting).WaitAsync(Deadline);
            }
            finally
            {
                foreach (var waiting in new[] { firstWaiting, lateWaiting })
                {
                    if (waiting.IsCompletedSuccessfully && await waiting is ShapeFlight<ShapePage> answer)
                    {
                        answer.Dispose();
                    }
                }
            }
        }
    }

    [Test]
    public async Task Non_live_requests_read_once_without_probing_or_sealing_and_ignore_the_cursor()
    {
        var sealer = new FakeSealer();
        await using var api = await StartAsync(new FakeTimeProvider(Start), sealer);
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        using var hold = new Latch();
        var shape = new FakeShape(Sealer(api), StaleHead, hold);

        var first = ReadFlightAsync(flights, new ShapeFlightRequest(database, "h-plain", "0", false, 1), shape);
        await shape.ReadEntered.WaitAsync(Deadline);
        var second = ReadFlightAsync(flights, new ShapeFlightRequest(database, "h-plain", "0", false, 2), shape);
        hold.Open();
        var answers = (await Task.WhenAll(first, second).WaitAsync(Deadline)).Select(Flight).ToArray();

        try
        {
            await Assert.That(sealer.Calls).IsEqualTo(0);
            await Assert.That(shape.Probes).IsEqualTo(0);
            await Assert.That(shape.Reads).IsEqualTo(1).Because("a cursor is part of the key only for live requests");
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    [Test]
    public async Task The_live_cursor_is_part_of_the_key()
    {
        var sealer = new FakeSealer();
        await using var api = await StartAsync(new FakeTimeProvider(Start), sealer);
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        using var hold = new Latch();
        var moved = new FakeShape(Sealer(api), StaleHead, hold);
        var same = new FakeShape(Sealer(api), StaleHead, hold);

        // Both pairs are launched while their reads are held, so each join is decided before any read finishes.
        var movedFirst = ReadFlightAsync(flights, LiveRequest(database, "h-moved", 1), moved);
        var movedSecond = ReadFlightAsync(flights, LiveRequest(database, "h-moved", 2), moved);
        var sameFirst = ReadFlightAsync(flights, LiveRequest(database, "h-same", 3), same);
        var sameSecond = ReadFlightAsync(flights, LiveRequest(database, "h-same", 3), same);
        hold.Open();
        var answers = (await Task.WhenAll(movedFirst, movedSecond, sameFirst, sameSecond).WaitAsync(Deadline)).Select(Flight).ToArray();

        try
        {
            await Assert.That(moved.Reads).IsEqualTo(2).Because("different cursors never share a flight");
            await Assert.That(same.Reads).IsEqualTo(1).Because("the same cursor shares one flight");
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    [Test]
    public async Task A_parked_waiter_times_out_after_twenty_seconds_with_an_up_to_date_page_and_no_seal()
    {
        var time = new FakeTimeProvider(Start);
        var sealer = new FakeSealer();
        await using var api = await StartAsync(time, sealer);
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        var head = await HeadAsync(api, database);
        var shape = new FakeShape(Sealer(api), head);

        var waiting = ReadFlightAsync(flights, LiveRequest(database), shape);
        await shape.ProbeEntered.WaitAsync(Deadline);

        time.Advance(TimeSpan.FromSeconds(19));
        await Assert.That(waiting.IsCompleted).IsFalse().Because("the wait lasts until the 20 second deadline");
        time.Advance(TimeSpan.FromSeconds(1));
        var answer = Flight(await waiting.WaitAsync(Deadline));
        var page = answer.Value;
        answer.Dispose();

        await Assert.That(page.UpToDate).IsTrue().Because("the deadline answers the sealed head the client already has");
        await Assert.That(page.Head).IsEqualTo(head);
        await Assert.That(sealer.Calls).IsEqualTo(0).Because("the deadline answers with a fresh read and no seal");
        await Assert.That(shape.Probes).IsEqualTo(1);
        await Assert.That(shape.Reads).IsEqualTo(1);
    }

    [Test]
    public async Task Next_cursor_is_the_twenty_second_bucket_and_always_moves_past_the_input()
    {
        var time = new FakeTimeProvider(Start);
        await using var api = await StartAsync(time, new FakeSealer());
        var flights = Flights(api);
        var bucket = Start.ToUnixTimeSeconds() / ShapeFlights<ShapePage>.LiveBucketSeconds;

        await Assert.That(flights.NextCursor(null)).IsEqualTo(bucket);
        await Assert.That(flights.NextCursor(bucket)).IsEqualTo(bucket + 1).Because("the input bucket is never reused");

        time.Advance(TimeSpan.FromSeconds(20));
        await Assert.That(flights.NextCursor(bucket + 1)).IsEqualTo(bucket + 2);

        time.Advance(TimeSpan.FromSeconds(19));
        await Assert.That(flights.NextCursor(null)).IsEqualTo(bucket + 1).Because("39 seconds is still the second bucket");

        await Assert.That(flights.NextCursor(5_000_000_000)).IsEqualTo(5_000_000_001).Because("a future input wins over the clock");
    }

    [Test]
    public async Task Thirty_three_distinct_running_flights_are_refused_as_shape_busy()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start), new FakeSealer());
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        using var hold = new Latch();
        var shape = new FakeShape(Sealer(api), StaleHead, hold);

        var running = Enumerable.Range(0, ShapeFlights<ShapePage>.MaxFlights)
            .Select(n => ReadFlightAsync(flights, LiveRequest(database, $"h-{n}"), shape))
            .ToArray();
        var refused = await ReadFlightAsync(flights, LiveRequest(database, "h-overflow"), shape).WaitAsync(Deadline);
        hold.Open();
        foreach (var answer in await Task.WhenAll(running).WaitAsync(Deadline))
        {
            Flight(answer).Dispose();
        }

        await Assert.That(refused is QuotaExceeded { Code: "shape_busy" }).IsTrue();
    }

    [Test]
    public async Task Two_hundred_fifty_seventh_requester_is_refused_as_shape_busy()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start), new FakeSealer());
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        using var hold = new Latch();
        var shape = new FakeShape(Sealer(api), StaleHead, hold);

        var joined = Enumerable.Range(0, ShapeFlights<ShapePage>.MaxRequesters)
            .Select(_ => ReadFlightAsync(flights, LiveRequest(database), shape))
            .ToArray();
        var refused = await ReadFlightAsync(flights, LiveRequest(database), shape).WaitAsync(Deadline);
        hold.Open();
        foreach (var answer in await Task.WhenAll(joined).WaitAsync(Deadline))
        {
            Flight(answer).Dispose();
        }

        await Assert.That(refused is QuotaExceeded { Code: "shape_busy" }).IsTrue();
        await Assert.That(shape.Reads).IsEqualTo(1).Because("all 256 requesters share one read");
    }

    [Test]
    public async Task Flight_and_requester_memory_is_reserved_while_held_and_released_on_dispose()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start), new FakeSealer());
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        var before = await api.ReservedBytesAsync();
        using var hold = new Latch();
        const long resultBytes = 1 << 20;
        var shape = new FakeShape(Sealer(api), StaleHead, hold) { ResultBytes = resultBytes };

        var running = ReadFlightAsync(flights, LiveRequest(database), shape);
        await shape.ReadEntered.WaitAsync(Deadline);
        var held = await api.ReservedBytesAsync();
        const double expected = ShapeFlights<ShapePage>.FlightBytes + ShapeFlights<ShapePage>.RequesterBytes + resultBytes;
        await Assert.That(held - before).IsGreaterThanOrEqualTo(expected)
            .Because("the flight, its requester and the reader's result bytes are all reserved while held");

        hold.Open();
        var answer = Flight(await running.WaitAsync(Deadline));
        answer.Dispose();

        await Assert.That(await api.ReservedBytesAsync()).IsEqualTo(before).Because("disposing the answer releases every byte");
    }

    [Test]
    public async Task Memory_refusal_returns_shape_busy_and_admission_recovers_after_release()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start), new FakeSealer());
        var database = await api.TenantOfAsync();
        var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
        var shape = new FakeShape(Sealer(api), StaleHead);
        List<IMemoryReservation> held = [];
        try
        {
            foreach (var bytes in new[] { 1024L * 1024, ShapeFlights<ShapePage>.RequesterBytes })
            {
                while (budget.TryReserve(MemoryLane.Foreground, bytes, database) is { } reservation)
                {
                    held.Add(reservation);
                }
            }

            var refused = await ReadFlightAsync(Flights(api), LiveRequest(database), shape).WaitAsync(Deadline);
            await Assert.That(refused is QuotaExceeded { Code: "shape_busy" }).IsTrue();
            await Assert.That(shape.Reads).IsEqualTo(0);
        }
        finally
        {
            foreach (var reservation in held)
            {
                reservation.Dispose();
            }
        }

        using var recovered = Flight(await ReadFlightAsync(Flights(api), LiveRequest(database), shape));
        await Assert.That(shape.Reads).IsEqualTo(1);
    }

    [Test]
    public async Task Cancelling_one_requester_leaves_the_others_waiting_on_the_same_flight()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start), new FakeSealer());
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        using var hold = new Latch();
        var shape = new FakeShape(Sealer(api), StaleHead, hold);
        using var leaving = new CancellationTokenSource();

        var stays = ReadFlightAsync(flights, LiveRequest(database), shape);
        var leaves = ReadFlightAsync(flights, LiveRequest(database), shape, leaving.Token);
        await shape.ReadEntered.WaitAsync(Deadline);
        leaving.Cancel();

        await Assert.That(async () => await leaves.WaitAsync(Deadline)).Throws<OperationCanceledException>();
        await Assert.That(shape.Canceled.IsCompleted).IsFalse().Because("the flight runs on while another requester waits");
        hold.Open();
        var answer = Flight(await stays.WaitAsync(Deadline));
        answer.Dispose();
    }

    [Test]
    public async Task Last_requester_leaving_cancels_the_flight_and_its_read()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start), new FakeSealer());
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        var shape = new FakeShape(Sealer(api), StaleHead, new Latch());
        using var leaving = new CancellationTokenSource();

        var only = ReadFlightAsync(flights, LiveRequest(database), shape, leaving.Token);
        await shape.ReadEntered.WaitAsync(Deadline);
        leaving.Cancel();

        await Assert.That(async () => await only.WaitAsync(Deadline)).Throws<OperationCanceledException>();
        await shape.Canceled.WaitAsync(Deadline);
    }

    [Test]
    public async Task Abandoned_flights_keep_their_running_slots_until_their_work_ends()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start), new FakeSealer());
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        using var hold = new Latch();
        // Reads that ignore cancellation keep their flights running after every requester has left.
        var abandonedShape = new FakeShape(Sealer(api), StaleHead, hold, ignoreCancel: true);
        var others = new FakeShape(Sealer(api), StaleHead, hold, ignoreCancel: true);
        using var leaving = new CancellationTokenSource();

        var abandoned = ReadFlightAsync(flights, LiveRequest(database, "h-abandoned"), abandonedShape, leaving.Token);
        await abandonedShape.ReadEntered.WaitAsync(Deadline);
        leaving.Cancel();
        await Assert.That(async () => await abandoned.WaitAsync(Deadline)).Throws<OperationCanceledException>();

        var running = Enumerable.Range(1, ShapeFlights<ShapePage>.MaxFlights - 1)
            .Select(n => ReadFlightAsync(flights, LiveRequest(database, $"h-{n}"), others))
            .ToArray();
        var refused = await ReadFlightAsync(flights, LiveRequest(database, "h-after"), others).WaitAsync(Deadline);
        await Assert.That(refused is QuotaExceeded { Code: "shape_busy" }).IsTrue()
            .Because("the abandoned flight still occupies one of the 32 running slots");

        hold.Open();
        foreach (var answer in await Task.WhenAll(running).WaitAsync(Deadline))
        {
            Flight(answer).Dispose();
        }
    }

    [Test]
    public async Task Application_stop_cancels_every_parked_flight()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start), new FakeSealer());
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        var head = await HeadAsync(api, database);
        var shape = new FakeShape(Sealer(api), head);

        var parked = ReadFlightAsync(flights, LiveRequest(database), shape);
        await shape.ProbeEntered.WaitAsync(Deadline);
        api.Server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        await Assert.That(async () => await parked.WaitAsync(Deadline)).Throws<OperationCanceledException>();
        await Assert.That(shape.Reads).IsEqualTo(0);
    }

    [Test]
    public async Task Application_stop_cancels_a_seal_that_is_still_running()
    {
        var sealer = new FakeSealer { Hold = new Latch() };
        await using var api = await StartAsync(new FakeTimeProvider(Start), sealer);
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        var head = await HeadAsync(api, database);
        var shape = new FakeShape(Sealer(api), head);

        var parked = ReadFlightAsync(flights, LiveRequest(database), shape);
        await shape.ProbeEntered.WaitAsync(Deadline);
        var written = await api.PutAsync(DocumentApi.NewId(), new { text = new Faker().Lorem.Sentence() });
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
        await sealer.Entered.WaitAsync(Deadline);
        api.Server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        await Assert.That(async () => await parked.WaitAsync(Deadline)).Throws<OperationCanceledException>();
        await sealer.Canceled.WaitAsync(Deadline);
        await Assert.That(shape.Reads).IsEqualTo(0).Because("the read never started once its seal was cancelled");
    }

    [Test]
    public async Task A_database_that_does_not_exist_is_refused_as_not_found()
    {
        await using var api = await StartAsync(new FakeTimeProvider(Start), new FakeSealer());
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        var missing = database with { DatabaseId = DocumentApi.NewId() };
        var shape = new FakeShape(Sealer(api), StaleHead);

        var refused = await ReadFlightAsync(flights, LiveRequest(missing), shape).WaitAsync(Deadline);

        await Assert.That(refused is NotFound { Code: "unknown_database" }).IsTrue();
        await Assert.That(shape.Reads).IsEqualTo(0);
    }

    [Test]
    public async Task A_seal_refusal_reaches_the_requester_unchanged()
    {
        var sealer = new FakeSealer { Refusal = new Unavailable("the head cannot advance", "shape_too_large") };
        await using var api = await StartAsync(new FakeTimeProvider(Start), sealer);
        var flights = Flights(api);
        var database = await api.TenantOfAsync();
        var head = await HeadAsync(api, database);
        var shape = new FakeShape(Sealer(api), head);

        var waiting = ReadFlightAsync(flights, LiveRequest(database), shape);
        await shape.ProbeEntered.WaitAsync(Deadline);
        var written = await api.PutAsync(DocumentApi.NewId(), new { text = new Faker().Lorem.Sentence() });
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
        var refused = await waiting.WaitAsync(Deadline);

        await Assert.That(refused is Unavailable { Code: "shape_too_large" }).IsTrue();
        await Assert.That(shape.Reads).IsEqualTo(0).Because("a stuck seal answers before any read");
    }

    /// <summary>The fake answer. <see cref="UpToDate"/> is derived from the real sealed head, not scripted.</summary>
    private sealed record ShapePage(string Body, long Head, bool UpToDate);

    private static async Task<DocumentApi> StartAsync(FakeTimeProvider time, FakeSealer sealer) =>
        await DocumentApi.StartAsync(services: collection =>
        {
            collection.AddSingleton<TimeProvider>(time);
            collection.AddSingleton<IShapeFlightSealer>(sealer);
            collection.AddSingleton(provider => new ShapeFlights<ShapePage>(
                provider.GetRequiredService<SqliteStorageEngine>(), sealer, provider.GetRequiredService<IMemoryBudget>(),
                time, provider.GetRequiredService<IHostApplicationLifetime>()));
        });

    private static ShapeFlights<ShapePage> Flights(DocumentApi api) => api.Server.Services.GetRequiredService<ShapeFlights<ShapePage>>();

    private static ShapeFlightRequest LiveRequest(TenantContext database, string handle = "h-live", long? cursor = 0) =>
        new(database, handle, "0", true, cursor);

    private static Task<ShapeFlightResult<ShapePage>> ReadFlightAsync(
        ShapeFlights<ShapePage> flights, ShapeFlightRequest request, FakeShape shape, CancellationToken cancellationToken = default) =>
        flights.ReadAsync(request, shape.Probe, shape.Read, cancellationToken).AsTask();

    private static ShapeFlight<ShapePage> Flight(ShapeFlightResult<ShapePage> result) =>
        result is ShapeFlight<ShapePage> flight ? flight : throw new InvalidOperationException("expected a flight, not a refusal");

    private static async Task<SqliteDatabaseStore> StoreAsync(DocumentApi api, TenantContext database) =>
        (SqliteDatabaseStore)(await api.Server.Services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;

    private static FakeSealer Sealer(DocumentApi api) => (FakeSealer)api.Server.Services.GetRequiredService<IShapeFlightSealer>();

    private static async Task<long> HeadAsync(DocumentApi api, TenantContext database) =>
        await FeedHeadAsync(await StoreAsync(api, database), CancellationToken.None);

    private static Task<long> FeedHeadAsync(SqliteDatabaseStore store, CancellationToken cancellationToken) => store.WithWriterAsync(session =>
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = "SELECT seq FROM _tb_feed_sequence WHERE id = 1;";
        return (long)command.ExecuteScalar()!;
    }, "a live-tail test reading the persisted feed frontier", cancellationToken);

    /// <summary>A gate the test opens. Work waiting on it is released by the test, never by time.</summary>
    private sealed class Latch : IDisposable
    {
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Opened => _opened.Task;

        public void Open() => _opened.TrySetResult();

        public void Dispose() => Open();
    }

    /// <summary>The fake seal: counts calls, optionally holds, and returns a scripted refusal.</summary>
    private sealed class FakeSealer : IShapeFlightSealer
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private readonly Lock _gate = new();
        private readonly Dictionary<Ulid, long> _heads = [];

        public Latch? Hold { get; init; }

        public Unavailable? Refusal { get; init; }

        public int Calls => Volatile.Read(ref _calls);

        public long Head(IDatabaseStore store)
        {
            lock (_gate)
            {
                return _heads.GetValueOrDefault(store.Incarnation);
            }
        }

        public Task Entered => _entered.Task;

        public Task Canceled => _canceled.Task;

        public async ValueTask<ShapeFlightSealResult> SealAsync(IDatabaseStore store, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            _entered.TrySetResult();
            using var canceledLease = cancellationToken.Register(() => _canceled.TrySetResult());
            if (Hold is not null)
            {
                await Hold.Opened.WaitAsync(cancellationToken);
            }

            if (Refusal is { } refusal)
            {
                return refusal;
            }

            var head = await FeedHeadAsync((SqliteDatabaseStore)store, cancellationToken);
            lock (_gate)
            {
                _heads[store.Incarnation] = head;
            }

            return new Done();
        }
    }

    /// <summary>
    /// A shape whose probe compares the real sealed head with the client's head, and whose read reports the real head.
    /// Each call is counted and signals its entry; reads reserve their result bytes before they signal.
    /// </summary>
    private sealed class FakeShape(FakeSealer sealer, long clientHead, Latch? hold = null, bool ignoreCancel = false)
    {
        private readonly TaskCompletionSource _probeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _body = new Faker().Lorem.Sentence();
        private int _probes;
        private int _reads;

        public long ResultBytes { get; init; }

        public int Probes => _probes;

        public int Reads => _reads;

        public Task ProbeEntered => _probeEntered.Task;

        public Task ReadEntered => _readEntered.Task;

        public Task Canceled => _canceled.Task;

        public ShapeHeadProbe Probe => (store, _) =>
        {
            _ = Interlocked.Increment(ref _probes);
            _probeEntered.TrySetResult();
            return ValueTask.FromResult(sealer.Head(store) == clientHead);
        };

        public ShapeTailReader<ShapePage> Read => async (store, cancellationToken) =>
        {
            _ = Interlocked.Increment(ref _reads);
            if (ResultBytes > 0)
            {
                QosContext.ReserveResult(ResultBytes);
            }

            _readEntered.TrySetResult();
            using var canceledLease = cancellationToken.Register(() => _canceled.TrySetResult());
            if (hold is not null)
            {
                await hold.Opened.WaitAsync(ignoreCancel ? CancellationToken.None : cancellationToken);
            }

            var head = sealer.Head(store);
            return new ShapePage(_body, head, head == await FeedHeadAsync((SqliteDatabaseStore)store, cancellationToken));
        };
    }
}
