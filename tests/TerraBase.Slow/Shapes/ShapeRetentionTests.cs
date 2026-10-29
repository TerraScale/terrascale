using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Turso.Data.Sqlite;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Features.Shapes;
using TerraBase.Fast.Documents;
using TerraBase.Tenancy;
using TerraBase.Testing;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Fast.Shapes;

/// <summary>
/// Shape retention (FR-20 maintenance): chunks and snapshots older than the feed retention leave, each handle keeps a floor
/// that rejects older offsets, and the work runs in bounded batches. Shape rows are derived data written uncaptured, so no
/// pass may touch the feed or the change log.
/// </summary>
public sealed class ShapeRetentionTests
{
    private const string H1 = "h1-0000000000000001";
    private const string H2 = "h1-0000000000000002";
    private const int Mi = 1024 * 1024;
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task A_request_below_the_floor_answers_409_must_refetch_over_the_real_listener()
    {
        await using var api = await DocumentApi.StartAsync(services: services =>
        {
            services.AddSingleton(provider => new RetentionProbe(new ShapeRetention(new FakeTimeProvider(Now), provider.GetRequiredService<IShapeStorage>())));
            services.AddTerraScaleEndpoints([EndpointRegistration.Create<RetentionProbeEndpoint>()]);
        });
        var probe = api.Server.Services.GetRequiredService<RetentionProbe>();
        probe.Store = await StoreAsync(api.Server, await api.TenantOfAsync());
        await WriteAsync(probe.Store, connection => Chunk(connection, H1, 0, 10, Now.AddDays(-8)));
        _ = await probe.Retention.RunPassAsync(probe.Store, H1, CancellationToken.None);

        using var response = await api.Client.GetAsync("/v1/shape-retention-probe");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("must_refetch");
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
    }

    [Test]
    public async Task The_floor_survives_removing_every_chunk_and_restarting_the_server()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-shape-retention-").FullName;
        try
        {
            TenantContext tenant;
            var clock = new FakeTimeProvider(Now);
            await using (var api = await DocumentApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                tenant = await api.TenantOfAsync();
                var store = await StoreAsync(api.Server, tenant);
                var first = Retention(api.Server, clock);
                await WriteAsync(store, connection => Chunk(connection, H1, 0, 10, Now.AddDays(-8)));
                await Assert.That(await first.RunPassAsync(store, H1, CancellationToken.None)).IsEqualTo(1);
                await Assert.That(await OffsetsAsync(store, H1)).IsEmpty();
            }

            await using var restarted = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
            var retention = Retention(restarted, clock);
            var reopened = await StoreAsync(restarted, tenant);
            await Assert.That(await retention.GetFloorAsync(reopened, H1, CancellationToken.None)).IsEqualTo(10L);
            await Assert.That(await retention.CheckOffsetAsync(reopened, H1, 9, CancellationToken.None)).IsNotNull();
            await Assert.That(await retention.CheckOffsetAsync(reopened, H1, 10, CancellationToken.None)).IsNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task The_floor_covers_feed_gaps_and_the_counter_when_the_feed_becomes_empty()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var retention = Retention(api.Server, new FakeTimeProvider(Now));
        for (var i = 0; i < 4; i++)
        {
            _ = await api.PutAsync(DocumentApi.NewId(), new { n = i });
        }

        var seqs = await FeedSeqsAsync(store);
        await Assert.That(seqs.Length).IsEqualTo(4);
        await Assert.That(await retention.GetFloorAsync(store, H1, CancellationToken.None)).IsEqualTo(0L);
        await WriteAsync(store, connection => Execute(connection, "DELETE FROM _tb_feed WHERE seq = $seq;", ("$seq", seqs[2])));
        _ = await retention.RunPassAsync(store, H1, CancellationToken.None);
        await Assert.That(await retention.GetFloorAsync(store, H1, CancellationToken.None)).IsEqualTo(seqs[2])
            .Because("per-origin feed GC can leave a hole above a recent retained prefix");
        await WriteAsync(store, connection => Execute(connection, "DELETE FROM _tb_feed WHERE seq = $seq;", ("$seq", seqs[3])));
        await Assert.That(await retention.GetFloorAsync(store, H1, CancellationToken.None)).IsEqualTo(seqs[3]);

        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await transaction.TrimFeedAsync(new TerraBase.Engine.Lattices.Hlc(ulong.MaxValue >> 1), 1024, CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        await Assert.That(await FeedSeqsAsync(store)).IsEmpty();
        await Assert.That(await retention.GetFloorAsync(store, H1, CancellationToken.None)).IsEqualTo(seqs[3]);
        _ = await api.PutAsync(DocumentApi.NewId(), new { n = 5 });
        await Assert.That(await retention.GetFloorAsync(store, H1, CancellationToken.None)).IsEqualTo(seqs[3]);
    }

    [Test]
    public async Task Advancing_the_fake_clock_expires_a_chunk_without_changing_feed_retention()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var time = new FakeTimeProvider(Now);
        var retention = Retention(api.Server, time);
        await WriteAsync(store, connection => Chunk(connection, H1, 0, 10, Now));
        time.Advance(TimeSpan.FromDays(7));
        await Assert.That(await retention.RunPassAsync(store, H1, CancellationToken.None)).IsEqualTo(0);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await retention.RunPassAsync(store, H1, CancellationToken.None)).IsEqualTo(1);
    }

    [Test]
    public async Task A_damaged_oversized_body_fails_the_pass_instead_of_reporting_cleanup_complete()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var retention = Retention(api.Server, new FakeTimeProvider(Now));
        await WriteAsync(store, connection => Chunk(connection, H1, 0, 10, Now.AddDays(-8), 8 * Mi + 1));
        await Assert.That(async () => await retention.RunPassAsync(store, H1, CancellationToken.None)).Throws<InvalidDataException>();
        await Assert.That(async () => await retention.DropHandleAsync(store, H1, CancellationToken.None)).Throws<InvalidDataException>();
        await Assert.That(await retention.GetFloorAsync(store, H1, CancellationToken.None)).IsEqualTo(0L);
        await Assert.That(await OffsetsAsync(store, H1)).IsEquivalentTo([0L]);
    }

    [Test]
    public async Task A_partial_rotation_drop_keeps_its_floor_and_shares_the_byte_budget()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var retention = Retention(api.Server, new FakeTimeProvider(Now));
        await WriteAsync(store, connection =>
        {
            Chunk(connection, H1, 0, 10, Now.AddDays(-8));
            Chunk(connection, H1, 10, 20, Now, 4 * Mi);
            Chunk(connection, H1, 20, 30, Now, 4 * Mi);
            Snapshot(connection, H1, 30, 0, Now, 4 * Mi);
        });
        _ = await retention.RunPassAsync(store, H1, CancellationToken.None);
        var before = await BodyBytesAsync(store, H1);
        await Assert.That(await retention.DropHandleAsync(store, H1, CancellationToken.None)).IsEqualTo(2);
        await Assert.That(before - await BodyBytesAsync(store, H1)).IsEqualTo(8L * Mi);
        await Assert.That(await retention.GetFloorAsync(store, H1, CancellationToken.None)).IsEqualTo(10L);
        await Assert.That(await PagesAsync(store, H1)).IsEquivalentTo([(30L, 0L)]);
        await Assert.That(await retention.DropHandleAsync(store, H1, CancellationToken.None)).IsEqualTo(2);
        await Assert.That(await retention.GetFloorAsync(store, H1, CancellationToken.None)).IsEqualTo(0L);
        await Assert.That(await PagesAsync(store, H1)).IsEmpty();
    }

    [Test]
    public async Task Chunks_older_than_seven_days_leave_and_set_the_floor_while_the_boundary_and_recent_chunks_stay()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var time = new FakeTimeProvider(Now);
        var retention = Retention(api.Server, time);
        await WriteAsync(store, connection =>
        {
            Chunk(connection, H1, 0, 10, Ago(time, TimeSpan.FromDays(7) + TimeSpan.FromMilliseconds(1)));
            Chunk(connection, H1, 10, 20, Ago(time, TimeSpan.FromDays(7)));
            Chunk(connection, H1, 20, 30, Ago(time, TimeSpan.FromDays(1)));
            Chunk(connection, H2, 0, 10, Ago(time, TimeSpan.FromDays(30)));
        });
        var feedBefore = await FeedSeqsAsync(store);
        var counterBefore = await ScalarAsync(store, "SELECT seq FROM _tb_feed_sequence WHERE id = 1;");
        var cdcBefore = await ScalarAsync(store, "SELECT COUNT(*) FROM turso_cdc;");

        await Assert.That(await retention.RunPassAsync(store, H1, CancellationToken.None)).IsEqualTo(1);

        await Assert.That(await retention.GetFloorAsync(store, H1, CancellationToken.None)).IsEqualTo(10L)
            .Because("the floor becomes the end of the newest expired chunk");
        await Assert.That(await retention.GetFloorAsync(store, H2, CancellationToken.None)).IsEqualTo(0L)
            .Because("a pass on one handle leaves the other handle's floor alone");
        await Assert.That(await OffsetsAsync(store, H1)).IsEquivalentTo([10L, 20L]);
        await Assert.That(await OffsetsAsync(store, H2)).IsEquivalentTo([0L])
            .Because("the second handle's expired chunk is not touched by a pass on the first");

        var below = await retention.CheckOffsetAsync(store, H1, 9, CancellationToken.None);
        await Assert.That(below).IsNotNull();
        await Assert.That(below!.Code).IsEqualTo("must_refetch");
        await Assert.That(await retention.CheckOffsetAsync(store, H1, 10, CancellationToken.None)).IsNull();
        await Assert.That(await retention.CheckOffsetAsync(store, H2, 0, CancellationToken.None)).IsNull();

        await Assert.That(await FeedSeqsAsync(store)).IsEquivalentTo(feedBefore);
        await Assert.That(await ScalarAsync(store, "SELECT seq FROM _tb_feed_sequence WHERE id = 1;")).IsEqualTo(counterBefore);
        await Assert.That(await ScalarAsync(store, "SELECT COUNT(*) FROM turso_cdc;")).IsEqualTo(cdcBefore)
            .Because("shape maintenance writes uncaptured and never reaches the change log");
    }

    [Test]
    public async Task Obsolete_snapshots_below_the_floor_go_but_the_newest_offset_keeps_every_page()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var time = new FakeTimeProvider(Now);
        var retention = Retention(api.Server, time);
        await WriteAsync(store, connection =>
        {
            Chunk(connection, H1, 0, 50, Ago(time, TimeSpan.FromDays(8)));
            Snapshot(connection, H1, 0, 0, Ago(time, TimeSpan.FromDays(30)));
            Snapshot(connection, H1, 0, 1, Ago(time, TimeSpan.FromDays(30)));
            Snapshot(connection, H1, 20, 0, Ago(time, TimeSpan.FromDays(1)));
            Snapshot(connection, H1, 40, 0, Ago(time, TimeSpan.FromDays(30)));
            Snapshot(connection, H1, 40, 1, Ago(time, TimeSpan.FromDays(30)));
            Snapshot(connection, H1, 40, 2, Ago(time, TimeSpan.FromDays(30)));
            Snapshot(connection, H2, 0, 0, Ago(time, TimeSpan.FromDays(30)));
        });

        await DrainAsync(retention, store, H1);

        await Assert.That(await PagesAsync(store, H1)).IsEquivalentTo([(20L, 0L), (40L, 0L), (40L, 1L), (40L, 2L)])
            .Because("below-floor and older than 7 days goes, except the newest offset, which keeps all its pages");
        await Assert.That(await PagesAsync(store, H2)).IsEquivalentTo([(0L, 0L)])
            .Because("a snapshot of the other handle is above its own floor of 0");
    }

    [Test]
    public async Task The_newest_complete_snapshot_keeps_every_page_while_a_newer_one_is_still_being_sealed()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var time = new FakeTimeProvider(Now);
        var retention = Retention(api.Server, time);
        await WriteAsync(store, connection =>
        {
            Chunk(connection, H1, 0, 50, Ago(time, TimeSpan.FromDays(8)));
            Snapshot(connection, H1, 10, 0, Ago(time, TimeSpan.FromDays(30)), next: "a");
            Snapshot(connection, H1, 10, 1, Ago(time, TimeSpan.FromDays(30)));
            // A seal of offset 30 published its first page only: it has no terminal page yet.
            Snapshot(connection, H1, 30, 0, Ago(time, TimeSpan.FromDays(30)), next: "b");
        });

        await DrainAsync(retention, store, H1);

        await Assert.That(await PagesAsync(store, H1)).IsEquivalentTo([(10L, 0L), (10L, 1L), (30L, 0L)])
            .Because("an incomplete newer offset is not a snapshot a refetch can start from");
    }

    [Test]
    public async Task Dropping_a_handle_removes_chunks_and_snapshots_of_every_age_and_only_that_handle()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var time = new FakeTimeProvider(Now);
        var retention = Retention(api.Server, time);
        await WriteAsync(store, connection =>
        {
            Chunk(connection, H1, 0, 10, Ago(time, TimeSpan.FromDays(8)));
            Chunk(connection, H1, 10, 20, Ago(time, TimeSpan.FromDays(1)));
            Snapshot(connection, H1, 0, 0, Ago(time, TimeSpan.FromDays(30)));
            Snapshot(connection, H1, 5, 0, Ago(time, TimeSpan.FromDays(1)));
            Chunk(connection, H2, 0, 10, Ago(time, TimeSpan.FromDays(8)));
            Chunk(connection, H2, 10, 20, Ago(time, TimeSpan.FromDays(1)));
            Snapshot(connection, H2, 0, 0, Ago(time, TimeSpan.FromDays(30)));
        });
        await DrainAsync(retention, store, H1);
        await DrainAsync(retention, store, H2);
        var floorH2 = await retention.GetFloorAsync(store, H2, CancellationToken.None);
        var h2Chunks = await OffsetsAsync(store, H2);
        var h2Pages = await PagesAsync(store, H2);
        await Assert.That(await ScalarAsync(store, "SELECT COUNT(*) FROM _tb_shape_chunk WHERE handle = '~floor:' || $h;", ("$h", H1))).IsEqualTo(1L);

        await DrainDropAsync(retention, store, H1);

        await Assert.That(await OffsetsAsync(store, H1)).IsEmpty();
        await Assert.That(await PagesAsync(store, H1)).IsEmpty();
        await Assert.That(await ScalarAsync(store, "SELECT COUNT(*) FROM _tb_shape_chunk WHERE handle = '~floor:' || $h;", ("$h", H1))).IsEqualTo(0L)
            .Because("the floor metadata is removed once the handle is dropped");
        await Assert.That(await retention.GetFloorAsync(store, H1, CancellationToken.None)).IsEqualTo(0L);
        await Assert.That(await OffsetsAsync(store, H2)).IsEquivalentTo(h2Chunks);
        await Assert.That(await PagesAsync(store, H2)).IsEquivalentTo(h2Pages);
        await Assert.That(await retention.GetFloorAsync(store, H2, CancellationToken.None)).IsEqualTo(floorH2);
    }

    [Test]
    public async Task A_pass_deletes_at_most_1024_rows_across_chunks_and_snapshots_and_the_rest_follow_in_later_passes()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var time = new FakeTimeProvider(Now);
        var retention = Retention(api.Server, time);
        await WriteAsync(store, connection =>
        {
            // 700 expired chunks end at 7000; 401 old snapshots sit below it, and the newest (offset 4000) is kept: 1100 eligible.
            for (var i = 0; i < 700; i++)
            {
                Chunk(connection, H1, i * 10, i * 10 + 10, Ago(time, TimeSpan.FromDays(8)));
            }

            for (var i = 0; i <= 400; i++)
            {
                Snapshot(connection, H1, i * 10, 0, Ago(time, TimeSpan.FromDays(30)));
            }
        });

        var first = await retention.RunPassAsync(store, H1, CancellationToken.None);

        await Assert.That(first).IsGreaterThan(0);
        await Assert.That(first).IsLessThanOrEqualTo(ShapeRetention.BatchRows);
        await Assert.That(await ScalarAsync(store, "SELECT COUNT(*) FROM _tb_shape_chunk WHERE handle = $h;", ("$h", H1)) + await ScalarAsync(store, "SELECT COUNT(*) FROM _tb_shape_snapshot WHERE handle = $h;", ("$h", H1)))
            .IsGreaterThan(1L).Because("1100 eligible rows cannot all go in one pass");

        var rest = await DrainAsync(retention, store, H1);

        await Assert.That(first + rest).IsEqualTo(1100);
        await Assert.That(await OffsetsAsync(store, H1)).IsEmpty();
        await Assert.That(await PagesAsync(store, H1)).IsEquivalentTo([(4000L, 0L)]);
    }

    [Test]
    public async Task A_pass_stops_within_the_8_mebibyte_budget_shared_by_chunks_and_snapshots()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api.Server, await api.TenantOfAsync());
        var time = new FakeTimeProvider(Now);
        var retention = Retention(api.Server, time);
        await WriteAsync(store, connection =>
        {
            Chunk(connection, H1, 0, 10, Ago(time, TimeSpan.FromDays(8)), 4 * Mi);
            Chunk(connection, H1, 10, 20, Ago(time, TimeSpan.FromDays(8)), 4 * Mi);
            Chunk(connection, H1, 20, 30, Ago(time, TimeSpan.FromDays(8)), 4 * Mi);
            Snapshot(connection, H1, 0, 0, Ago(time, TimeSpan.FromDays(8)), Mi);
            Snapshot(connection, H1, 5, 0, Ago(time, TimeSpan.FromDays(1)));
        });
        var total = await BodyBytesAsync(store, H1);
        await Assert.That(total).IsEqualTo(13L * Mi + 2);

        var before = total;
        var deleted = await retention.RunPassAsync(store, H1, CancellationToken.None);
        var after = await BodyBytesAsync(store, H1);

        await Assert.That(deleted).IsGreaterThan(0);
        await Assert.That(before - after).IsLessThanOrEqualTo(8L * Mi).Because("one pass stops once its byte budget is spent");
        await Assert.That(after).IsGreaterThanOrEqualTo(5L * Mi).Because("13 MiB minus at most 8 MiB is still left after one pass");

        await DrainAsync(retention, store, H1);

        await Assert.That(await OffsetsAsync(store, H1)).IsEmpty();
        await Assert.That(await PagesAsync(store, H1)).IsEquivalentTo([(5L, 0L)])
            .Because("the newest snapshot offset is kept whatever its age");
    }

    private static DateTimeOffset Ago(TimeProvider time, TimeSpan age) => time.GetUtcNow() - age;

    private static ShapeRetention Retention(TerraBaseTestServer server, TimeProvider time) =>
        new(time, new TursoShapeStorage(server.Services.GetRequiredService<TursoStorageEngine>()));

    private static async Task<TursoDatabaseStore> StoreAsync(TerraBaseTestServer server, TenantContext tenant) =>
        (TursoDatabaseStore)(await server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;

    private static async Task<int> DrainAsync(ShapeRetention retention, TursoDatabaseStore store, string handle)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await retention.RunPassAsync(store, handle, CancellationToken.None);
            total += deleted;
        }
        while (deleted > 0);

        return total;
    }

    private static async Task<int> DrainDropAsync(ShapeRetention retention, TursoDatabaseStore store, string handle)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await retention.DropHandleAsync(store, handle, CancellationToken.None);
            total += deleted;
        }
        while (deleted > 0);

        return total;
    }

    // Shape rows are written with the capture-off pragma SuspendCaptureAsync sends, then capture is turned back on.
    private static Task<bool> WriteAsync(TursoDatabaseStore store, Action<SqliteConnection> write) => store.WithWriterAsync(session =>
    {
        Execute(session.Connection, TursoSession.CaptureOff);
        try
        {
            write(session.Connection);
        }
        finally
        {
            Execute(session.Connection, TursoSession.CaptureOn);
        }

        return true;
    }, "a test writing shape rows uncaptured", CancellationToken.None);

    private static void Chunk(SqliteConnection connection, string handle, long offset, long end, DateTimeOffset sealedAt, int bytes = 2) =>
        Execute(connection,
            "INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($h, $o, $e, $b, $s);",
            ("$h", handle), ("$o", offset), ("$e", end), ("$b", new byte[bytes]), ("$s", sealedAt.ToUnixTimeMilliseconds()));

    private static void Snapshot(SqliteConnection connection, string handle, long offset, long page, DateTimeOffset sealedAt, int bytes = 2,
        string? next = null) =>
        Execute(connection,
            "INSERT INTO _tb_shape_snapshot (handle, offset, page, next_id, body, sealed_at) VALUES ($h, $o, $p, $n, $b, $s);",
            ("$h", handle), ("$o", offset), ("$p", page), ("$n", next), ("$b", new byte[bytes]), ("$s", sealedAt.ToUnixTimeMilliseconds()));

    private static Task<long[]> OffsetsAsync(TursoDatabaseStore store, string handle) => store.WithWriterAsync(session =>
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = "SELECT offset FROM _tb_shape_chunk WHERE handle = $h ORDER BY offset;";
        Bind(command, ("$h", handle));
        using var reader = command.ExecuteReader();
        List<long> offsets = [];
        while (reader.Read())
        {
            offsets.Add(reader.GetInt64(0));
        }

        return offsets.ToArray();
    }, "a test reading chunk offsets", CancellationToken.None);

    private static Task<(long Offset, long Page)[]> PagesAsync(TursoDatabaseStore store, string handle) => store.WithWriterAsync(session =>
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = "SELECT offset, page FROM _tb_shape_snapshot WHERE handle = $h ORDER BY offset, page;";
        Bind(command, ("$h", handle));
        using var reader = command.ExecuteReader();
        List<(long, long)> pages = [];
        while (reader.Read())
        {
            pages.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return pages.ToArray();
    }, "a test reading snapshot pages", CancellationToken.None);

    private static Task<long> BodyBytesAsync(TursoDatabaseStore store, string handle) => ScalarAsync(store,
        "SELECT (SELECT COALESCE(SUM(length(body)), 0) FROM _tb_shape_chunk WHERE handle = $h) + (SELECT COALESCE(SUM(length(body)), 0) FROM _tb_shape_snapshot WHERE handle = $h);",
        ("$h", handle));

    private static Task<long[]> FeedSeqsAsync(TursoDatabaseStore store) => store.WithWriterAsync(session =>
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = "SELECT seq FROM _tb_feed ORDER BY seq;";
        using var reader = command.ExecuteReader();
        List<long> seqs = [];
        while (reader.Read())
        {
            seqs.Add(reader.GetInt64(0));
        }

        return seqs.ToArray();
    }, "a test reading the feed sequence", CancellationToken.None);

    private static Task<long> ScalarAsync(TursoDatabaseStore store, string sql, params (string Name, object? Value)[] parameters) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            Bind(command, parameters);
            return (long)command.ExecuteScalar()!;
        }, "a test reading shape rows", CancellationToken.None);

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private sealed class RetentionProbe(ShapeRetention retention)
    {
        public ShapeRetention Retention { get; } = retention;
        public TursoDatabaseStore? Store { get; set; }
    }

    /// <summary>Test-only adapter for the error returned to FR-20; no production route is registered by this lane.</summary>
    public sealed class RetentionProbeEndpoint : EndpointWithoutRequest<IResult>
    {
        /// <inheritdoc />
        public override void Configure()
        {
            Get("/v1/shape-retention-probe");
            Name("shape-retention-probe");
            AllowAnonymous();
            Metadata(ListenerMetadata.Client);
            Produces<ErrorBody>(StatusCodes.Status409Conflict);
        }

        /// <inheritdoc />
        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var probe = HttpContext.RequestServices.GetRequiredService<RetentionProbe>();
            var failure = await probe.Retention.CheckOffsetAsync(probe.Store!, H1, 9, cancellationToken);
            return failure is not null ? ErrorResponse.From(failure) : TypedResults.NoContent();
        }
    }
}
