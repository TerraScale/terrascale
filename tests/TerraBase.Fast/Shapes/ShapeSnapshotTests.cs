using System.Buffers.Text;
using System.Diagnostics.Metrics;
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
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Storage;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;

namespace TerraBase.Fast.Shapes;

/// <summary>
/// FR-13 snapshots (shape-reads.md sections 3.2 and 6): every page of a snapshot is built in one read transaction whose
/// feed head is S, pages are written by short uncaptured writer transactions, and only finished snapshots are served.
/// The snapshot API runs against the decorated production store; the raw SQLite store is used only to inspect and damage rows.
/// Reseal policy: 4096 chunks past the newest snapshot's offset, or one hour since it was sealed.
/// </summary>
public sealed class ShapeSnapshotTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly string[] Envelope =
        ["shape_id", "handle", "offset", "next_offset", "covered_offset", "values_at", "up_to_date", "cursor_resume", "changes"];

    // The gate for snapshot isolation on Turso: a read transaction keeps its feed head and row values while HTTP writes commit.
    [Test]
    public async Task A_read_transaction_keeps_its_feed_head_and_page_bytes_while_http_writes_commit()
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        var ids = Ids(5);
        var model = Model();
        _ = await WriteAsync(api, model, [.. ids.Select((id, n) => (id, (JsonObject?)Doc(n)))]);
        var shape = Shape();
        var transactions = fixture.Transactions;

        long head;
        await using (var reader = await transactions.BeginAsync(fixture.Store, write: false, CancellationToken.None))
        {
            var storage = (IShapeSnapshotStorage)reader;
            var (offset, cursor) = await storage.ShapeSnapshotHeadAsync(shape.ResumeScope, Start, CancellationToken.None);
            head = offset;
            byte[] before;
            using (var page = await storage.BuildShapeSnapshotPageAsync(shape, offset, null, cursor, fixture.Memory, fixture.Time, CancellationToken.None))
            {
                before = page.Body.ToArray();
            }

            var oldVersion = (await api.GetAsync(ids[0])).Version;
            _ = await WriteAsync(api, model, (ids[0], Doc(100)), (ids[2], null), (DocumentApi.NewId(), Doc(101)));
            await Assert.That(await CounterAsync(fixture.Raw)).IsGreaterThan(offset).Because("the HTTP writes committed while the reader stayed open");

            var (again, cursorAgain) = await storage.ShapeSnapshotHeadAsync(shape.ResumeScope, Start, CancellationToken.None);
            await Assert.That(again).IsEqualTo(offset);
            await Assert.That(cursorAgain).IsEqualTo(cursor);
            using (var page = await storage.BuildShapeSnapshotPageAsync(shape, offset, null, cursor, fixture.Memory, fixture.Time, CancellationToken.None))
            {
                await Assert.That(page.Body.ToArray().SequenceEqual(before)).IsTrue().Because("the reader still sees the rows at its head");
            }

            var stored = await reader.GetAsync(DocumentApi.Collection, ids[0], CancellationToken.None);
            await Assert.That(ETags.Version(stored!.Scope, DocumentApi.Collection, stored.Id, stored.Version, stored.Json)).IsEqualTo(oldVersion);
        }

        await using var fresh = await transactions.BeginAsync(fixture.Store, write: false, CancellationToken.None);
        var (current, _) = await ((IShapeSnapshotStorage)fresh).ShapeSnapshotHeadAsync(shape.ResumeScope, Start, CancellationToken.None);
        await Assert.That(current).IsEqualTo(await CounterAsync(fixture.Raw));
        await Assert.That(current).IsGreaterThan(head);
    }

    [Test]
    public async Task A_seal_scanning_while_http_writes_commit_publishes_byte_bounded_contiguous_pages_at_its_head()
    {
        // The blocked predicate holds one storage thread, so the concurrent HTTP writes need others.
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-threads"] = "4");
        await using var fixture = await Fixture.OpenAsync(api);
        var faker = new Faker();
        var ids = Ids(1000);
        var model = Model();
        var versions = await WriteAsync(api, model, [.. ids.Select((id, n) => (id, (JsonObject?)Doc(n, faker.Random.AlphaNumeric(1500))))]);
        var expected = Model(model);

        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shape = Shape(document =>
        {
            if (started.TrySetResult())
            {
                _ = release.Wait(Wait);
            }

            return true;
        });

        var seal = Task.Run(async () => await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None));
        long afterWrites;
        try
        {
            await started.Task.WaitAsync(Wait);
            await Assert.That(async () => await fixture.Snapshots.SealIfDueAsync(fixture.Store, Shape(), CancellationToken.None))
                .Throws<StorageBusyException>().Because("one seal is admitted at a time");

            // Updates at both ends and the middle, deletes, an id inside the keyset range and one after it, all while page 0 scans.
            _ = await WriteAsync(api, model, (ids[0], Doc(-1)), (ids[500], Doc(-2)), (ids[999], Doc(-3)), (ids[1], null), (ids[998], null),
                (ids[500] + "x", Doc(-4)), (DocumentApi.NewId(), Doc(-5)));
            afterWrites = await CounterAsync(fixture.Raw);
        }
        finally
        {
            // A failed write or assertion must not leave the storage thread blocked in the predicate.
            release.Set();
        }

        var snapshot = await seal;

        await Assert.That(snapshot.Offset).IsLessThan(afterWrites).Because("the snapshot head is the reader's, taken before the writes");
        await Assert.That(snapshot.SealedAt).IsEqualTo(Start.ToUnixTimeMilliseconds());
        await Assert.That(await fixture.Snapshots.NewestAsync(fixture.Store, shape.Handle, CancellationToken.None)).IsEqualTo(snapshot);

        var (pages, nextIds) = await PagesAsync(fixture.Snapshots, fixture.Store, shape.Handle, snapshot.Offset);
        await Assert.That(pages.Count).IsGreaterThanOrEqualTo(2);
        var ops = await OpsAsync(pages, shape, snapshot.Offset);
        await AssertMatchesAsync(ops, expected);
        foreach (var op in ops)
        {
            await Assert.That(op.GetProperty("version").GetString()).IsEqualTo(versions[Id(op)]);
        }

        await Assert.That((await api.GetAsync(ids[0])).Version).IsNotEqualTo(versions[ids[0]]).Because("the write did commit");

        // Pages are byte bounded below the document limit, and each continuation is the page's last examined id.
        var first = await PageOpsAsync(pages[0]);
        await Assert.That(first.Length).IsLessThan(ShapeSnapshots.PageDocuments);
        for (var i = 0; i < nextIds.Count - 1; i++)
        {
            var pageOps = await PageOpsAsync(pages[i]);
            await Assert.That(nextIds[i]).IsEqualTo(Id(pageOps[^1]));
        }

        // Stored pages are fixed bytes: reading the chain again returns them unchanged.
        var (again, _) = await PagesAsync(fixture.Snapshots, fixture.Store, shape.Handle, snapshot.Offset);
        await Assert.That(again.Count).IsEqualTo(pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            await Assert.That(again[i].SequenceEqual(pages[i])).IsTrue();
        }

        await Assert.That(await ScalarAsync(fixture.Raw, $"SELECT COUNT(*) FROM _tb_shape_snapshot WHERE handle = '{shape.Handle}';")).IsEqualTo((long)pages.Count);
        await Assert.That(await ScalarAsync(fixture.Raw, "SELECT COUNT(*) FROM turso_cdc WHERE table_name = '_tb_shape_snapshot';")).IsEqualTo(0L)
            .Because("snapshot pages are derived data written uncaptured");
        using var whole = fixture.Memory.TryReserve(MemoryLane.Bulk, Fixture.Limit, tenant: null);
        await Assert.That(whole).IsNotNull().Because("every seal and page admission was returned");
    }

    [Test]
    public async Task A_snapshot_at_S_equals_the_replayed_write_history_and_feed_through_S()
    {
        // FR-12 materialized chunks are not on this branch; S is checked against the HTTP write history and the feed instead.
        await using var api = await DocumentApi.StartAsync();
        await StorageFixture.CreateCollectionAsync(api.Tenant, DocumentApi.Database, "other");
        await using var fixture = await Fixture.OpenAsync(api);
        var ids = Ids(10);
        var model = Model();
        _ = await WriteAsync(api, model, [.. ids.Select((id, n) => (id, (JsonObject?)Doc(n)))]);
        _ = await WriteAsync(api, model, (ids[0], Doc(10)), (ids[1], Doc(11)), (ids[2], null));
        await Assert.That((await api.TransactAsync(new { op = "put", collection = "other", id = ids[3], document = Doc(99) })).Status).IsEqualTo(HttpStatusCode.OK);
        var shape = Shape();
        var first = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        var firstModel = Model(model);

        var created = DocumentApi.NewId();
        _ = await WriteAsync(api, model, (ids[3], Doc(13)), (ids[4], null), (created, Doc(14)), (ids[2], Doc(12)), (ids[0], Doc(20)));
        fixture.Time.Advance(ShapeSnapshots.ResealAge);
        var second = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);

        await Assert.That(second.Offset).IsGreaterThan(first.Offset);
        await Assert.That(second.Offset).IsEqualTo(await CounterAsync(fixture.Raw));
        await Assert.That(await fixture.Snapshots.NewestAsync(fixture.Store, shape.Handle, CancellationToken.None)).IsEqualTo(second);

        foreach (var (snapshot, expected) in new[] { (first, firstModel), (second, model) })
        {
            var (pages, _) = await PagesAsync(fixture.Snapshots, fixture.Store, shape.Handle, snapshot.Offset);
            var ops = await OpsAsync(pages, shape, snapshot.Offset);
            await AssertMatchesAsync(ops, expected);
            await Assert.That(string.Join(',', await FeedLiveAsync(fixture.Raw, snapshot.Offset))).IsEqualTo(string.Join(',', expected.Keys))
                .Because("replaying the feed through S gives the snapshot's live ids");
        }

        var (current, _) = await PagesAsync(fixture.Snapshots, fixture.Store, shape.Handle, second.Offset);
        foreach (var op in await OpsAsync(current, shape, second.Offset))
        {
            await Assert.That(op.GetProperty("version").GetString()).IsEqualTo((await api.GetAsync(Id(op))).Version)
                .Because("the op version is the Document API version");
        }
    }

    [Test]
    public async Task An_empty_collection_seals_one_page_without_ops_and_a_second_seal_reuses_it()
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        var shape = Shape();

        var snapshot = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        await Assert.That(snapshot.Offset).IsEqualTo(await CounterAsync(fixture.Raw));
        await Assert.That(snapshot.FirstOffset).IsEqualTo($"snap.{snapshot.Offset.ToString(CultureInfo.InvariantCulture)}.0");
        var (pages, nextIds) = await PagesAsync(fixture.Snapshots, fixture.Store, shape.Handle, snapshot.Offset);
        await Assert.That(pages.Count).IsEqualTo(1);
        await Assert.That(nextIds[0]).IsNull();
        await Assert.That(await OpsAsync(pages, shape, snapshot.Offset)).IsEmpty();

        await Assert.That(await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None)).IsEqualTo(snapshot);
        await Assert.That(await ScalarAsync(fixture.Raw, $"SELECT COUNT(*) FROM _tb_shape_snapshot WHERE handle = '{shape.Handle}';")).IsEqualTo(1L);
    }

    [Test]
    public async Task A_filtered_scan_stops_after_10000_examined_ids_and_still_advances()
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        var ids = Ids(ShapeSnapshots.PageExamined + 1);
        _ = await WriteAsync(api, Model(), [.. ids.Select((id, n) => (id, (JsonObject?)Doc(n)))]);

        var shape = Shape(document => document.Id == ids[^1]);
        var snapshot = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        var (pages, nextIds) = await PagesAsync(fixture.Snapshots, fixture.Store, shape.Handle, snapshot.Offset);

        await Assert.That(pages.Count).IsEqualTo(2);
        await Assert.That(nextIds[0]).IsEqualTo(ids[ShapeSnapshots.PageExamined - 1]).Because("the continuation is the last examined id");
        await Assert.That(await PageOpsAsync(pages[0])).IsEmpty();
        var ops = await OpsAsync(pages, shape, snapshot.Offset);
        await Assert.That(string.Join(',', ops.Select(Id))).IsEqualTo(ids[^1]);
    }

    [Test]
    public async Task A_cancelled_seal_publishes_no_partial_snapshot_and_the_next_seal_replaces_its_pages()
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        var ids = Ids(3);
        var model = Model();
        // Each 600 KiB document fills a page on its own, so the seal writes three pages.
        _ = await WriteAsync(api, model, [.. ids.Select((id, n) => (id, (JsonObject?)Doc(n, new string('x', 600 * 1024))))]);
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        // Page 0 sees documents 0 and 1 (1 does not fit); the third call is page 1, after page 0 is stored.
        var shape = Shape(document =>
        {
            if (Interlocked.Increment(ref calls) == 3)
            {
                cancel.Cancel();
            }

            return true;
        });

        await Assert.That(async () => await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, cancel.Token)).Throws<OperationCanceledException>();
        var head = await CounterAsync(fixture.Raw);
        await Assert.That(await ScalarAsync(fixture.Raw, $"SELECT COUNT(*) FROM _tb_shape_snapshot WHERE handle = '{shape.Handle}' AND next_id IS NOT NULL;"))
            .IsGreaterThanOrEqualTo(1L).Because("the interrupted seal stored its first page");
        await Assert.That(await ScalarAsync(fixture.Raw, $"SELECT COUNT(*) FROM _tb_shape_snapshot WHERE handle = '{shape.Handle}' AND next_id IS NULL;")).IsEqualTo(0L);
        await Assert.That(await fixture.Snapshots.NewestAsync(fixture.Store, shape.Handle, CancellationToken.None)).IsNull();
        await Assert.That(await fixture.Snapshots.ReadPageAsync(fixture.Store, shape.Handle, head, null, CancellationToken.None)).IsNull();
        await Assert.That(await fixture.Snapshots.ReadPageAsync(fixture.Store, shape.Handle, head, ids[0], CancellationToken.None)).IsNull();

        var snapshot = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        await Assert.That(snapshot.Offset).IsEqualTo(head);
        var (pages, _) = await PagesAsync(fixture.Snapshots, fixture.Store, shape.Handle, snapshot.Offset);
        await Assert.That(pages.Count).IsEqualTo(3);
        await AssertMatchesAsync(await OpsAsync(pages, shape, snapshot.Offset), model);
        await Assert.That(await ScalarAsync(fixture.Raw, $"SELECT COUNT(*) FROM _tb_shape_snapshot WHERE handle = '{shape.Handle}';")).IsEqualTo(3L)
            .Because("the next seal deleted the unfinished pages before writing its own");
    }

    [Test]
    public async Task A_seal_without_memory_admission_or_after_cancellation_publishes_nothing()
    {
        await using var api = await DocumentApi.StartAsync();
        // A Hobby tenant gets 1/16 of 64 MiB: the seal's small admissions fit, the 8 MiB page workspace does not.
        await using var fixture = await Fixture.OpenAsync(api, limit: 64L * 1024 * 1024);
        _ = await WriteAsync(api, Model(), (DocumentApi.NewId(), Doc(1)));
        var shape = Shape();

        await Assert.That(async () => await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None)).Throws<StorageBusyException>();
        await Assert.That(async () => await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, new CancellationToken(canceled: true)))
            .Throws<OperationCanceledException>();

        await Assert.That(await fixture.Snapshots.NewestAsync(fixture.Store, shape.Handle, CancellationToken.None)).IsNull();
        await Assert.That(await ScalarAsync(fixture.Raw, $"SELECT COUNT(*) FROM _tb_shape_snapshot WHERE handle = '{shape.Handle}';")).IsEqualTo(0L);
        using var whole = fixture.Memory.TryReserve(MemoryLane.Bulk, 64L * 1024 * 1024, tenant: null);
        await Assert.That(whole).IsNotNull().Because("the refused seal returned its admissions");
    }

    [Test]
    public async Task A_read_page_keeps_its_memory_admission_until_the_page_is_disposed()
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        _ = await WriteAsync(api, Model(), (DocumentApi.NewId(), Doc(1, new string('y', 64 * 1024))));
        var shape = Shape();
        var snapshot = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);

        using (var page = await fixture.Snapshots.ReadPageAsync(fixture.Store, shape.Handle, snapshot.Offset, null, CancellationToken.None))
        {
            await Assert.That(page).IsNotNull();
            using var held = fixture.Memory.TryReserve(MemoryLane.Bulk, Fixture.Limit, tenant: null);
            await Assert.That(held).IsNull().Because("the returned page still holds its admission");
        }

        using var released = fixture.Memory.TryReserve(MemoryLane.Bulk, Fixture.Limit, tenant: null);
        await Assert.That(released).IsNotNull();
    }

    [Test]
    public async Task A_page_holds_at_most_1000_documents()
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        var ids = Ids(ShapeSnapshots.PageDocuments + 1);
        var model = Model();
        _ = await WriteAsync(api, model, [.. ids.Select((id, n) => (id, (JsonObject?)Doc(n)))]);
        var shape = Shape();

        var snapshot = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        var (pages, nextIds) = await PagesAsync(fixture.Snapshots, fixture.Store, shape.Handle, snapshot.Offset);
        await Assert.That(pages.Count).IsEqualTo(2);
        await Assert.That((await PageOpsAsync(pages[0])).Length).IsEqualTo(ShapeSnapshots.PageDocuments);
        await Assert.That(nextIds[0]).IsEqualTo(ids[ShapeSnapshots.PageDocuments - 1]);
        await AssertMatchesAsync(await OpsAsync(pages, shape, snapshot.Offset), model);
    }

    [Test]
    public async Task A_document_too_large_to_share_a_page_travels_alone_past_1_MiB()
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        var ids = Ids(3);
        var model = Model();
        _ = await WriteAsync(api, model, (ids[0], Doc(0)), (ids[1], Doc(1, new string('z', ShapeSnapshots.PageBytes - 256))), (ids[2], Doc(2)));
        var shape = Shape();

        var snapshot = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        var (pages, _) = await PagesAsync(fixture.Snapshots, fixture.Store, shape.Handle, snapshot.Offset);
        await Assert.That(pages.Count).IsEqualTo(3);
        await Assert.That(pages[1].Length).IsGreaterThan(ShapeSnapshots.PageBytes).Because("the singleton page is above 1 MiB with its envelope");
        await Assert.That((await PageOpsAsync(pages[1])).Length).IsEqualTo(1);
        await AssertMatchesAsync(await OpsAsync(pages, shape, snapshot.Offset), model);
    }

    [Test]
    public async Task A_page_closes_after_its_5_second_deadline_and_the_next_page_continues()
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        var ids = Ids(5);
        var model = Model();
        _ = await WriteAsync(api, model, [.. ids.Select((id, n) => (id, (JsonObject?)Doc(n)))]);
        // The scan of ids[1] uses up the deadline, so page 0 stops before ids[2]. Each id is examined once.
        var shape = Shape(document =>
        {
            if (document.Id == ids[1])
            {
                fixture.Time.Advance(ShapeSnapshots.PageDeadline);
            }

            return true;
        });

        var snapshot = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        var (pages, nextIds) = await PagesAsync(fixture.Snapshots, fixture.Store, shape.Handle, snapshot.Offset);
        await Assert.That(pages.Count).IsEqualTo(2);
        await Assert.That(nextIds[0]).IsEqualTo(ids[1]);
        await Assert.That((await PageOpsAsync(pages[1])).Length).IsEqualTo(3);
        await AssertMatchesAsync(await OpsAsync(pages, shape, snapshot.Offset), model);
    }

    // {"n":"<0xFF>"} is invalid UTF-8; {"n": is malformed JSON. Both are stored uncaptured over a valid HTTP write.
    [Test]
    [Arguments("7B226E223A22FF227D")]
    [Arguments("7B226E223A")]
    public async Task A_damaged_stored_document_fails_the_seal_and_publishes_nothing(string damaged)
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        var ids = Ids(2);
        _ = await WriteAsync(api, Model(), (ids[0], Doc(0)), (ids[1], Doc(1)));
        await RawWriteAsync(fixture.Raw, $"UPDATE \"{DocumentApi.Collection}\" SET doc = CAST(X'{damaged}' AS TEXT) WHERE id = '{ids[1]}';");
        var shape = Shape();

        await Assert.That(async () => await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None)).Throws<InvalidDataException>();
        await Assert.That(await fixture.Snapshots.NewestAsync(fixture.Store, shape.Handle, CancellationToken.None)).IsNull();
        await Assert.That(await fixture.Snapshots.ReadPageAsync(fixture.Store, shape.Handle, await CounterAsync(fixture.Raw), null, CancellationToken.None)).IsNull();
        await Assert.That(await ScalarAsync(fixture.Raw, $"SELECT COUNT(*) FROM _tb_shape_snapshot WHERE handle = '{shape.Handle}';")).IsEqualTo(0L);
    }

    [Test]
    public async Task A_snapshot_reseals_at_4096_chunks_past_its_offset_but_not_at_4095()
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        _ = await WriteAsync(api, Model(), (DocumentApi.NewId(), Doc(1)));
        var shape = Shape();
        var first = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        await Assert.That(first.Offset).IsGreaterThan(0L);

        // A chunk ending at the snapshot's offset is already covered and does not count.
        await RawWriteAsync(fixture.Raw, Chunks(shape.Handle, (0, first.Offset)));
        await RawWriteAsync(fixture.Raw, Chunks(shape.Handle, [.. Enumerable.Range(0, ShapeSnapshots.ResealChunks - 1)
            .Select(i => (first.Offset + i, first.Offset + i + 1))]));
        _ = await WriteAsync(api, Model(), (DocumentApi.NewId(), Doc(2)));

        await Assert.That(await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None)).IsEqualTo(first)
            .Because("4095 chunks past S are below the threshold, even though the head moved");

        var last = first.Offset + ShapeSnapshots.ResealChunks - 1;
        await RawWriteAsync(fixture.Raw, Chunks(shape.Handle, (last, last + 1)));
        var second = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        await Assert.That(second.Offset).IsEqualTo(await CounterAsync(fixture.Raw));
        await Assert.That(second.Offset).IsGreaterThan(first.Offset);
        await Assert.That(await fixture.Snapshots.NewestAsync(fixture.Store, shape.Handle, CancellationToken.None)).IsEqualTo(second);
    }

    [Test]
    public async Task A_snapshot_reseals_one_hour_after_sealing_and_the_older_one_stays_servable()
    {
        await using var api = await DocumentApi.StartAsync();
        await using var fixture = await Fixture.OpenAsync(api);
        _ = await WriteAsync(api, Model(), (DocumentApi.NewId(), Doc(1)));
        var shape = Shape();
        var first = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        _ = await WriteAsync(api, Model(), (DocumentApi.NewId(), Doc(2)));

        fixture.Time.Advance(ShapeSnapshots.ResealAge - TimeSpan.FromMilliseconds(1));
        await Assert.That(await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None)).IsEqualTo(first);

        fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
        var second = await fixture.Snapshots.SealIfDueAsync(fixture.Store, shape, CancellationToken.None);
        await Assert.That(second.Offset).IsGreaterThan(first.Offset);
        await Assert.That(second.SealedAt).IsEqualTo((Start + ShapeSnapshots.ResealAge).ToUnixTimeMilliseconds());
        using var older = await fixture.Snapshots.ReadPageAsync(fixture.Store, shape.Handle, first.Offset, null, CancellationToken.None);
        await Assert.That(older).IsNotNull();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const long Limit = 512L * 1024 * 1024;
        private readonly Meter _meter;

        private Fixture(Meter meter, MemoryBudget memory, SqliteDatabaseStore raw, IDatabaseStore store, SqliteShapeSnapshotTransactions transactions)
        {
            _meter = meter;
            Memory = memory;
            Raw = raw;
            Store = store;
            Transactions = transactions;
            Snapshots = new ShapeSnapshots(memory, Time, transactions);
        }

        public FakeTimeProvider Time { get; } = new(Start);

        public MemoryBudget Memory { get; }

        /// <summary>The engine's store, for SQL inspection only.</summary>
        public SqliteDatabaseStore Raw { get; }

        /// <summary>The decorated production store every snapshot call goes through.</summary>
        public IDatabaseStore Store { get; }

        public SqliteShapeSnapshotTransactions Transactions { get; }

        public ShapeSnapshots Snapshots { get; }

        public static async Task<Fixture> OpenAsync(DocumentApi api, long limit = Limit)
        {
            var tenant = await api.TenantOfAsync();
            var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
            var raw = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(tenant, CancellationToken.None))!;
            var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
            var meter = new Meter("TerraBase.Fast.ShapeSnapshots");
            return new Fixture(meter, new MemoryBudget(limit, meter), raw, store, new SqliteShapeSnapshotTransactions(engine));
        }

        public ValueTask DisposeAsync()
        {
            _meter.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static ShapeSnapshotDefinition Shape(Func<StoredDocument, bool>? predicate = null) => new(
        "h1-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
        DocumentApi.Collection, RandomNumberGenerator.GetBytes(16), predicate);

    private static string[] Ids(int count) => [.. Enumerable.Range(0, count).Select(_ => DocumentApi.NewId()).Order(StringComparer.Ordinal)];

    private static JsonObject Doc(int n, string? text = null) => new() { ["n"] = n, ["text"] = text ?? $"value {n}" };

    private static SortedDictionary<string, JsonObject> Model(IDictionary<string, JsonObject>? from = null) =>
        from is null ? new(StringComparer.Ordinal) : new(from.ToDictionary(pair => pair.Key, pair => (JsonObject)pair.Value.DeepClone()), StringComparer.Ordinal);

    private static string Id(JsonElement op) => op.GetProperty("id").GetString()!;

    /// <summary>Writes in transactions of 250 operations (null deletes), applies them to the model and returns each result's version.</summary>
    private static async Task<Dictionary<string, string>> WriteAsync(
        DocumentApi api, SortedDictionary<string, JsonObject> model, params (string Id, JsonObject? Document)[] writes)
    {
        Dictionary<string, string> versions = new(StringComparer.Ordinal);
        foreach (var batch in writes.Chunk(250))
        {
            object[] ops = [.. batch.Select(write => write.Document is { } document
                ? (object)new { op = "put", collection = DocumentApi.Collection, id = write.Id, document }
                : new { op = "delete", collection = DocumentApi.Collection, id = write.Id })];
            var response = await api.TransactAsync(ops, CancellationToken.None);
            await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
            var results = response.Json.GetProperty("results");
            for (var i = 0; i < batch.Length; i++)
            {
                var (id, document) = batch[i];
                if (document is null)
                {
                    _ = model.Remove(id);
                }
                else
                {
                    model[id] = (JsonObject)document.DeepClone();
                    versions[id] = results[i].GetProperty("version").GetString()!;
                }
            }
        }

        return versions;
    }

    /// <summary>Follows a published snapshot's continuation chain; every page must be served.</summary>
    private static async Task<(List<byte[]> Pages, List<string?> NextIds)> PagesAsync(ShapeSnapshots snapshots, IDatabaseStore store, string handle, long offset)
    {
        List<byte[]> pages = [];
        List<string?> nextIds = [];
        string? after = null;
        do
        {
            using var page = await snapshots.ReadPageAsync(store, handle, offset, after, CancellationToken.None)
                ?? throw new InvalidOperationException($"snapshot {offset} has no page after {after ?? "the start"}");
            pages.Add(page.Body.ToArray());
            nextIds.Add(page.NextId);
            after = page.NextId;
        }
        while (after is not null);
        return (pages, nextIds);
    }

    /// <summary>Checks every page against section 6 and the chain's contiguity, and returns the ops in page order.</summary>
    private static async Task<List<JsonElement>> OpsAsync(IReadOnlyList<byte[]> pages, ShapeSnapshotDefinition shape, long offset)
    {
        var at = offset.ToString(CultureInfo.InvariantCulture);
        var expectedOffset = ShapeSnapshots.PageOffset(offset, null);
        List<JsonElement> ops = [];
        for (var i = 0; i < pages.Count; i++)
        {
            var body = pages[i];
            var last = i == pages.Count - 1;
            var pageOps = await PageOpsAsync(body);
            await Assert.That(body.Length).IsLessThanOrEqualTo(pageOps.Length > 1 ? ShapeSnapshots.PageBytes : ShapeSnapshots.SingletonBytes)
                .Because("pages hold 1 MiB; a single document may travel alone up to 8 MiB");
            await Assert.That(body[0]).IsEqualTo((byte)'{').Because("UTF-8 without a BOM");
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            await Assert.That(JsonSerializer.SerializeToUtf8Bytes(root).SequenceEqual(body)).IsTrue().Because("bodies are compact");
            await Assert.That(string.Join(',', root.EnumerateObject().Select(property => property.Name))).IsEqualTo(string.Join(',', Envelope));
            await Assert.That(root.GetProperty("shape_id").GetString()).IsEqualTo(shape.ShapeId);
            await Assert.That(root.GetProperty("handle").GetString()).IsEqualTo(shape.Handle);
            await Assert.That(root.GetProperty("offset").GetString()).IsEqualTo(expectedOffset);
            var next = root.GetProperty("next_offset").GetString()!;
            if (last)
            {
                await Assert.That(next).IsEqualTo(at);
            }
            else
            {
                await Assert.That(next).StartsWith($"snap.{at}.");
                await Assert.That(next).IsNotEqualTo(expectedOffset);
            }

            expectedOffset = next;
            await Assert.That(root.GetProperty("covered_offset").GetString()).IsEqualTo(at);
            await Assert.That(root.GetProperty("values_at").GetString()).IsEqualTo(at);
            await Assert.That(root.GetProperty("up_to_date").GetBoolean()).IsFalse().Because("only a fresh tail vouches for the head");
            var cursor = Base64Url.DecodeFromChars(root.GetProperty("cursor_resume").GetString());
            await Assert.That(cursor[0]).IsEqualTo((byte)1);
            await Assert.That(cursor.AsSpan(1, 16).SequenceEqual(shape.ResumeScope.Span)).IsTrue();
            var change = root.GetProperty("changes").EnumerateArray().Single();
            await Assert.That(string.Join(',', change.EnumerateObject().Select(property => property.Name))).IsEqualTo("offset,ops");
            await Assert.That(change.GetProperty("offset").GetString()).IsEqualTo(at);
            foreach (var op in change.GetProperty("ops").EnumerateArray())
            {
                await Assert.That(string.Join(',', op.EnumerateObject().Select(property => property.Name))).IsEqualTo("op,id,version,document");
                await Assert.That(op.GetProperty("op").GetString()).IsEqualTo("upsert");
                await Assert.That(op.GetProperty("version").GetString()).StartsWith("tbv1-");
                ops.Add(op.Clone());
            }
        }

        return ops;
    }

    private static async Task<JsonElement[]> PageOpsAsync(byte[] page)
    {
        using var json = JsonDocument.Parse(page);
        var ops = json.RootElement.GetProperty("changes")[0].GetProperty("ops").EnumerateArray().Select(op => op.Clone()).ToArray();
        await Assert.That(ops.Length).IsLessThanOrEqualTo(ShapeSnapshots.PageDocuments);
        return ops;
    }

    /// <summary>The ops are the model in ordinal id order, each id once, with the stored document unchanged.</summary>
    private static async Task AssertMatchesAsync(List<JsonElement> ops, SortedDictionary<string, JsonObject> model)
    {
        await Assert.That(string.Join(',', ops.Select(Id))).IsEqualTo(string.Join(',', model.Keys));
        foreach (var op in ops)
        {
            await Assert.That(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(model[Id(op)]), op.GetProperty("document"))).IsTrue();
        }
    }

    /// <summary>The live ids of the shape's collection after replaying feed rows with seq at or below S, in ordinal order.</summary>
    private static Task<string[]> FeedLiveAsync(SqliteDatabaseStore store, long offset) => store.WithWriterAsync(session =>
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = $"SELECT id, deleted FROM _tb_feed WHERE collection = '{DocumentApi.Collection}' AND seq <= {offset} ORDER BY seq;";
        using var reader = command.ExecuteReader();
        Dictionary<string, bool> live = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            live[reader.GetString(0)] = reader.GetInt64(1) == 0;
        }

        return live.Where(pair => pair.Value).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
    }, "a test replaying the feed", CancellationToken.None);

    private static Task<long> CounterAsync(SqliteDatabaseStore store) =>
        ScalarAsync(store, "SELECT seq FROM _tb_feed_sequence WHERE id = 1;");

    private static Task<long> ScalarAsync(SqliteDatabaseStore store, string sql) => store.WithWriterAsync(session =>
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }, "a test inspecting shape rows", CancellationToken.None);

    // The same capture-off pragma SuspendCaptureAsync sends; each statement commits on its own.
    private static Task<bool> RawWriteAsync(SqliteDatabaseStore store, params string[] statements) => store.WithWriterAsync(session =>
    {
        using (var off = session.Connection.CreateCommand())
        {
            off.CommandText = SqliteSession.CaptureOff;
            off.ExecuteNonQuery();
        }

        try
        {
            foreach (var sql in statements)
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            using var on = session.Connection.CreateCommand();
            on.CommandText = SqliteSession.CaptureOn;
            on.ExecuteNonQuery();
        }

        return true;
    }, "a test writing shape chunks uncaptured", CancellationToken.None);

    // Multi-row inserts of at most 500 rows per statement, as raw uncaptured chunk rows the B1 tests also write.
    private static string[] Chunks(string handle, params (long Offset, long End)[] chunks) =>
        [.. chunks.Chunk(500).Select(batch => "INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES "
            + string.Join(", ", batch.Select(chunk => FormattableString.Invariant($"('{handle}', {chunk.Offset}, {chunk.End}, X'7B7D', 1791849600000)"))) + ";")];
}
