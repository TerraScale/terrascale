using System.Text;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using ReplicaCluster = TerraBase.Fast.Replication.PeerStreamTests.Cluster;
using Machine = TerraBase.Fast.Replication.PeerStreamTests.Machine;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The incarnation contract (docs/terrabase/replication-incarnations.md): change ids and peer acks belong to one database
/// file. A copy or restore is a new file whose ids restart, so an ack of the old file must neither move the new file's
/// cursor nor release its change log, even when its change id is at or below the new head (the old bound check passes it).
/// Digest and bucket reads answer only for the incarnation they name.
/// </summary>
public sealed class IncarnationContractTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Test]
    public async Task A_copy_refuses_an_ack_of_its_sources_incarnation_and_keeps_its_change_log()
    {
        var clock = new PeerStreamTests.ShiftedTime();
        await using var cluster = await ReplicaCluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        for (var write = 0; write < 5; write++)
        {
            var id = EntityIds.New().ToString();
            await gru.WriteAsync(async transaction =>
            {
                _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
                _ = await transaction.PutAsync("items", id, Json("""{"n":1}"""), CancellationToken.None);
            });
        }

        var source = await gru.IncarnationAsync();
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, await gru.HeadAsync(cluster.Database))).IsTrue();
        var stale = await gru.AckedAsync(cluster.Database, sjc.Node);
        await Assert.That(stale).IsGreaterThan(0);

        var engine = gru.Api.Server.Services.GetRequiredService<IStorageEngine>();
        var copy = cluster.Database with { DatabaseId = EntityIds.New().ToString() };
        await Assert.That(await engine.CopyDatabaseAsync(cluster.Database, copy, CancellationToken.None)).IsTrue();
        var incarnation = await gru.IncarnationAsync(copy);
        await Assert.That(incarnation).IsNotEqualTo(source);
        await WriteUntilAsync(engine, gru.Groups, copy, incarnation, "items", stale);

        // sjc acknowledged nothing of the copy; every copy row is past the 60 s hold, so any ack that counted would cut it.
        await Assert.That(await gru.AckedAsync(copy, sjc.Node)).IsEqualTo(0);
        clock.Offset = TimeSpan.FromMinutes(2);
        await AssertRefusedAsync(gru.Groups, gru.Api.Server.Services, copy, source, incarnation, sjc.Node, stale, await gru.HeadAsync(copy));
    }

    [Test]
    public async Task A_restore_refuses_an_ack_of_the_backed_up_incarnation_and_keeps_its_change_log()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var host = await BackupShippingHost.StartAsync();
        for (var index = 0; index < 8; index++)
        {
            await host.PutAsync("people", "document-" + index, "text " + index, token);
        }

        var tenant = new TenantContext(host.Database.Organization, PlanTier.Enterprise, host.Database.Project, host.Database.Id);
        var services = host.Api.Server.Services;
        var groups = services.GetRequiredService<IChangeGroupSource>();
        var peer = ReplicaId.Of(2, 7, 0);
        var source = (await groups.AckedAsync(tenant, peer, token))!.Value.Incarnation;
        var stale = (await groups.HeadAsync(tenant, source, token))!.Value;
        await Assert.That(stale).IsGreaterThan(0);
        await Assert.That(await groups.AckAsync(tenant, source, peer, stale, default, token)).IsTrue();

        await host.ShipAsync(TimeSpan.FromSeconds(10), token);
        await host.RestoreAsync(host.Status().LastShippedAt!.Value, "recovered", token);
        var restored = tenant with { DatabaseId = await host.DatabaseIdAsync("recovered", token) };
        var cursor = (await groups.AckedAsync(restored, peer, token))!.Value;
        await Assert.That(cursor.Incarnation).IsNotEqualTo(source);
        await Assert.That(cursor.Acked).IsEqualTo(0);
        await Assert.That(cursor.RequiresRefill).IsTrue().Because("the restored peer row names the backed-up file");
        var engine = services.GetRequiredService<IStorageEngine>();
        await WriteUntilAsync(engine, groups, restored, cursor.Incarnation, "people", stale);

        // Turso stamps rows with the wall clock, the host's time is frozen earlier: age every row past the 60 s hold.
        var store = (SqliteDatabaseStore)(await services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(restored, token))!;
        _ = await store.WithWriterAsync(writer =>
        {
            using var update = writer.Connection.CreateCommand();
            update.CommandText = "UPDATE turso_cdc SET change_time = $time;";
            update.Parameters.AddWithValue("$time", (host.Time.GetUtcNow() - TimeSpan.FromHours(1)).ToUnixTimeSeconds());
            return update.ExecuteNonQuery();
        }, "a test ageing the change log", token);
        var head = (await groups.HeadAsync(restored, cursor.Incarnation, token))!.Value;
        await AssertRefusedAsync(groups, services, restored, source, cursor.Incarnation, peer, stale, head);
    }

    // A stream pins a source copy, then reads its digests and buckets by that pin: another copy of the same database (here
    // sjc's, on gru) is refused, over gRPC and at the source; the right pin answers with its head and echoes the pin.
    [Test]
    public async Task Digest_and_bucket_reads_answer_only_for_the_incarnation_they_name()
    {
        await using var cluster = await ReplicaCluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, await gru.HeadAsync(cluster.Database))).IsTrue();
        var (pin, other) = (await gru.IncarnationAsync(), await sjc.IncarnationAsync());
        await Assert.That(pin).IsNotEqualTo(other);
        var bucket = SqliteStorageTransaction.Bucket("items", id);

        using var channel = PeerChannel.Create(gru.Api.Server.BaseAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var discovered = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(cluster.Database, null) });
        await Assert.That(discovered.Held).IsTrue();
        await Assert.That(new Ulid(discovered.Incarnation.Span)).IsEqualTo(pin);

        var head = await gru.HeadAsync(cluster.Database);
        var pinned = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(cluster.Database, pin) });
        await Assert.That(await gru.HeadAsync(cluster.Database)).IsEqualTo(head).Because("the file stays quiet while its digest is read");
        await Assert.That(pinned.Held).IsTrue();
        await Assert.That(new Ulid(pinned.Incarnation.Span)).IsEqualTo(pin);
        await Assert.That(pinned.HeadChangeId).IsEqualTo((ulong)head).Because("the reply's head is the pinned copy's, read with its digests");
        await Assert.That(pinned.Buckets.ToByteArray()).IsEquivalentTo(discovered.Buckets.ToByteArray());

        var refused = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(cluster.Database, other) });
        await Assert.That(refused.Held).IsFalse();
        await Assert.That(refused.Buckets.IsEmpty).IsTrue();

        // Every page of the pinned copy echoes the pin and holds the document.
        List<string> ids = [];
        using (var call = client.ReadBucket(new BucketRequest { Database = ChangeGroups.Wire(cluster.Database, pin), Buckets = { (uint)bucket } }))
        {
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                await Assert.That(new Ulid(call.ResponseStream.Current.Incarnation.Span)).IsEqualTo(pin);
                ids.AddRange(StateList.Parser.ParseFrom(ApplicationEnvelope.Decode(call.ResponseStream.Current.States.Span, ReplicationProtocol.MaxApplicationBytes))
                    .States.Where(state => state.Collection == "items").Select(state => state.Id));
            }
        }

        await Assert.That(ids).Contains(id);
        await Assert.That(await ReadBucketErrorAsync(client, ChangeGroups.Wire(cluster.Database, other), bucket)).IsEqualTo(StatusCode.FailedPrecondition);
        await Assert.That(await ReadBucketErrorAsync(client, ChangeGroups.Wire(cluster.Database, null), bucket)).IsEqualTo(StatusCode.InvalidArgument);

        // The same at the source.
        var states = gru.Api.Server.Services.GetRequiredService<IReplicaStates>();
        var digest = await states.DigestAsync(cluster.Database, pin, CancellationToken.None);
        await Assert.That(digest).IsNotNull();
        await Assert.That((digest!.Incarnation, digest.Head)).IsEqualTo((pin, head));
        await Assert.That(await states.DigestAsync(cluster.Database, other, CancellationToken.None)).IsNull();
        await Assert.That(await states.ReadBucketsAsync(cluster.Database, other, [bucket], null, 4096, int.MaxValue, CancellationToken.None)).IsNull();
        var page = await states.ReadBucketsAsync(cluster.Database, pin, [bucket], null, 4096, int.MaxValue, CancellationToken.None);
        await Assert.That(page is { } found && found.States.Any(state => state.Collection == "items" && state.Id == id)).IsTrue();
    }

    // A frame of the push stream must name its sender's incarnation: without one the receiver cannot scope its change ids,
    // and ends the stream as for a nameless database.
    [Test]
    public async Task A_batch_without_its_senders_incarnation_ends_the_stream()
    {
        await using var cluster = await ReplicaCluster.StartAsync("gru");
        var gru = cluster["gru"];
        await gru.WriteAsync(async transaction => _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None));
        using var channel = PeerChannel.Create(gru.Api.Server.BaseAddress, PeerTls.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
        var origin = ReplicaId.Of(1, 7, 0);
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        var stamp = new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16);
        var at = new EntryVersion(stamp, origin);
        using var body = System.Text.Json.JsonDocument.Parse("""{"n":1}""");
        var delta = new ChangeDelta("items", EntityIds.New().ToString(), stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(stamp));
        var encoded = ChangeGroups.Encode([new OriginGroup(1, [delta])]);
        await call.RequestStream.WriteAsync(new PushFrame
        {
            Batch = new Batch { Database = ChangeGroups.Wire(cluster.Database, Ulid.NewUlid()), LastChangeId = 1, Groups = encoded },
        });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Ack.Refused).IsFalse();
        await call.RequestStream.WriteAsync(new PushFrame
        {
            Batch = new Batch { Database = ChangeGroups.Wire(cluster.Database, null), LastChangeId = 1, Groups = encoded },
        });
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(CancellationToken.None));
        await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(error.Status.Detail).Contains("incarnation");
    }

    // The writer is the authority: an ack started while the writer is held is refused after that writer renews the file.
    // The cached identity still names the old one, so only a check after the lease refuses it. The control without the
    // renewal records.
    [Test]
    public async Task An_ack_started_while_the_writer_is_held_is_refused_after_the_file_is_renewed()
    {
        await using var cluster = await ReplicaCluster.StartAsync(cdc: true, "gru");
        var gru = cluster["gru"];
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", EntityIds.New().ToString(), Json("""{"n":1}"""), CancellationToken.None);
        });
        var incarnation = await gru.IncarnationAsync();
        var peer = ReplicaId.Of(2, 7, 0);
        await Assert.That(await gru.AckedAsync(cluster.Database, peer)).IsEqualTo(0);
        var store = (SqliteDatabaseStore)(await gru.Api.Server.Services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
        var first = await gru.HeadAsync(cluster.Database);
        await Assert.That(await AckBehindWriterAsync(gru, store, incarnation, peer, first, renewal: null)).IsTrue();
        await Assert.That(await PeerAsync(store, peer)).IsEqualTo((first, incarnation.ToString()));

        await gru.WriteAsync(async transaction => _ = await transaction.PutAsync("items", EntityIds.New().ToString(), Json("""{"n":2}"""), CancellationToken.None));
        var second = await gru.HeadAsync(cluster.Database);
        await Assert.That(second).IsGreaterThan(first);
        var renewal = Ulid.NewUlid();
        try
        {
            await Assert.That(await AckBehindWriterAsync(gru, store, incarnation, peer, second, renewal)).IsFalse();
            await Assert.That(store.Incarnation).IsEqualTo(incarnation).Because("the cached identity never saw the renewal");
            await Assert.That(await PeerAsync(store, peer)).IsEqualTo((first, incarnation.ToString())).Because("a refused ack writes nothing");
        }
        finally
        {
            // Back to the incarnation the open store caches, so the shutdown sees one file.
            _ = await store.WithWriterAsync(session => Renew(session, incarnation), "a test restoring the incarnation", CancellationToken.None);
        }
    }

    // The ack of the old incarnation at or below the new head is refused and changes nothing; a peer row persisted with the
    // old incarnation counts as 0 for retention and the cursor; then the same ack of the new incarnation records and cuts.
    private static async Task AssertRefusedAsync(
        IChangeGroupSource groups, IServiceProvider services, TenantContext database, Ulid old, Ulid incarnation, ReplicaId peer, long stale, long head)
    {
        await Assert.That(stale).IsLessThanOrEqualTo(head).Because("the old ack names a change id the new file has, which a bound check passes");
        var engine = services.GetRequiredService<SqliteStorageEngine>();
        var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
        // The peer row came with the copy and names the old file: the peer must refill this one, on every read until it acks.
        var refill = new SourceCursor(incarnation, 0) { RequiresRefill = true };
        await Assert.That(await groups.AckedAsync(database, peer, CancellationToken.None)).IsEqualTo(refill);
        var before = await LogAsync(store);
        await Assert.That(await groups.AckedAsync(database, peer, CancellationToken.None)).IsEqualTo(refill);
        await Assert.That(await LogAsync(store)).IsEqualTo(before).Because("reading the cursor keeps the old scope that requires the refill");
        var retains = await groups.RetainsAsync(database, incarnation, 0, CancellationToken.None);
        var written = (await groups.ReadAsync(database, incarnation, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None))!.Groups;
        var all = written.Count;
        var key = written.SelectMany(group => group.Deltas).Select(delta => (delta.Collection, delta.Id)).First(pair => pair.Collection is "items" or "people");
        var bucket = SqliteStorageTransaction.Bucket(key.Collection, key.Id);
        var states = services.GetRequiredService<IReplicaStates>();
        var stable = new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16);

        await Assert.That(await groups.AckAsync(database, old, peer, stale, default, CancellationToken.None)).IsFalse();
        await Assert.That(await groups.RecordStableAsync(database, old, peer, stable, CancellationToken.None)).IsFalse();
        await Assert.That(await states.ForgetAsync(database, old, [key], new Hlc(ulong.MaxValue), CancellationToken.None)).IsNull()
            .Because("a refill's forget pinned to the old copy deletes nothing of this one");
        await Assert.That(await states.DigestAsync(database, old, CancellationToken.None)).IsNull();
        await Assert.That(await states.ReadBucketsAsync(database, old, [bucket], null, 4096, int.MaxValue, CancellationToken.None)).IsNull();
        await Assert.That(await LogAsync(store)).IsEqualTo(before).Because("refused acks and stable reports write no peer row and cut no change log row");
        var page = await states.ReadBucketsAsync(database, incarnation, [bucket], null, 4096, int.MaxValue, CancellationToken.None);
        await Assert.That(page is { } kept && kept.States.Any(state => (state.Collection, state.Id) == key)).IsTrue().Because("the refused forget kept the document");
        await Assert.That((await states.DigestAsync(database, incarnation, CancellationToken.None))?.Incarnation).IsEqualTo(incarnation);
        await Assert.That(await groups.AckedAsync(database, peer, CancellationToken.None)).IsEqualTo(refill);
        await Assert.That(await groups.RetainsAsync(database, incarnation, 0, CancellationToken.None)).IsEqualTo(retains);
        await Assert.That((await groups.ReadAsync(database, incarnation, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None))!.Groups.Count).IsEqualTo(all);
        await Assert.That(await groups.AckAsync(database, Ulid.NewUlid(), peer, stale, default, CancellationToken.None)).IsFalse();
        await Assert.That(await groups.ReadAsync(database, old, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)).IsNull();
        await Assert.That(await groups.HeadAsync(database, old, CancellationToken.None)).IsNull();
        await Assert.That(await groups.RetainsAsync(database, old, 0, CancellationToken.None)).IsNull();

        // A row of the old incarnation (a copied or restored ack) with an id below the head holds every row, as 0 does.
        var seededRows = await store.WithWriterAsync(session =>
        {
            using var seed = session.Connection.CreateCommand();
            seed.CommandText = "UPDATE _tb_peers SET acked = $acked, incarnation = $incarnation WHERE node = $node;";
            seed.Parameters.AddWithValue("$acked", stale);
            seed.Parameters.AddWithValue("$incarnation", old.ToString());
            seed.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
            return seed.ExecuteNonQuery();
        }, "a test seeding an old incarnation's ack", CancellationToken.None);
        await Assert.That(seededRows).IsEqualTo(1);
        // Seeding is captured too; the trim must keep every row present after that write.
        var seededBefore = await LogAsync(store);
        await services.GetRequiredService<TursoChangeGroupSource>().TrimAsync(store, CancellationToken.None);
        var seeded = await LogAsync(store);
        await Assert.That((seeded.Rows, seeded.Oldest, seeded.Newest)).IsEqualTo((seededBefore.Rows, seededBefore.Oldest, seededBefore.Newest))
            .Because("retention counts another incarnation's ack as 0");
        await Assert.That(await groups.AckedAsync(database, peer, CancellationToken.None)).IsEqualTo(refill);

        await Assert.That(await groups.AckAsync(database, incarnation, peer, stale, default, CancellationToken.None)).IsTrue();
        await Assert.That(await groups.AckedAsync(database, peer, CancellationToken.None)).IsEqualTo(new SourceCursor(incarnation, stale))
            .Because("the accepted ack of this incarnation scopes the row to it and clears the refill requirement");
        await Assert.That((await LogAsync(store)).Oldest).IsGreaterThan(before.Oldest).Because("the same ack of this incarnation releases the rows it covers");
    }

    // Local writes to `collection` of the new file until its own change ids pass `head`.
    private static async Task WriteUntilAsync(IStorageEngine engine, IChangeGroupSource groups, TenantContext database, Ulid incarnation, string collection, long head)
    {
        var store = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
        for (var write = 0; write < 500 && (await groups.HeadAsync(database, incarnation, CancellationToken.None) ?? throw new InvalidOperationException("the copy refused its own incarnation")) <= head; write++)
        {
            var id = EntityIds.New().ToString();
            _ = await store.WriteAsync(async (transaction, token) =>
            {
                _ = await transaction.PutAsync(collection, id, Json("""{"text":"after"}"""), token);
                return true;
            }, static _ => true, CancellationToken.None);
        }
    }

    // Holds the database's writer, starts the ack, renews the file's incarnation under the writer when asked,
    // then frees the writer and returns the ack's answer.
    private static async Task<bool> AckBehindWriterAsync(Machine machine, SqliteDatabaseStore store, Ulid incarnation, ReplicaId peer, long changeId, Ulid? renewal)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = store.WithWriterAsync(session =>
        {
            entered.SetResult();
            release.Wait(deadline.Token);
            return renewal is { } renewed ? Renew(session, renewed) : 0;
        }, "a test holding the writer", deadline.Token);
        Task<bool>? ack = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            ack = machine.Groups.AckAsync(machine.Cluster.Database, incarnation, peer, changeId, default, deadline.Token).AsTask();
        }
        finally
        {
            release.Set();
            _ = await holder;
        }

        return await ack!.WaitAsync(deadline.Token);
    }

    private static int Renew(SqliteSession session, Ulid incarnation)
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = "UPDATE commit_offset SET incarnation = $incarnation WHERE id = 1;";
        command.Parameters.AddWithValue("$incarnation", incarnation.ToString());
        return command.ExecuteNonQuery();
    }

    private static Task<(long Acked, string Incarnation)> PeerAsync(SqliteDatabaseStore store, ReplicaId peer) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT acked, incarnation FROM _tb_peers WHERE node = $node;";
            command.Parameters.AddWithValue("$node", unchecked((long)peer.Value));
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : (-1L, "");
        }, "a test reading a peer's ack", CancellationToken.None);

    // The change log's extent and every peer row, read on the writer.
    private static Task<Log> LogAsync(SqliteDatabaseStore store) =>
        store.WithWriterAsync(session =>
        {
            using var extent = session.Connection.CreateCommand();
            extent.CommandText = "SELECT count(*), coalesce(min(change_id), 0), coalesce(max(change_id), 0) FROM turso_cdc;";
            using var rows = extent.ExecuteReader();
            _ = rows.Read();
            var (count, oldest, newest) = (rows.GetInt64(0), rows.GetInt64(1), rows.GetInt64(2));
            using var peers = session.Connection.CreateCommand();
            peers.CommandText = "SELECT node, applied, acked, stable, seen, incarnation FROM _tb_peers ORDER BY node;";
            using var reader = peers.ExecuteReader();
            var text = new StringBuilder();
            while (reader.Read())
            {
                _ = text.Append($"{reader.GetInt64(0)}:{reader.GetInt64(1)}:{reader.GetInt64(2)}:{reader.GetInt64(3)}:{reader.GetInt64(4)}:{reader.GetString(5)};");
            }

            return new Log(count, oldest, newest, text.ToString());
        }, "a test reading the change log", CancellationToken.None);

    private static async Task<StatusCode> ReadBucketErrorAsync(Features.Replication.V1.Replication.ReplicationClient client, Features.Replication.V1.Database database, int bucket)
    {
        using var call = client.ReadBucket(new BucketRequest { Database = database, Buckets = { (uint)bucket } });
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
            }
        });
        return error!.StatusCode;
    }

    private static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);

    private sealed record Log(long Rows, long Oldest, long Newest, string Peers);
}
