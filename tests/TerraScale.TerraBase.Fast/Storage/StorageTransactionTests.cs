using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Engine.Sqlite;
using TerraScale.TerraBase.Fast.Documents;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Storage;

/// <summary>
/// The SQLite session pool, through the running server's engine: no listener can dispose a
/// transaction twice, delete a database under an open transaction, or start one pre-cancelled.
/// </summary>
public sealed class StorageTransactionTests
{
    // Every test runs its own server and data directory, so they can share the ids.
    private static readonly TenantContext Database = Tenant(Guid.NewGuid().ToString());

    [Test]
    [Arguments("put", false)]
    [Arguments("put", true)]
    [Arguments("delete", false)]
    [Arguments("tombstone", false)]
    [Arguments("drop", false)]
    [Arguments("missingDelete", false)]
    [Arguments("missingTombstone", false)]
    [Arguments("missingDrop", false)]
    [Arguments("emptyDrop", false)]
    public async Task Mixing_local_document_mutations_and_replication_is_rollback_only_in_either_order(string operation, bool losingApply)
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(server, "pool");
        using var client = tls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:read", "documents:write"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using (var response = await client.PutAsync(new Uri(tenant.Path("pool"), UriKind.Relative), null, token))
        {
            response.EnsureSuccessStatusCode();
        }

        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var id = Guid.CreateVersion7().ToString("D");
        var json = "{\"n\":1}"u8.ToArray();
        var origin = Guid.NewGuid().ToByteArray(bigEndian: true);
        foreach (var store in new[]
        {
            (await engine.OpenDatabaseAsync(tenant.Database("pool"), token))!,
            await engine.OpenSystemDatabaseAsync(token),
        })
        {
            // _system exercises the same rule with capture disabled.
            long version;
            long sequence;
            CollectionInfo? items;
            CollectionInfo? empty;
            await using (var initial = await store.BeginTransactionAsync(write: true, token))
            {
                await initial.CreateCollectionAsync(new CollectionInfo("items", 0), token);
                await initial.CreateCollectionAsync(new CollectionInfo("empty", 0), token);
                version = await initial.PutAsync("items", id, json, token);
                items = await initial.GetCollectionAsync("items", token);
                empty = await initial.GetCollectionAsync("empty", token);
                await initial.CommitAsync(token);
            }

            await using (var read = await store.BeginTransactionAsync(write: false, token))
            {
                sequence = await read.GetLastChangeSequenceAsync(token);
            }

            foreach (var applyFirst in new[] { false, true })
            {
                await using (var transaction = await store.BeginTransactionAsync(write: true, token))
                {
                    await transaction.CreateCollectionAsync(new CollectionInfo("rolled-back", 0), token);
                    await transaction.SetAppliedSequenceAsync(origin, 1, token);
                    var change = new DocumentChange("items", id, losingApply ? version : version + 10,
                        losingApply ? "{\"n\":0}"u8.ToArray() : "{}"u8.ToArray());
                    if (applyFirst)
                    {
                        await Assert.That(await transaction.ApplyAsync(change, token)).IsEqualTo(!losingApply);
                    }
                    else
                    {
                        await LocalAsync();
                    }

                    var error = await Assert.That(async () =>
                    {
                        if (applyFirst)
                        {
                            await LocalAsync();
                        }
                        else
                        {
                            await transaction.ApplyAsync(change, token);
                        }
                    }).Throws<InvalidOperationException>();
                    await Assert.That(error!.Message).IsEqualTo("a transaction cannot mix local document mutations and replicated changes");
                    // Catching the mixed call cannot allow a subsequent put or a partial commit.
                    await Assert.That(async () => { await transaction.PutAsync("items", id, "{}"u8.ToArray(), token); }).Throws<InvalidOperationException>();
                    await Assert.That(async () => { await transaction.CommitAsync(token); }).Throws<InvalidOperationException>();

                    async Task LocalAsync()
                    {
                        switch (operation)
                        {
                            case "put":
                                await transaction.PutAsync("items", id, "{}"u8.ToArray(), token);
                                break;
                            case "delete":
                            case "missingDelete":
                                await transaction.DeleteAsync("items", operation == "delete" ? id : "missing", token);
                                break;
                            case "tombstone":
                            case "missingTombstone":
                                await transaction.TombstoneAsync("items", operation == "tombstone" ? id : "missing", token);
                                break;
                            default:
                                await transaction.DropCollectionAsync(operation == "drop" ? "items" : operation == "emptyDrop" ? "empty" : "missing", token);
                                break;
                        }
                    }
                }

                await using var read = await store.BeginTransactionAsync(write: false, token);
                var document = await read.GetAsync("items", id, token);
                await Assert.That(document!.Version).IsEqualTo(version);
                await Assert.That(document.Json.Span.SequenceEqual(json)).IsTrue();
                await Assert.That(await read.GetCollectionAsync("items", token)).IsEqualTo(items);
                await Assert.That(await read.GetCollectionAsync("empty", token)).IsEqualTo(empty);
                await Assert.That(await read.GetCollectionAsync("rolled-back", token)).IsNull();
                await Assert.That(await read.GetLastChangeSequenceAsync(token)).IsEqualTo(sequence);
                await Assert.That(await read.GetAppliedSequenceAsync(origin, token)).IsEqualTo(0);
                await Assert.That(await read.CommitAsync(token)).IsEqualTo(version);
            }
        }

        using var visible = await client.GetAsync(new Uri($"{tenant.Path("pool")}/collections/items/documents/{id}", UriKind.Relative), token);
        visible.EnsureSuccessStatusCode();
        await Assert.That(await visible.Content.ReadAsStringAsync(token)).Contains("\"n\":1");
    }

    [Test]
    public async Task SQLite_enforces_query_only_for_reads_and_resets_it_before_reusing_the_session()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (engine, store) = await OpenAsync(server);
        var session = ((SqliteStorageEngine)engine).Pool.Rent(store) ?? throw new InvalidOperationException("missing idle session");
        try
        {
            using var view = session.Connection.CreateCommand();
            // Report the native connection flag through the engine's normal document read, without
            // exposing the rented transaction's connection or depending on private implementation fields.
            view.CommandText = """
                CREATE TEMP VIEW documents AS SELECT 'items' AS collection, 'a' AS id, 1 AS version,
                    json_object('readOnly', (SELECT query_only FROM pragma_query_only)) AS body;
                """;
            await view.ExecuteNonQueryAsync();
        }
        finally
        {
            store.Return(session);
        }

        await using (var read = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(Encoding.UTF8.GetString((await read.GetAsync("items", "a", CancellationToken.None))!.Json.Span))
                .IsEqualTo("{\"readOnly\":1}");
        }

        await using var write = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await Assert.That(Encoding.UTF8.GetString((await write.GetAsync("items", "a", CancellationToken.None))!.Json.Span))
            .IsEqualTo("{\"readOnly\":0}");
    }

    [Test]
    [Arguments("put")]
    [Arguments("delete")]
    [Arguments("tombstone")]
    [Arguments("applyPut")]
    [Arguments("applyDelete")]
    [Arguments("prefix")]
    [Arguments("createCollection")]
    [Arguments("dropCollection")]
    public async Task A_read_transaction_refuses_local_and_replicated_mutations(string operation)
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var (engine, store) = await OpenAsync(server);
        var json = "{\"n\":1}"u8.ToArray();
        await using (var initial = await store.BeginTransactionAsync(write: true, token))
        {
            await initial.PutAsync("items", "a", json, token);
            await initial.CommitAsync(token);
        }

        var notifications = 0;
        void Observe(object? sender, ChangeGroupCommitted change)
        {
            Interlocked.Increment(ref notifications);
        }
        engine.ChangesCommitted += Observe;
        try
        {
            await using var reader = await store.BeginTransactionAsync(write: false, token);
            var origin = await reader.GetOriginAsync(token);
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
                    case "applyPut":
                        await reader.ApplyAsync(new DocumentChange("items", "a", 2, "{}"u8.ToArray()), token);
                        break;
                    case "applyDelete":
                        await reader.ApplyAsync(new DocumentChange("items", "a", 2, null), token);
                        break;
                    case "createCollection":
                        await reader.CreateCollectionAsync(new CollectionInfo("new", 0), token);
                        break;
                    case "dropCollection":
                        await reader.DropCollectionAsync("items", token);
                        break;
                    default:
                        await reader.SetAppliedSequenceAsync(origin, 42, token);
                        break;
                }
            }).Throws<InvalidOperationException>();
            await Assert.That(error!.Message).IsEqualTo("a read transaction cannot mutate database state");
            // A caller that catches the rejection cannot commit or change the frontier.
            await Assert.That(async () => { await reader.CommitAsync(token); }).Throws<InvalidOperationException>();
        }
        finally
        {
            engine.ChangesCommitted -= Observe;
        }

        await Assert.That(notifications).IsEqualTo(0);
        await using var check = await store.BeginTransactionAsync(write: false, token);
        var document = await check.GetAsync("items", "a", token);
        await Assert.That(document!.Version).IsEqualTo(1);
        await Assert.That(document.Json.Span.SequenceEqual(json)).IsTrue();
        await Assert.That(await check.GetLastChangeSequenceAsync(token)).IsEqualTo(1);
        await Assert.That(await check.GetAppliedSequenceAsync(await check.GetOriginAsync(token), token)).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_failed_or_cancelled_baseline_closes_the_unpublished_store_and_its_sessions(bool cancel)
    {
        using var cancelled = new CancellationTokenSource();
        SqliteStorageEngine? engine = null;
        SqliteDatabaseStore? unpublished = null;
        SqliteSession? migratedSession = null;
        var inserts = 0;
        var inject = true;
        var name = Tenant(Guid.NewGuid().ToString());
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<TerraBaseOptions>();
            engine = new SqliteStorageEngine(options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                provider.GetRequiredService<ILogger<SqliteStorageEngine>>(), options.ShutdownGrace, readinessCacheTtl: options.ReadinessCacheTtl,
                checkpoint: provider.GetRequiredService<DatabaseLifecycleCheckpoint>(), afterMigration: store =>
                {
                    if (!inject || store.Tenant?.DatabaseId != name.DatabaseId)
                    {
                        return;
                    }

                    unpublished = store;
                    migratedSession = engine!.Pool.Rent(store) ?? throw new InvalidOperationException("migration left no idle session");
                    try
                    {
                        migratedSession.Connection.CreateFunction("interrupt_seed", () =>
                        {
                            inserts++;
                            if (cancel)
                            {
                                cancelled.Cancel();
                                return 0;
                            }

                            throw new InvalidOperationException("seed insertion failed");
                        });
                        using var trigger = migratedSession.Connection.CreateCommand();
                        trigger.CommandText = "CREATE TEMP TRIGGER interrupt_seed AFTER INSERT ON changes BEGIN SELECT interrupt_seed(); END;";
                        trigger.ExecuteNonQuery();
                    }
                    finally
                    {
                        store.Return(migratedSession);
                    }
                });
            return engine;
        }));
        var directory = Directory.CreateDirectory(Path.Combine(server.DataDirectory, name.OrganizationId, name.ProjectId!)).FullName;
        var path = Path.Combine(directory, name.DatabaseId + ".db");
        await using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await legacy.OpenAsync();
            await using var create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY (collection, id)) WITHOUT ROWID;
                INSERT INTO documents VALUES ('items', 'a', 1, '{}'), ('items', 'b', 1, '{}');
                PRAGMA user_version = 1;
                """;
            await create.ExecuteNonQueryAsync();
        }

        _ = server.Services.GetRequiredService<IStorageEngine>();
        var idleBefore = engine!.Pool.IdleCount;
        if (cancel)
        {
            await Assert.That(async () => { await engine.OpenDatabaseAsync(name, cancelled.Token); }).Throws<OperationCanceledException>();
        }
        else
        {
            await Assert.That(async () => { await engine.OpenDatabaseAsync(name, CancellationToken.None); }).Throws<SqliteException>();
        }

        await Assert.That(inserts).IsEqualTo(1);
        await Assert.That(unpublished!.IsClosed).IsTrue();
        await Assert.That(unpublished.IdleCount).IsEqualTo(0);
        await Assert.That(engine.Pool.IdleCount).IsEqualTo(idleBefore);
        await Assert.That(migratedSession!.Connection.State).IsEqualTo(System.Data.ConnectionState.Closed);
        // Retrying uses a new store, completes the baseline, and keeps the original documents intact.
        inject = false;
        var retry = await engine.OpenDatabaseAsync(name, CancellationToken.None);
        await using var read = await retry!.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That((await read.ListAsync("items", null, 10, 1024, CancellationToken.None)).Documents.Count).IsEqualTo(2);
        await Assert.That(await read.GetLastChangeSequenceAsync(CancellationToken.None)).IsEqualTo(1);
    }

    [Test]
    public async Task Disposing_a_transaction_twice_returns_its_session_once()
    {
        await using var server = await TerraBaseTestServer.StartAsync(services: ConfigurePool);
        var (_, store) = await OpenAsync(server);
        // Creation leaves the migration session idle.
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
        await Assert.That(version).IsEqualTo(1);
        await Assert.That((await reader.GetAsync("items", "a", CancellationToken.None))?.Version).IsEqualTo(1);
    }

    [Test]
    public async Task A_deleted_database_hands_out_and_keeps_no_session()
    {
        await using var server = await TerraBaseTestServer.StartAsync(services: ConfigurePool);
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
        await using var server = await TerraBaseTestServer.StartAsync(services: ConfigurePool);
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
        await using var server = await TerraBaseTestServer.StartAsync(services: ConfigurePool);
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
        await using var server = await TerraBaseTestServer.StartAsync(services: ConfigurePool);
        var engine = server.Services.GetRequiredService<SqliteStorageEngine>();
        var stores = new List<SqliteDatabaseStore>();
        for (var i = 0; i < engine.Pool.MaxIdle + 8; i++)
        {
            var name = Database with { DatabaseId = Guid.NewGuid().ToString() };
            await engine.CreateDatabaseAsync(name, CancellationToken.None);
            var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(name, CancellationToken.None))!;
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
            .Where(target => target is not null && target.StartsWith(server.DataDirectory + "/", StringComparison.Ordinal) && target.EndsWith(".db", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Count();
        await Assert.That(openFiles).IsEqualTo(engine.Pool.MaxIdle);

        // A database whose connection was closed opens a new one when it is used again.
        await using var again = await stores[0].BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await again.GetAsync("items", "a", CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task A_shut_down_engine_opens_no_database()
    {
        await using var server = await TerraBaseTestServer.StartAsync(services: ConfigurePool);
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
        // Model an exhausted legacy file; such versions must no longer enter through Apply.
        var path = Path.Combine(server.DataDirectory, Database.OrganizationId, Database.ProjectId!, Database.DatabaseId + ".db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(CancellationToken.None);
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO documents (collection, id, version, body) VALUES ('items', 'max', 9223372036854775807, $body);
                UPDATE commit_offset SET value = 9223372036854775807 WHERE id = 1;
                """;
            seed.Parameters.AddWithValue("$body", tombstone ? DBNull.Value : (object)"""{"n":1}""");
            await seed.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var origin = Guid.NewGuid().ToByteArray();

        // A caller that catches an exhausted-version write must not commit its partial state or other work.
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await writer.SetAppliedSequenceAsync(origin, 1, CancellationToken.None);
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

        await using var reader = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await Assert.That(await reader.GetAppliedSequenceAsync(origin, CancellationToken.None)).IsEqualTo(0);
        var document = await reader.GetAsync("items", "max", CancellationToken.None);
        if (tombstone)
        {
            await Assert.That(document).IsNull();
            // A null read alone cannot prove the tombstone survived the failed resurrection.
            await Assert.That(await reader.ApplyAsync(new DocumentChange("items", "max", VersionClock.MaxVersion,
                Encoding.UTF8.GetBytes("{}")), CancellationToken.None)).IsFalse();
        }
        else
        {
            await Assert.That(document?.Version).IsEqualTo(long.MaxValue);
            await Assert.That(Encoding.UTF8.GetString(document!.Json.Span)).IsEqualTo("""{"n":1}""");
        }
    }

    [Test]
    public async Task A_partially_appended_change_group_leaves_a_transaction_that_cannot_commit()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (engine, store) = await OpenAsync(server);
        await using (var initial = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await initial.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);
            await initial.CommitAsync(CancellationToken.None);
        }

        var session = ((SqliteStorageEngine)engine).Pool.Rent(store) ?? throw new InvalidOperationException("the store has no idle session");
        using var cancelled = new CancellationTokenSource();
        var appended = 0;
        var returned = false;
        try
        {
            // SQLite update hooks ignore WITHOUT ROWID tables; a connection-local AFTER INSERT trigger does not.
            session.Connection.CreateFunction("cancel_change_append", () =>
            {
                if (++appended == 1)
                {
                    cancelled.Cancel();
                }

                return 0;
            });
            using (var install = session.Connection.CreateCommand())
            {
                install.CommandText = "CREATE TEMP TRIGGER cancel_change_append AFTER INSERT ON changes BEGIN SELECT cancel_change_append(); END;";
                await install.ExecuteNonQueryAsync(CancellationToken.None);
            }

            store.Return(session);
            returned = true;
            await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            await writer.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":2}"""), CancellationToken.None);
            await writer.PutAsync("items", "b", Encoding.UTF8.GetBytes("{}"), CancellationToken.None);

            await Assert.ThrowsAsync<OperationCanceledException>(() => writer.CommitAsync(cancelled.Token).AsTask());
            await Assert.That(appended).IsEqualTo(1);
            await Assert.That((await writer.ReadChangesAsync(2, CancellationToken.None))?.Count).IsEqualTo(1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
        }
        finally
        {
            try
            {
                using var remove = session.Connection.CreateCommand();
                remove.CommandText = "DROP TRIGGER IF EXISTS temp.cancel_change_append;";
                await remove.ExecuteNonQueryAsync(CancellationToken.None);
                session.Connection.CreateFunction("cancel_change_append", (Func<int>?)null);
            }
            finally
            {
                if (!returned)
                {
                    store.Return(session);
                }
            }
        }

        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var document = await reader.GetAsync("items", "a", CancellationToken.None);
        await Assert.That(document?.Version).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(document!.Json.Span)).IsEqualTo("""{"n":1}""");
        await Assert.That(await reader.GetAsync("items", "b", CancellationToken.None)).IsNull();
        var original = await reader.ReadChangesAsync(1, CancellationToken.None);
        await Assert.That(original?.Count).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(original![0].Json!.Value.Span)).IsEqualTo("""{"n":1}""");
        await Assert.That(await reader.ReadChangesAsync(2, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Publication_and_retirement_subscribers_can_enter_an_unrelated_database_lifecycle()
    {
        await using var api = await DocumentApi.StartAsync();
        var database = await api.TenantOfAsync();
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("probe"))).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var probe = await api.TenantOfAsync("probe");
        var id = DocumentApi.NewId();
        await api.ReplaceWithOlderFileAsync("db", path => DocumentFiles.WriteAsync(path, ("items", id, "{}")));
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var operations = new List<Task<bool>>(2);
        var entries = new List<Task>(2);
        var enteredImmediately = true;
        IDatabaseStore? published = null;
        IDatabaseStore? retired = null;
        var publishedBeforeCallback = false;
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

        void OnPublication(object? sender, ChangeGroupCommitted committed)
        {
            if (committed.Database.Tenant == database)
            {
                published = committed.Database;
                publishedBeforeCallback = engine.OpenDatabaseAsync(database, deadline.Token).AsTask().IsCompletedSuccessfully;
                EnterLifecycle();
            }
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

        static void ThrowOnPublication(object? sender, ChangeGroupCommitted committed)
        {
            throw new InvalidOperationException("subscriber failure");
        }

        static void ThrowOnRetirement(object? sender, IDatabaseStore store)
        {
            throw new InvalidOperationException("subscriber failure");
        }
        engine.ChangesCommitted += ThrowOnPublication;
        engine.ChangesCommitted += OnPublication;
        engine.DatabaseDeleted += ThrowOnRetirement;
        engine.DatabaseDeleted += OnRetirement;
        try
        {
            (await api.GetAsync(id)).Status.ShouldBe(System.Net.HttpStatusCode.OK);
            await Assert.That(published).IsNotNull();
            await Assert.That(publishedBeforeCallback).IsTrue();
            await Assert.That(await operations.Single()).IsTrue();
            await engine.CreateDatabaseAsync(probe, deadline.Token);
            await api.DeleteDatabaseAsync();
            await Assert.That(retired).IsSameReferenceAs(published);
            await Assert.That(retiredBeforeCallback).IsTrue();
            await Assert.That(operations.Count).IsEqualTo(2);
            await Assert.That(await operations[1]).IsTrue();
            await Assert.That(enteredImmediately).IsTrue();
            await Assert.That(await engine.OpenDatabaseAsync(database, deadline.Token)).IsNull();
        }
        finally
        {
            engine.ChangesCommitted -= ThrowOnPublication;
            engine.ChangesCommitted -= OnPublication;
            engine.DatabaseDeleted -= ThrowOnRetirement;
            engine.DatabaseDeleted -= OnRetirement;
            await Task.WhenAll(operations);
            await Task.WhenAll(entries);
        }
    }

    [Test]
    public async Task A_failing_change_subscriber_does_not_fail_a_committed_write()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (engine, store) = await OpenAsync(server);
        static void Throw(object? sender, ChangeGroupCommitted committed)
        {
            throw new InvalidOperationException("subscriber failure");
        }

        var notifications = 0;
        void Observe(object? sender, ChangeGroupCommitted committed)
        {
            notifications++;
        }

        engine.ChangesCommitted += Throw;
        engine.ChangesCommitted += Observe;
        try
        {
            await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            await writer.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);

            await writer.CommitAsync(CancellationToken.None);
        }
        finally
        {
            engine.ChangesCommitted -= Throw;
            engine.ChangesCommitted -= Observe;
        }

        await Assert.That(notifications).IsEqualTo(1);
        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That((await reader.GetAsync("items", "a", CancellationToken.None))?.Version).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancelling_after_a_scan_starts_stops_rows_and_leaves_the_connection_usable(bool afterCursor)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        const int Rows = 32;
        await using var management = await ManagementApi.StartAsync(services: ConfigurePool);
        var server = management.Server;
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        var client = tenant.Data;
        using var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/pool", UriKind.Relative), null, deadline.Token);
        created.EnsureSuccessStatusCode();
        await StorageFixture.CreateCollectionAsync(tenant, "pool", "items", deadline.Token);
        for (var i = 0; i < Rows; i++)
        {
            using var written = await client.PutAsJsonAsync(
                new Uri($"/v1/projects/{tenant.ProjectId}/databases/pool/collections/items/documents/{StorageFixture.Id($"d{i:D3}")}", UriKind.Relative), new { n = i }, deadline.Token);
            written.EnsureSuccessStatusCode();
        }

        var engine = server.Services.GetRequiredService<SqliteStorageEngine>();
        var database = await tenant.ContextAsync(management.Server, "pool", deadline.Token);
        var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(database, deadline.Token))!;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var scanned = 0;
        var observing = false;
        var session = engine.Pool.Rent(store)!;
        try
        {
            // Cancel from real SQLite row evaluation, after the worker started (not by request timing).
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
            StorageFixture.ObserveBodies(session.Connection, "observed_body");
            observing = true;
        }
        finally
        {
            engine.Pool.Return(store, session);
        }

        // The generated body observes actual row evaluation while preserving indexed id order.
        var transaction = await store.BeginTransactionAsync(write: false, deadline.Token);
        await using (transaction)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => transaction.ListAsync(
                "items", afterCursor ? StorageFixture.Id("d000") : null, Rows, 1024 * 1024, cancelled.Token).AsTask());
            await Assert.That(scanned).IsGreaterThan(0);
            await Assert.That(scanned).IsLessThan(Rows);
            // The cancelled scan ended on its worker; the same connection can safely execute again.
            await Assert.That((await transaction.GetAsync("items", StorageFixture.Id("d000"), deadline.Token))?.Version).IsEqualTo(2);
        }

        await Assert.That(engine.Io.Admitted).IsEqualTo(0);
        using var recovered = await client.GetAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/pool/collections/items/documents/{StorageFixture.Id("d000")}", UriKind.Relative), deadline.Token);
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
        }, ConfigurePool);
        var (_, store) = await OpenAsync(server);
        var engine = server.Services.GetRequiredService<SqliteStorageEngine>();
        var io = engine.Io;
        await using (var seed = await store.BeginTransactionAsync(write: true, deadline.Token))
        {
            await seed.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), deadline.Token);
            await seed.CommitAsync(deadline.Token);
        }

        // Hold the only worker inside real SQLite row evaluation until released, not by timing.
        using var release = new ManualResetEventSlim();
        var holding = 0;
        var session = engine.Pool.Rent(store)!;
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
            StorageFixture.ObserveBodies(session.Connection, "held_body");
        }
        finally
        {
            engine.Pool.Return(store, session);
        }

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

            await DisposeBeginAsync(reader);
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

    internal static void ConfigurePool(IServiceCollection services)
    {
        // Explicit limits keep pool assertions and fixture size independent of the machine's CPU count.
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<TerraBaseOptions>();
            return new SqliteStorageEngine(
                options.DataDirectory, options.StorageThreads, options.StorageQueue, options.MemoryBudget,
                provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                provider.GetRequiredService<ILogger<SqliteStorageEngine>>(), options.ShutdownGrace, maxIdle: 8, maxIdlePerDatabase: 2);
        });
    }

    internal static async Task DisposeBeginAsync(Task<IStorageTransaction>? begin)
    {
        if (begin is null)
        {
            return;
        }

        IStorageTransaction transaction;
        try
        {
            transaction = await begin;
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            // Refused BEGINs have no transaction to dispose; their assertions run before cleanup.
            return;
        }

        await transaction.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static TenantContext Tenant(string database) => new(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString(), database);

    private static async Task<(IStorageEngine Engine, SqliteDatabaseStore Store)> OpenAsync(TerraBaseTestServer server)
    {
        var engine = server.Services.GetRequiredService<SqliteStorageEngine>();
        await engine.CreateDatabaseAsync(Database, CancellationToken.None);
        return (engine, (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(Database, CancellationToken.None))!);
    }
}
