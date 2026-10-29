using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Storage;

/// <summary>
/// The Turso session pool, through the running server's engine: no listener can dispose a
/// transaction twice, delete a database under an open transaction, or start one pre-cancelled.
/// </summary>
public sealed class StorageTransactionEdgeTests
{
    // Every test runs its own server and data directory, so they can share the ids.
    private static readonly TenantContext Database = Tenant(EntityIds.New().ToString());

    [Test]
    [Arguments("put")]
    [Arguments("delete")]
    [Arguments("tombstone")]
    [Arguments("createCollection")]
    [Arguments("dropCollection")]
    public async Task A_read_transaction_refuses_mutations(string operation)
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        var (engine, store) = await OpenAsync(server);
        var json = "{\"n\":1}"u8.ToArray();
        long version;
        await using (var initial = await store.BeginTransactionAsync(write: true, token))
        {
            version = await initial.PutAsync("items", "a", json, token);
            await initial.CommitAsync(token);
        }

        await using (var reader = await store.BeginTransactionAsync(write: false, token))
        {
            var error = await Assert.That(async () =>
            {
                switch (operation)
                {
                    case "put":
                        await reader.PutAsync("items", "a", "{}"u8.ToArray(), token);
                        break;
                    case "delete":
                        await reader.DeleteAsync("items", "a", token);
                        break;
                    case "tombstone":
                        await reader.TombstoneAsync("items", "a", token);
                        break;
                    case "createCollection":
                        await reader.CreateCollectionAsync(new CollectionInfo("new", 0), token);
                        break;
                    default:
                        await reader.DropCollectionAsync("items", token);
                        break;
                }
            }).Throws<InvalidOperationException>();
            await Assert.That(error!.Message).IsEqualTo("a read transaction cannot mutate database state");
            // A caller that catches the rejection cannot commit.
            await Assert.That(async () => { await reader.CommitAsync(token); }).Throws<InvalidOperationException>();
        }

        await using var check = await store.BeginTransactionAsync(write: false, token);
        var document = await check.GetAsync("items", "a", token);
        await Assert.That(document!.Version).IsEqualTo(version);
        await Assert.That(document.Json.Span.SequenceEqual(json)).IsTrue();
    }

    [Test]
    public async Task Disposing_a_transaction_twice_returns_its_session_once()
    {
        await using var server = await TerraBaseTestServer.StartAsync(services: StorageTransactionFixture.ConfigurePool);
        var (_, store) = await OpenAsync(server);
        // Creation leaves the bootstrap session idle.
        await Assert.That(store.IdleCount).IsEqualTo(1);

        var first = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await first.DisposeAsync();
        // The reader rents the session `first` returned; disposing `first` again must not return it a second time.
        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await first.DisposeAsync();
        await Assert.That(store.IdleCount).IsEqualTo(0);

        await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await writer.CreateCollectionAsync(new CollectionInfo("items", 0), CancellationToken.None);
        var version = await writer.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);
        await writer.CommitAsync(CancellationToken.None);
        await Assert.That((await reader.GetAsync("items", "a", CancellationToken.None))?.Version).IsEqualTo(version);
    }

    [Test]
    public async Task A_deleted_database_hands_out_and_keeps_no_session()
    {
        await using var server = await TerraBaseTestServer.StartAsync(services: StorageTransactionFixture.ConfigurePool);
        var (engine, store) = await OpenAsync(server);
        var open = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await (await store.BeginTransactionAsync(write: false, CancellationToken.None)).DisposeAsync();
        await Assert.That(store.IdleCount).IsEqualTo(1);

        await Assert.That(await engine.DeleteDatabaseAsync(Database, CancellationToken.None)).IsTrue();

        await Assert.That(store.IdleCount).IsEqualTo(0);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.BeginTransactionAsync(write: false, CancellationToken.None).AsTask());
        // The transaction that was open during the delete closes its session instead of pooling it.
        await open.DisposeAsync();
        await Assert.That(store.IdleCount).IsEqualTo(0);
        await Assert.That(await engine.OpenDatabaseAsync(Database, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task A_cancelled_request_takes_no_idle_session()
    {
        await using var server = await TerraBaseTestServer.StartAsync(services: StorageTransactionFixture.ConfigurePool);
        var (_, store) = await OpenAsync(server);
        await Assert.That(store.IdleCount).IsEqualTo(1);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.BeginTransactionAsync(write: true, cancelled.Token).AsTask());

        await Assert.That(store.IdleCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_disposed_transaction_refuses_every_operation()
    {
        await using var server = await TerraBaseTestServer.StartAsync(services: StorageTransactionFixture.ConfigurePool);
        var (_, store) = await OpenAsync(server);
        var stale = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await stale.DisposeAsync();
        // The reader now holds the session `stale` used; nothing `stale` does may reach it.
        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(store.IdleCount).IsEqualTo(0);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.GetAsync("items", "a", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.PutAsync("items", "a", Encoding.UTF8.GetBytes("{}"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.DeleteAsync("items", "a", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.ListAsync("items", null, 10, 1 << 20, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.CommitAsync(CancellationToken.None).AsTask());

        await Assert.That(await reader.GetAsync("items", "a", CancellationToken.None)).IsNull();
        await Assert.That((await reader.ListAsync("items", null, 10, 1 << 20, CancellationToken.None)).Documents).IsEmpty();
    }

    [Test]
    public async Task Idle_connections_stay_bounded_across_many_databases()
    {
        await using var server = await TerraBaseTestServer.StartAsync(services: services =>
        {
            StorageTransactionFixture.ConfigurePool(services);
            // Maintenance may rent an idle session; freeze its clock for exact pool/LRU/file observations.
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
        });
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var stores = new List<TursoDatabaseStore>();
        for (var i = 0; i < engine.Pool.MaxIdle + 8; i++)
        {
            var name = Database with { DatabaseId = EntityIds.New().ToString() };
            await engine.CreateDatabaseAsync(name, CancellationToken.None);
            var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(name, CancellationToken.None))!;
            await (await store.BeginTransactionAsync(write: false, CancellationToken.None)).DisposeAsync();
            stores.Add(store);
        }

        await Assert.That(engine.Pool.IdleCount).IsEqualTo(engine.Pool.MaxIdle);
        // The least recently used databases gave up their connections, the most recent kept theirs,
        // and no other database file is still open.
        await Assert.That(stores[0].IdleCount).IsEqualTo(0);
        await Assert.That(stores[^1].IdleCount).IsEqualTo(1);
        var openFiles = new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos()
            .Select(descriptor => descriptor.LinkTarget)
            .OfType<string>()
            .Where(target => target.StartsWith(server.DataDirectory + "/", StringComparison.Ordinal) && target.EndsWith(".db", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var journal = engine.IntentStore!.FilePath;
        var system = engine.SystemStore!.FilePath;
        await Assert.That(openFiles).Contains(journal);
        await Assert.That(openFiles).Contains(system);
        await Assert.That(openFiles.Except([journal, system])).IsEquivalentTo(stores.Where(store => store.IdleCount > 0).Select(store => store.FilePath))
            .Because("only pooled tenant files and the two fixed node-local stores may remain open");

        // A database whose connection was closed opens a new one when it is used again.
        await using var again = await stores[0].BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await again.GetAsync("items", "a", CancellationToken.None)).IsNull();
    }

    // The node-local intent journal is never replicated or backed up: recording and retiring intents captures nothing, while
    // the tenant database's own write is captured.

    [Test]
    public async Task A_shut_down_engine_opens_no_database()
    {
        await using var server = await TerraBaseTestServer.StartAsync(services: StorageTransactionFixture.ConfigurePool);
        var (engine, _) = await OpenAsync(server);
        await engine.OpenSystemDatabaseAsync(CancellationToken.None);

        // The server disposes the engine again when it stops; the second call is a no-op.
        await engine.DisposeAsync();

        // Even a database that was opened, and so cached, before the shutdown.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.OpenDatabaseAsync(Database, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.CreateDatabaseAsync(Database, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.OpenSystemDatabaseAsync(CancellationToken.None).AsTask());
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task A_write_that_fails_leaves_a_transaction_that_cannot_commit(bool put, bool tombstone)
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (_, store) = await OpenAsync(server);
        long version;
        await using (var seed = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            version = await seed.PutAsync("items", "max", Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);
            if (tombstone)
            {
                version = (await seed.TombstoneAsync("items", "max", CancellationToken.None))!.Value;
            }

            await seed.CommitAsync(CancellationToken.None);
        }

        // Model an exhausted commit offset: the next local write cannot take one.
        var path = Path.Combine(server.DataDirectory, Database.OrganizationId, Database.ProjectId!, Database.DatabaseId + ".db");
        await using (var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false })))
        {
            await connection.OpenAsync(CancellationToken.None);
            using var exhaust = connection.CreateCommand();
            exhaust.CommandText = "UPDATE commit_offset SET value = 9223372036854775807 WHERE id = 1;";
            await exhaust.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // A caller that catches the failed write must not commit its partial state.
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await Assert.ThrowsAsync<OverflowException>(async () =>
            {
                if (put)
                {
                    await writer.PutAsync("items", "max", Encoding.UTF8.GetBytes("""{"n":2}"""), CancellationToken.None);
                }
                else
                {
                    await writer.TombstoneAsync("items", "max", CancellationToken.None);
                }
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
        }

        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await reader.GetVersionAsync("items", "max", CancellationToken.None)).IsEqualTo(new DocumentVersion(version, tombstone));
        if (!tombstone)
        {
            var document = await reader.GetAsync("items", "max", CancellationToken.None);
            await Assert.That(Encoding.UTF8.GetString(document!.Json.Span)).IsEqualTo("""{"n":1}""");
        }
    }

    // Each commit's marker names its own final CDC row, whether the marker statement is kept from an earlier transaction or
    // read fresh after this transaction's DDL (a collection created by the put).

    // The feed sequence and the commit offset are read and set under the writer: each feed row takes the next number, a
    // rolled-back savepoint gives its number back, each commit takes one offset, and an exhausted sequence refuses the write.
    [Test]
    public async Task Feed_rows_take_consecutive_numbers_and_an_exhausted_feed_sequence_commits_nothing()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (_, store) = await OpenAsync(server);
        static byte[] Json(int n)
        {
            return Encoding.UTF8.GetBytes($$"""{"n":{{n}}}""");
        }

        Task<string> ReadAsync(string sql)
        {
            return store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }, "a test reading the feed", CancellationToken.None);
        }

        var startSeq = long.Parse(await ReadAsync("SELECT seq FROM _tb_feed_sequence WHERE id = 1;"), System.Globalization.CultureInfo.InvariantCulture);
        var startOffset = long.Parse(await ReadAsync("SELECT value FROM commit_offset WHERE id = 1;"), System.Globalization.CultureInfo.InvariantCulture);
        await using (var writer = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            writer.Savepoint();
            _ = await writer.PutAsync("items", "a", Json(1), CancellationToken.None);
            await writer.FlushStatesAsync();
            writer.ReleaseSavepoint();
            writer.Savepoint();
            _ = await writer.PutAsync("items", "gone", Json(2), CancellationToken.None);
            await writer.FlushStatesAsync();
            await writer.RollbackToSavepointAsync();
            writer.Savepoint();
            _ = await writer.PutAsync("items", "b", Json(3), CancellationToken.None);
            await writer.FlushStatesAsync();
            writer.ReleaseSavepoint();
            _ = await writer.CommitAsync(CancellationToken.None);
        }

        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await writer.PutAsync("items", "c", Json(4), CancellationToken.None);
            _ = await writer.CommitAsync(CancellationToken.None);
        }

        await Assert.That(await ReadAsync("SELECT group_concat(id || ':' || (seq - " + startSeq + "), ',') FROM (SELECT id, seq FROM _tb_feed WHERE collection = 'items' ORDER BY seq);"))
            .IsEqualTo("a:1,b:2,c:3");
        await Assert.That(await ReadAsync("SELECT seq FROM _tb_feed_sequence WHERE id = 1;")).IsEqualTo((startSeq + 3).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(await ReadAsync("SELECT value FROM commit_offset WHERE id = 1;")).IsEqualTo((startOffset + 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(await ReadAsync("SELECT count(DISTINCT commit_offset) FROM _tb_feed WHERE collection = 'items';")).IsEqualTo("2");

        _ = await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "UPDATE _tb_feed_sequence SET seq = 9223372036854775807 WHERE id = 1;";
            return command.ExecuteNonQuery();
        }, "a test exhausting the feed sequence", CancellationToken.None);
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await writer.PutAsync("items", "d", Json(5), CancellationToken.None);
            await Assert.ThrowsAsync<OverflowException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
        }

        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await reader.GetVersionAsync("items", "d", CancellationToken.None)).IsNull();
        await Assert.That(await ReadAsync("SELECT value FROM commit_offset WHERE id = 1;")).IsEqualTo((startOffset + 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // A write transaction remembers the collections it found until it drops one or rolls back to a savepoint: a collection
    // the rolled-back write created, or one the transaction dropped, is looked up again and created anew by the next put.
    [Test]
    public async Task A_write_transaction_looks_up_again_a_collection_it_rolled_back_or_dropped()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (_, store) = await OpenAsync(server);
        var body = Encoding.UTF8.GetBytes("""{"n":1}""");
        await using (var seed = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await seed.PutAsync("kept", "c", body, CancellationToken.None);
            _ = await seed.CommitAsync(CancellationToken.None);
        }

        await using (var writer = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            writer.Savepoint();
            _ = await writer.PutAsync("fresh", "a", body, CancellationToken.None);
            await writer.FlushStatesAsync();
            await writer.RollbackToSavepointAsync();
            await Assert.That(await writer.GetAsync("fresh", "a", CancellationToken.None)).IsNull();
            _ = await writer.PutAsync("fresh", "b", body, CancellationToken.None);
            await Assert.That(await writer.GetAsync("kept", "c", CancellationToken.None)).IsNotNull();
            await Assert.That(await writer.DropCollectionAsync("kept", CancellationToken.None)).IsTrue();
            await Assert.That(await writer.GetAsync("kept", "c", CancellationToken.None)).IsNull();
            _ = await writer.PutAsync("kept", "d", body, CancellationToken.None);
            _ = await writer.CommitAsync(CancellationToken.None);
        }

        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await reader.GetAsync("fresh", "a", CancellationToken.None)).IsNull();
        await Assert.That(await reader.GetAsync("fresh", "b", CancellationToken.None)).IsNotNull();
        await Assert.That(await reader.GetAsync("kept", "c", CancellationToken.None)).IsNull();
        await Assert.That(await reader.GetAsync("kept", "d", CancellationToken.None)).IsNotNull();
    }

    [Test]
    public async Task Retirement_subscribers_can_enter_an_unrelated_database_lifecycle()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var database = await api.TenantOfAsync();
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("probe"))).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var probe = await api.TenantOfAsync("probe");
        var id = DocumentApi.NewId();
        await api.ReplaceFileAsync("db", path => DocumentFiles.WriteAsync(path, ("items", id, "{}")));
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var operations = new List<Task<bool>>(1);
        var entries = new List<Task>(1);
        var enteredImmediately = true;
        IDatabaseStore? retired = null;
        var retiredBeforeCallback = false;
        void EnterLifecycle()
        {
            // No blocking wait in a subscriber. Completing an absent intent takes only the lifecycle
            // semaphore, so it completes synchronously exactly when that semaphore is available. The
            // deletion's file work runs on the storage pool, so it completes later.
            var entry = engine.CompleteDatabaseIntentAsync(probe, deadline.Token).AsTask();
            enteredImmediately &= entry.IsCompletedSuccessfully;
            entries.Add(entry);
            operations.Add(engine.DeleteDatabaseAsync(probe, deadline.Token).AsTask());
        }

        void OnRetirement(object? sender, IDatabaseStore store)
        {
            if (store.Tenant == database)
            {
                retired = store;
                retiredBeforeCallback = store.IsClosed;
                EnterLifecycle();
            }
        }

        static void ThrowOnRetirement(object? sender, IDatabaseStore store)
        {
            throw new InvalidOperationException("subscriber failure");
        }
        engine.DatabaseDeleted += ThrowOnRetirement;
        engine.DatabaseDeleted += OnRetirement;
        try
        {
            (await api.GetAsync(id)).Status.ShouldBe(System.Net.HttpStatusCode.OK);
            await api.DeleteDatabaseAsync();
            await Assert.That(retired).IsNotNull();
            await Assert.That(retiredBeforeCallback).IsTrue();
            await Assert.That(await operations.Single()).IsTrue();
            await Assert.That(enteredImmediately).IsTrue();
            await Assert.That(await engine.OpenDatabaseAsync(database, deadline.Token)).IsNull();
        }
        finally
        {
            engine.DatabaseDeleted -= ThrowOnRetirement;
            engine.DatabaseDeleted -= OnRetirement;
            await Task.WhenAll(operations);
            await Task.WhenAll(entries);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancelling_after_a_scan_starts_stops_rows_and_leaves_the_connection_usable(bool afterCursor)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        const int Rows = 32;
        // Per-test time isolates the admission assertion from the periodic usage flush.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var management = await ManagementApi.StartAsync(time: time, services: StorageTransactionFixture.ConfigurePool);
        var server = management.Server;
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        var client = tenant.Data;
        using var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/pool", UriKind.Relative), null, deadline.Token);
        created.EnsureSuccessStatusCode();
        await StorageFixture.CreateCollectionAsync(tenant, "pool", "items", deadline.Token);
        for (var i = 0; i < Rows; i++)
        {
            using var written = await client.PutAsJsonAsync(
                new Uri($"/v1/projects/{tenant.ProjectId}/databases/pool/collections/items/documents/by-key?id={StorageFixture.Id($"d{i:D3}")}", UriKind.Relative), new { n = i }, deadline.Token);
            written.EnsureSuccessStatusCode();
        }

        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var database = await tenant.ContextAsync(management.Server, "pool", deadline.Token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, deadline.Token))!;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var scanned = 0;
        var observing = false;
        var session = await StorageFixture.RentReaderAsync(engine, store);
        try
        {
            // Cancel from real Turso row evaluation, after the worker started (not by request timing).
            session.Connection.CreateFunction<string, string>("observed_body", body =>
            {
                if (!observing)
                {
                    return body;
                }

                if (Interlocked.Increment(ref scanned) == 8)
                {
                    cancelled.Cancel();
                }

                return body;
            }, isDeterministic: true);
            observing = true;
        }
        finally
        {
            engine.Pool.Return(store, session);
        }

        await StorageFixture.ObserveBodiesAsync(store, "observed_body");

        // The generated body observes actual row evaluation while preserving indexed id order.
        var transaction = await store.BeginTransactionAsync(write: false, deadline.Token);
        await using (transaction)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => transaction.ListAsync(
                "items", afterCursor ? StorageFixture.Id("d000") : null, Rows, 1024 * 1024, cancelled.Token).AsTask());
            await Assert.That(scanned).IsGreaterThan(0);
            await Assert.That(scanned).IsLessThan(Rows);
            // Turso ended the transaction with the stopped scan: it refuses to read outside its snapshot.
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.GetAsync("items", StorageFixture.Id("d000"), deadline.Token).AsTask());
        }

        // The cancelled scan ended on its worker; the connection serves the next transaction.
        await using (var next = await store.BeginTransactionAsync(write: false, deadline.Token))
        {
            await Assert.That(await next.GetAsync("items", StorageFixture.Id("d000"), deadline.Token)).IsNotNull();
        }

        await Assert.That(engine.Io.Admitted).IsEqualTo(0);
        using var recovered = await client.GetAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/pool/collections/items/documents/by-key?id={StorageFixture.Id("d000")}", UriKind.Relative), deadline.Token);
        recovered.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task A_call_cancelled_while_queued_frees_its_slot_at_once()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "3";
        }, StorageTransactionFixture.ConfigurePool);
        var (_, store) = await OpenAsync(server);
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var io = engine.Io;
        await using (var seed = await store.BeginTransactionAsync(write: true, deadline.Token))
        {
            await seed.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), deadline.Token);
            await seed.CommitAsync(deadline.Token);
        }

        // Hold the only worker inside real Turso row evaluation until released, not by timing.
        using var release = new ManualResetEventSlim();
        var holding = 0;
        var session = await StorageFixture.RentReaderAsync(engine, store);
        try
        {
            session.Connection.CreateFunction<string, string>("held_body", body =>
            {
                if (Interlocked.CompareExchange(ref holding, 2, 1) == 1)
                {
                    release.Wait(TimeSpan.FromSeconds(30));
                }

                return body;
            }, isDeterministic: true);
        }
        finally
        {
            engine.Pool.Return(store, session);
        }

        await StorageFixture.ObserveBodiesAsync(store, "held_body");

        // The active transaction takes the observed session; the other one opens a second connection.
        var active = await store.BeginTransactionAsync(write: false, deadline.Token);
        await using var activeCleanup = active;
        var other = await store.BeginTransactionAsync(write: false, deadline.Token);
        await using var otherCleanup = other;
        using var cancelHeld = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task<StoredDocument?>? held = null;
        Task<IStorageTransaction>? reader = null;
        try
        {
            Volatile.Write(ref holding, 1);
            held = active.GetAsync("items", "a", cancelHeld.Token).AsTask();
            await WaitUntilAsync(() => Volatile.Read(ref holding) == 2 && io.Running == 1, deadline.Token);
            await cancelHeld.CancelAsync();
            // Cancellation cannot abandon a call already using its connection on the worker.
            await Assert.That(held.IsCompleted).IsFalse();
            // Repeated calls on an existing admission must release their queue nodes too.
            for (var i = 0; i < 8; i++)
            {
                using var cancelledCall = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var read = other.GetOffsetAsync(cancelledCall.Token).AsTask();
                await WaitUntilAsync(() => io.Queued == 1, deadline.Token);
                await cancelledCall.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => read);
                await Assert.That(io.Queued).IsEqualTo(0);
            }

            // The reader needs a new connection (both are rented): its open is queued behind the held call.
            reader = store.BeginTransactionAsync(write: false, cancel.Token).AsTask();
            await WaitUntilAsync(() => io.Admitted == 3 && io.Queued == 1, deadline.Token);

            await cancel.CancelAsync();

            // Cancelled while the thread is still held: the reader gives up its slot now, not after the held call.
            await Assert.ThrowsAsync<OperationCanceledException>(() => reader);
            await Assert.That(io.Admitted).IsEqualTo(2);
            await Assert.That(io.Running).IsEqualTo(1);
        }
        finally
        {
            release.Set();
            if (held is not null)
            {
                try
                {
                    await held;
                }
                catch (OperationCanceledException)
                {
                    // Cancelled after it finished on the worker; the connection stays usable either way.
                }
            }

            await StorageTransactionFixture.DisposeBeginAsync(reader);
        }

        await other.DisposeAsync();
        await active.DisposeAsync();
        await Assert.That(io.Admitted).IsEqualTo(0);
        // The explicit idle cap retains both connections; the cancelled open never ran.
        await Assert.That(store.IdleCount).IsEqualTo(2);
        var recovered = await store.BeginTransactionAsync(write: false, deadline.Token);
        await using var recoveredCleanup = recovered;
        await Assert.That(await recovered.GetOffsetAsync(deadline.Token)).IsEqualTo(1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static TenantContext Tenant(string database) => new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), database);

    private static async Task<(IStorageEngine Engine, TursoDatabaseStore Store)> OpenAsync(TerraBaseTestServer server)
    {
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        await engine.CreateDatabaseAsync(Database, CancellationToken.None);
        return (engine, (TursoDatabaseStore)(await engine.OpenDatabaseAsync(Database, CancellationToken.None))!);
    }
}
