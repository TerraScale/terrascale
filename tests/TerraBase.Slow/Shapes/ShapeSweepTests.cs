using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Shapes;

public sealed class ShapeSweepTests
{
    [Test]
    public async Task A_collection_dropped_before_a_restart_is_swept_with_an_empty_registry_and_no_later_request()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-shape-sweep-").FullName;
        try
        {
            string unfiltered;
            string filtered;
            await using (var first = await Node.StartAsync(directory))
            {
                await first.PutAsync(3);
                unfiltered = await first.BootstrapAsync();
                filtered = await first.BootstrapAsync("""{"eq":["/n",1]}""");

                await Assert.That(await first.RowsAsync(unfiltered)).IsEqualTo(new Rows(0, 1, 0, 1));
                await Assert.That(await first.RowsAsync(filtered)).IsEqualTo(new Rows(0, 1, 0, 1));
                await first.DropAsync();
            }

            await using var restarted = await Node.StartAsync(directory);
            var pass = await restarted.PassAsync();
            await Assert.That(pass.Retired).IsEqualTo(2);
            await Assert.That(await restarted.RowsAsync(unfiltered)).IsEqualTo(Rows.None);
            await Assert.That(await restarted.RowsAsync(filtered)).IsEqualTo(Rows.None);
            await Assert.That(await restarted.ShapeRowsAsync()).IsEqualTo(0L);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Evicted_filtered_shapes_stay_until_a_drop_and_recreate_and_the_new_lineage_is_kept()
    {
        await using var node = await Node.StartAsync();
        await node.PutAsync(2);
        List<string> handles = [];

        for (var i = 0; i <= ShapeFilterRegistry.MaxPerDatabase; i++)
        {
            handles.Add(await node.BootstrapAsync($$"""{"eq":["/n",{{i}}]}"""));
        }

        var current = await node.PassAsync();
        await Assert.That(current.Current).IsEqualTo(handles.Count).Because("eviction does not change a filtered handle's lineage");
        await Assert.That(current.Deleted).IsEqualTo(0L);
        await Assert.That(await node.RowsAsync(handles[0])).IsEqualTo(new Rows(0, 1, 0, 1));

        await node.DropAsync();
        await node.CreateAsync();
        await node.PutAsync(1);
        var recreated = await node.BootstrapAsync($$"""{"eq":["/n",0]}""");
        await Assert.That(handles).DoesNotContain(recreated);
        var swept = await node.PassAsync();
        await Assert.That(swept.Retired).IsEqualTo(handles.Count);
        await Assert.That(swept.Current).IsEqualTo(1);
        foreach (var handle in handles)
        {
            await Assert.That(await node.RowsAsync(handle)).IsEqualTo(Rows.None);
        }

        await Assert.That(await node.RowsAsync(recreated)).IsEqualTo(new Rows(0, 1, 0, 1));
    }

    [Test]
    public async Task More_durable_retired_handles_than_the_retirement_queue_holds_leave_in_bounded_passes_without_the_registry()
    {
        await using var node = await Node.StartAsync();
        await node.PutAsync(1);
        var sealedHandle = (await node.SealAsync("")).Handle;
        var count = ShapeRegistry.MaxRetired + 1;

        await node.WriteAsync(connection =>
        {
            for (var i = 1; i < count; i++)
            {
                var handle = string.Create(CultureInfo.InvariantCulture, $"h1-{i:x16}");
                Chunk(connection, handle, 0);
                Execute(connection, """
                    INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at)
                    SELECT $lineage, 0, 0, body, 0 FROM _tb_shape_chunk WHERE handle = $sealed;
                    """, ("$lineage", "~lineage:" + handle), ("$sealed", "~lineage:" + sealedHandle));
            }
        });

        await Assert.That(await node.ShapeRowsAsync()).IsEqualTo(2L * count);
        await node.DropAsync();
        List<long> deleted = [];
        long remaining;
        do
        {
            deleted.Add((await node.PassAsync()).Deleted);
            remaining = await node.ShapeRowsAsync();
        }
        while (remaining > 0 && deleted.Count < 4);

        await Assert.That(remaining).IsEqualTo(0L);

        await Assert.That(deleted).IsEquivalentTo([ShapeRetention.BatchRows, ShapeRetention.BatchRows, 2L * count - 2 * ShapeRetention.BatchRows]);
    }

    [Test]
    public async Task A_current_handle_gets_ordinary_retention_with_no_request_and_keeps_its_newest_complete_snapshot()
    {
        await using var node = await Node.StartAsync();
        await node.PutAsync(3);
        var handle = (await node.SealAsync("")).Handle;
        long end = 0;
        await node.WriteAsync(connection =>
        {

            Execute(connection, "UPDATE _tb_shape_chunk SET sealed_at = 0 WHERE handle = $h;", ("$h", handle));
            end = Scalar(connection, "SELECT end_offset FROM _tb_shape_chunk WHERE handle = $h;", ("$h", handle));
            Snapshot(connection, handle, 0, 0);
            Snapshot(connection, handle, 1, 0, next: "a");
            Snapshot(connection, handle, 1, 1);
            Snapshot(connection, handle, end + 10, 0, next: "b");
        });
        await Assert.That(end).IsGreaterThan(1L);

        var pass = await node.PassAsync();
        await Assert.That(pass.Current).IsEqualTo(1);
        await Assert.That(pass.Deleted).IsEqualTo(2L);
        await Assert.That(await node.RowsAsync(handle)).IsEqualTo(new Rows(0, 3, 1, 1));
        await Assert.That(await node.ReadAsync(connection => Scalar(connection, "SELECT end_offset FROM _tb_shape_chunk WHERE handle = $h;",
            ("$h", "~floor:" + handle)))).IsEqualTo(end);
        await Assert.That(await node.ReadAsync(connection => Scalar(connection,
            "SELECT count(*) FROM _tb_shape_snapshot WHERE handle = $h AND offset = 1;", ("$h", handle)))).IsEqualTo(2L)
            .Because("the newest complete snapshot keeps every page while a newer one is still being sealed");

        var idle = await node.PassAsync();
        await Assert.That(idle.Current).IsEqualTo(1);
        await Assert.That(idle.Deleted).IsEqualTo(0L);
    }

    [Test]
    public async Task A_damaged_handle_is_protected_and_the_sweep_moves_past_it()
    {
        await using var node = await Node.StartAsync();
        await node.PutAsync(1);
        string[] handles = [(await node.SealAsync("")).Handle, (await node.SealAsync("""{"eq":["/n",1]}""")).Handle];
        Array.Sort(handles, StringComparer.Ordinal);
        var (damaged, healthy) = (handles[0], handles[1]);
        var floor = (await node.SealAsync("""{"eq":["/n",2]}""")).Handle;

        await node.WriteAsync(connection =>
        {
            Execute(connection, "UPDATE _tb_shape_chunk SET body = $b WHERE handle = $h;", ("$h", damaged), ("$b", new byte[ShapeRetention.BatchBytes + 1]));
            Execute(connection, "DELETE FROM _tb_shape_chunk WHERE handle = $h;", ("$h", floor));
            Execute(connection, "INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($h, 0, 5, $b, 0);",
                ("$h", "~floor:" + floor), ("$b", new byte[ShapeRetention.BatchBytes + 1]));
        });
        await node.DropAsync();

        var pass = await node.PassAsync();
        await Assert.That(pass.Retired).IsEqualTo(1);
        await Assert.That(pass.Protected).IsEqualTo(2);
        await Assert.That(pass.Failed).IsEqualTo(0);
        await Assert.That(await node.RowsAsync(healthy)).IsEqualTo(Rows.None);
        await Assert.That(await node.RowsAsync(damaged)).IsEqualTo(new Rows(1, 0, 0, 1));
        await Assert.That(await node.RowsAsync(floor)).IsEqualTo(new Rows(0, 0, 1, 1));

        var next = await node.PassAsync();
        await Assert.That(next.Protected).IsEqualTo(2);
        await Assert.That(next.Failed).IsEqualTo(0);
        await Assert.That(await node.RowsAsync(damaged)).IsEqualTo(new Rows(1, 0, 0, 1));
        await Assert.That(await node.RowsAsync(floor)).IsEqualTo(new Rows(0, 0, 1, 1));
    }

    [Test]
    public async Task Missing_damaged_oversized_and_unknown_lineage_is_protected_and_counted()
    {
        await using var node = await Node.StartAsync();
        await node.PutAsync(1);
        var missing = (await node.SealAsync("")).Handle;
        var damaged = (await node.SealAsync("""{"eq":["/n",1]}""")).Handle;
        var oversized = (await node.SealAsync("""{"eq":["/n",2]}""")).Handle;
        var retired = (await node.SealAsync("""{"eq":["/n",3]}""")).Handle;
        await node.WriteAsync(connection =>
        {
            Execute(connection, "DELETE FROM _tb_shape_chunk WHERE handle = $h;", ("$h", "~lineage:" + missing));
            Execute(connection, "UPDATE _tb_shape_chunk SET body = $b WHERE handle = $h;", ("$h", "~lineage:" + damaged), ("$b", "2 not a record"u8.ToArray()));
            Execute(connection, "UPDATE _tb_shape_chunk SET body = $b WHERE handle = $h;", ("$h", "~lineage:" + oversized), ("$b", new byte[257]));
            Chunk(connection, "zz-not-a-handle", 0);
            Chunk(connection, "~floor:" + new string('f', 200), 0);
        });
        await node.DropAsync();

        var pass = await node.PassAsync();
        await Assert.That(pass.Retired).IsEqualTo(1);
        await Assert.That(pass.Protected).IsEqualTo(5);
        await Assert.That(await node.RowsAsync(retired)).IsEqualTo(Rows.None);
        await Assert.That(await node.RowsAsync(missing)).IsEqualTo(new Rows(1, 0, 0, 0));
        await Assert.That(await node.RowsAsync(damaged)).IsEqualTo(new Rows(1, 0, 0, 1));
        await Assert.That(await node.RowsAsync(oversized)).IsEqualTo(new Rows(1, 0, 0, 1));
        await Assert.That(await node.RowsAsync("zz-not-a-handle")).IsEqualTo(new Rows(1, 0, 0, 0));
        var metrics = await node.ScrapeAsync();
        await Assert.That(metrics).Contains("terrabase_shape_sweep_handles_total{otel_scope_name=\"TerraScale.TerraBase\",result=\"malformed\"} 2");
        await Assert.That(metrics).Contains("terrabase_shape_sweep_handles_total{otel_scope_name=\"TerraScale.TerraBase\",result=\"unclassified\"} 1");
        await Assert.That(metrics).Contains("terrabase_shape_sweep_handles_total{otel_scope_name=\"TerraScale.TerraBase\",result=\"unknown\"} 2");
    }

    [Test]
    public async Task Floor_only_and_lineage_only_handles_are_discovered_and_the_lineage_goes_last()
    {
        await using var node = await Node.StartAsync();
        await node.PutAsync(1);
        var floorOnly = (await node.SealAsync("")).Handle;
        var lineageOnly = (await node.SealAsync("""{"eq":["/n",1]}""")).Handle;

        await node.WriteAsync(connection =>
        {
            Execute(connection, "DELETE FROM _tb_shape_chunk WHERE handle IN ($a, $b);", ("$a", floorOnly), ("$b", lineageOnly));
            Chunk(connection, "~floor:" + floorOnly, 0, end: 5);
        });
        await Assert.That(await node.RowsAsync(floorOnly)).IsEqualTo(new Rows(0, 0, 1, 1));
        await Assert.That(await node.RowsAsync(lineageOnly)).IsEqualTo(new Rows(0, 0, 0, 1));

        var current = await node.PassAsync();
        await Assert.That(current.Current).IsEqualTo(2);
        await node.DropAsync();
        var pass = await node.PassAsync();
        await Assert.That(pass.Retired).IsEqualTo(2);
        await Assert.That(pass.Deleted).IsEqualTo(3L);
        await Assert.That(await node.ShapeRowsAsync()).IsEqualTo(0L);
    }

    [Test]
    public async Task Stale_chunk_and_snapshot_writers_cannot_republish_a_dropped_lineage()
    {
        await using var node = await Node.StartAsync();
        await node.PutAsync(2);
        var shape = await node.SealAsync("");
        var snapshot = new ShapeSnapshotDefinition(shape.Handle, shape.ShapeId, shape.Collection, shape.CollectionGeneration, shape.ResumeScope,
            shape.Incarnation);
        var transactions = node.Api.Server.Services.GetRequiredService<IShapeSnapshotTransactions>();
        var memory = node.Api.Server.Services.GetRequiredService<IMemoryBudget>();

        await using var reader = await transactions.BeginAsync(node.Store, write: false, CancellationToken.None);
        var storage = (IShapeSnapshotStorage)reader;
        var (offset, cursor) = await storage.ShapeSnapshotHeadAsync(shape.ResumeScope, node.Time.GetUtcNow(), CancellationToken.None);
        using var page = await storage.BuildShapeSnapshotPageAsync(snapshot, offset, null, cursor, memory, node.Time, CancellationToken.None);

        await node.DropAsync();
        await node.CreateAsync();
        await node.PutAsync(1);
        await Assert.That((await node.PassAsync()).Retired).IsEqualTo(1);
        await Assert.That(await node.RowsAsync(shape.Handle)).IsEqualTo(Rows.None);

        await using (var writer = await transactions.BeginAsync(node.Store, write: true, CancellationToken.None))
        {
            await Assert.That(async () => await ((IShapeSnapshotStorage)writer).InsertShapeSnapshotPageAsync(snapshot, offset, 0, page,
                node.Time.GetUtcNow().ToUnixTimeMilliseconds(), CancellationToken.None)).Throws<ShapeSealOffsetException>();
        }

        await Assert.That(async () => await node.Sealer.SealUpToHeadAsync(node.Store, shape, 0, CancellationToken.None)).Throws<ShapeSealOffsetException>();
        await Assert.That(await node.RowsAsync(shape.Handle)).IsEqualTo(Rows.None);
    }

    [Test]
    public async Task A_cancelled_batch_rolls_back_and_the_next_pass_finishes_the_drop()
    {
        await using var node = await Node.StartAsync();
        await node.PutAsync(1);
        var handle = (await node.SealAsync("")).Handle;

        await Assert.That(await node.BootstrapAsync()).IsEqualTo(handle);
        await node.DropAsync();
        var bodies = await node.ReadAsync(connection => Scalar(connection, """
            SELECT (SELECT sum(octet_length(body)) FROM _tb_shape_chunk WHERE handle IN ($h, '~lineage:' || $h))
                + (SELECT sum(octet_length(body)) FROM _tb_shape_snapshot WHERE handle = $h);
            """, ("$h", handle)));

        using var cancellation = new CancellationTokenSource();
        await using (var transaction = await node.Api.Server.Services.GetRequiredService<IShapeStorage>()
            .BeginTransactionAsync(node.Store, write: true, CancellationToken.None))
        {
            var step = await ((IShapeSweepTransaction)transaction).SweepShapeHandleAsync(handle, 0, ShapeRetention.BatchRows,
                ShapeRetention.BatchBytes, cancellation.Token);
            await Assert.That(step).IsEqualTo(new ShapeSweepStep(ShapeHandleState.Retired, 3, bodies, 0, false));
            await cancellation.CancelAsync();
            await Assert.That(async () => await transaction.CommitAsync(cancellation.Token)).Throws<OperationCanceledException>();
        }

        await Assert.That(await node.RowsAsync(handle)).IsEqualTo(new Rows(1, 1, 0, 1));
        await Assert.That((await node.PassAsync()).Retired).IsEqualTo(1);
        await Assert.That(await node.ShapeRowsAsync()).IsEqualTo(0L);
    }

    [Test]
    public async Task A_drop_charges_floor_and_lineage_bodies_and_leaves_what_does_not_fit_for_the_next_batch()
    {
        await using var node = await Node.StartAsync();
        await node.PutAsync(1);
        var handle = (await node.SealAsync("")).Handle;

        await node.WriteAsync(connection =>
        {
            Execute(connection, "UPDATE _tb_shape_chunk SET body = $b WHERE handle = $h;", ("$h", handle), ("$b", new byte[ShapeRetention.BatchBytes]));
            Chunk(connection, "~floor:" + handle, 0, end: 5);
        });
        var lineage = await node.ReadAsync(connection => Scalar(connection, "SELECT octet_length(body) FROM _tb_shape_chunk WHERE handle = $h;",
            ("$h", "~lineage:" + handle)));
        await node.DropAsync();

        async Task<ShapeSweepStep> StepAsync(long maxBytes, bool commit)
        {
            await using var transaction = await node.Api.Server.Services.GetRequiredService<IShapeStorage>()
                .BeginTransactionAsync(node.Store, write: true, CancellationToken.None);
            var step = await ((IShapeSweepTransaction)transaction).SweepShapeHandleAsync(handle, 0, ShapeRetention.BatchRows, maxBytes,
                CancellationToken.None);
            if (commit)
            {
                _ = await transaction.CommitAsync(CancellationToken.None);
            }

            return step;
        }

        await Assert.That(await StepAsync(ShapeRetention.BatchBytes, commit: true)).IsEqualTo(new ShapeSweepStep(ShapeHandleState.Retired, 1,
            ShapeRetention.BatchBytes, 0, true));
        await Assert.That(await node.RowsAsync(handle)).IsEqualTo(new Rows(0, 0, 1, 1));

        await Assert.That(await StepAsync(lineage, commit: false)).IsEqualTo(new ShapeSweepStep(ShapeHandleState.Retired, 1, 1, 0, true));
        await Assert.That(await node.RowsAsync(handle)).IsEqualTo(new Rows(0, 0, 1, 1));
        await Assert.That(await StepAsync(ShapeRetention.BatchBytes, commit: true)).IsEqualTo(new ShapeSweepStep(ShapeHandleState.Retired, 2,
            1 + lineage, 0, false));
        await Assert.That(await node.RowsAsync(handle)).IsEqualTo(Rows.None);
    }

    [Test]
    public async Task A_refused_memory_admission_skips_the_database_and_a_later_pass_sweeps_it()
    {
        await using var node = await Node.StartAsync();
        await node.PutAsync(1);
        var handle = await node.BootstrapAsync();
        await node.DropAsync();

        using (node.Budget.Refuse(node.Tenant))
        {
            var refused = await node.PassAsync();
            await Assert.That(refused.Refused).IsEqualTo(1);
            await Assert.That(refused.Deleted).IsEqualTo(0L);
        }

        await Assert.That(await node.RowsAsync(handle)).IsEqualTo(new Rows(0, 1, 0, 1));
        var pass = await node.PassAsync();
        await Assert.That(pass.Refused).IsEqualTo(0);
        await Assert.That(pass.Retired).IsEqualTo(1);
        await Assert.That(await node.RowsAsync(handle)).IsEqualTo(Rows.None);
    }

    private static void Chunk(SqliteConnection connection, string handle, long offset, long end = 1) =>
        Execute(connection, "INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($h, $o, $e, X'00', 0);",
            ("$h", handle), ("$o", offset), ("$e", end));

    private static void Snapshot(SqliteConnection connection, string handle, long offset, long page, string? next = null) =>
        Execute(connection, "INSERT INTO _tb_shape_snapshot (handle, offset, page, next_id, body, sealed_at) VALUES ($h, $o, $p, $n, X'00', 0);",
            ("$h", handle), ("$o", offset), ("$p", page), ("$n", next));

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private sealed record Rows(long Chunks, long Pages, long Floor, long Lineage)
    {
        public static readonly Rows None = new(0, 0, 0, 0);
    }

    private sealed class Node(DocumentApi api, FakeTimeProvider time, RefusingBudget budget, TenantContext tenant, IDatabaseStore store, TursoDatabaseStore turso)
        : IAsyncDisposable
    {
        public DocumentApi Api { get; } = api;
        public FakeTimeProvider Time { get; } = time;
        public RefusingBudget Budget { get; } = budget;
        public TenantContext Tenant { get; } = tenant;
        public IDatabaseStore Store { get; } = store;
        public IShapeSealer Sealer => Api.Server.Services.GetRequiredService<IShapeSealer>();

        public static async Task<Node> StartAsync(string? directory = null)
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            RefusingBudget? budget = null;
            var api = await DocumentApi.StartAsync(directory is null ? null : settings => settings["data-directory"] = directory, services =>
            {
                var memory = services.Last(service => service.ServiceType == typeof(IMemoryBudget));
                services.AddSingleton<IMemoryBudget>(provider => budget = new RefusingBudget((IMemoryBudget)memory.ImplementationFactory!(provider)));
            }, time);
            try
            {
                var tenant = await api.TenantOfAsync();
                var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None)
                    ?? throw new InvalidOperationException("the database is missing");
                var turso = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
                _ = api.Server.Services.GetRequiredService<IMemoryBudget>();
                return new Node(api, time, budget!, tenant, store, turso);
            }
            catch
            {
                await api.DisposeAsync();
                throw;
            }
        }

        public async Task PutAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                (await Api.PutAsync(DocumentApi.NewId(), new { n = i })).Status.ShouldBe(HttpStatusCode.Created);
            }
        }

        public async Task DropAsync() =>
            (await Api.SendAsync(HttpMethod.Delete, $"{Api.Collections}/{DocumentApi.Collection}")).Status.ShouldBe(HttpStatusCode.OK);

        public async Task CreateAsync() =>
            (await Api.SendAsync(HttpMethod.Post, Api.Collections, new { name = DocumentApi.Collection })).Status.ShouldBe(HttpStatusCode.Created);

        public async Task<string> BootstrapAsync(string? where = null)
        {
            using var response = await Api.Client.GetAsync(new Uri($"{Api.Collections}/{DocumentApi.Collection}/shape?offset=-1"
                + (where is null ? "" : "&where=" + Uri.EscapeDataString(where)), UriKind.Relative));
            response.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
            return response.Headers.GetValues("TB-Shape-Handle").Single();
        }

        public async Task<ShapeSealDefinition> SealAsync(string canonical)
        {
            CollectionInfo collection;
            await using (var transaction = await Store.BeginTransactionAsync(write: false, CancellationToken.None))
            {
                collection = await transaction.GetCollectionAsync(DocumentApi.Collection, CancellationToken.None)
                    ?? throw new InvalidOperationException("the collection is missing");
            }

            var identity = ShapeHandle.ComputeCanonical(Store, collection, canonical);
            var shape = new ShapeSealDefinition(identity.ShapeId, identity.Handle, collection.Name, collection.Id,
                ShapeService.ResumeScope(Tenant, collection, canonical), Store.Incarnation);
            _ = await Sealer.SealUpToHeadAsync(Store, shape, 0, CancellationToken.None);
            return shape;
        }

        public async Task<ShapeSweepPass> PassAsync()
        {
            var pass = Api.Server.Services.GetRequiredService<ShapeSweeper>().NextPassAsync();
            Time.Advance(ShapeSweeper.Interval);
            return await pass;
        }

        public Task<Rows> RowsAsync(string handle) => turso.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT count(*) FROM _tb_shape_chunk WHERE handle = $h), (SELECT count(*) FROM _tb_shape_snapshot WHERE handle = $h),
                    (SELECT count(*) FROM _tb_shape_chunk WHERE handle = '~floor:' || $h), (SELECT count(*) FROM _tb_shape_chunk WHERE handle = '~lineage:' || $h);
                """;
            _ = command.Parameters.AddWithValue("$h", handle);
            using var reader = command.ExecuteReader();
            reader.Read();
            return new Rows(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }, "a test counting shape rows", CancellationToken.None);

        public Task<T> ReadAsync<T>(Func<SqliteConnection, T> read) =>
            turso.WithWriterAsync(session => read(session.Connection), "a test reading shape rows", CancellationToken.None);

        public Task<long> ShapeRowsAsync() => turso.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT count(*) FROM _tb_shape_chunk WHERE handle <> '~frontier') + (SELECT count(*) FROM _tb_shape_snapshot);
                """;
            return (long)command.ExecuteScalar()!;
        }, "a test counting shape rows", CancellationToken.None);

        public Task<bool> WriteAsync(Action<SqliteConnection> write) => turso.WithWriterAsync(session =>
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

        public Task<string> ScrapeAsync() => Api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));

        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }

    private sealed class RefusingBudget(IMemoryBudget inner) : IMemoryBudget
    {
        private TenantContext? _refused;

        public IDisposable Refuse(TenantContext tenant)
        {
            Volatile.Write(ref _refused, tenant);
            return new Lift(this);
        }

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant) =>
            lane == MemoryLane.Bulk && Volatile.Read(ref _refused) is { } refused && tenant?.DatabaseId == refused.DatabaseId
                ? null : inner.TryReserve(lane, bytes, tenant);

        private sealed class Lift(RefusingBudget budget) : IDisposable
        {
            public void Dispose() => Volatile.Write(ref budget._refused, null);
        }
    }
}
