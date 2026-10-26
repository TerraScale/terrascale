using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// Peer streams between real machines over their internal listeners (docs/terrabase/multi-region-replication.md sections 7
/// to 9). A cluster either reads each machine's change groups from <c>turso_cdc</c> through the CDC tailer, as production
/// does, or from a <see cref="FakeChangeGroupSource"/>: a test then commits deltas on a machine through the engine's join, as
/// a local write would, and adds the same group to its source.
/// </summary>
public sealed class PeerStreamTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Test]
    public async Task Catalog_wake_observes_the_advanced_commit_sequence(CancellationToken cancellationToken)
    {
        await using var api = await ManagementApi.StartAsync(cancellationToken: cancellationToken);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken);
        var source = api.Server.Services.GetRequiredService<IChangeGroupSource>();
        long observed = -1;
        var armed = 0;
        using var subscription = source.Subscribe(database =>
        {
            if (database == ChangeGroups.System && Volatile.Read(ref armed) == 1)
            {
                _ = Interlocked.CompareExchange(ref observed, store.CommitSequence, -1);
            }
        });
        await using var transaction = await store.BeginTransactionAsync(write: true, cancellationToken);
        _ = await transaction.CreateCollectionAsync(new CollectionInfo("wake-order", 1), cancellationToken);
        var before = store.CommitSequence;
        Volatile.Write(ref armed, 1);
        _ = await transaction.CommitAsync(cancellationToken);
        Volatile.Write(ref armed, 0);
        await Assert.That(observed).IsGreaterThan(before);
    }

    [Test]
    public async Task Three_regions_converge_through_concurrent_reordered_and_duplicated_groups()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc", "fra");
        var (gru, sjc, fra) = (cluster["gru"], cluster["sjc"], cluster["fra"]);
        var stamp = Stamp();
        var id = EntityIds.New().ToString();

        // sjc's edit of a field commits before gru's create reaches anyone, fra edits another field and loses an older write
        // of the first, and gru ships its create twice: every machine ends with the same document.
        await sjc.CommitAsync(Set(sjc, stamp + 2, id, "a", "\"sjc\""));
        await gru.CommitAsync(
            Metadata(gru, stamp, "items"),
            Put(gru, stamp, id, """{"a":"gru","b":"gru","n":1}"""));
        await fra.CommitAsync(Set(fra, stamp + 1, id, "a", "\"fra\""), Set(fra, stamp + 3, id, "b", "\"fra\""));
        await gru.CommitAsync(Put(gru, stamp, id, """{"a":"gru","b":"gru","n":1}"""));

        foreach (var machine in cluster.Machines)
        {
            await Assert.That(await machine.UntilAsync(id, """{"a":"sjc","b":"fra","n":1}""")).IsTrue().Because(machine.Region);
        }

        // The collection came with its metadata document: the same identity everywhere.
        var collections = await Task.WhenAll(cluster.Machines.Select(machine => machine.CollectionIdAsync("items")));
        await Assert.That(collections.Distinct().Count()).IsEqualTo(1);

        // Every stream reports, nothing is behind, so each region's summary is ok.
        const string Ok = """{"region":"gru","status":"ok","maxLagSeconds":0,"peers":[{"region":"fra","status":"ok","lagSeconds":0},{"region":"sjc","status":"ok","lagSeconds":0}]}""";
        await Assert.That(await gru.HealthAsync(Ok)).IsEqualTo(Ok);
    }

    [Test]
    public async Task Production_senders_authenticate_every_peer_request_with_no_unmatched_credential()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (fromGru, fromSjc) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", fromGru, Json("""{"from":"gru"}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(fromGru, """{"from":"gru"}""")).IsTrue();
        await sjc.WriteAsync(async transaction =>
        {
            _ = await transaction.PutAsync("items", fromSjc, Json("""{"from":"sjc"}"""), CancellationToken.None);
        });
        await Assert.That(await gru.UntilAsync(fromSjc, """{"from":"sjc"}""")).IsTrue();

        // Each machine's push to the other crossed the credential check: gru received sjc's stream and sjc received gru's.
        foreach (var machine in cluster.Machines)
        {
            await AssertAuthenticatedAsync(machine);
        }
    }

    [Test]
    public async Task Senders_count_the_delta_batches_and_repair_pages_their_receivers_count()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var stamp = Stamp();
        var id = EntityIds.New().ToString();
        await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, id, """{"n":1}"""));
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();
        // gru counts each batch as it writes it, sjc once it applies it: every applied batch was sent, by the same measure.
        await TerraBaseTestServer.EventuallyAsync(async _ =>
        {
            var received = await ReplicationBytesAsync(sjc, "gru", "delta:received");
            return received > 0 && await ReplicationBytesAsync(gru, "sjc", "delta:sent") >= received;
        }, timeout: Deadline);

        // A document only sjc holds: gru's forced pull reads its bucket, and sjc counts the pages it answers with.
        using (var body = JsonDocument.Parse("""{"only":"sjc"}"""))
        {
            var state = DocumentState.Bottom.Put(new EntryVersion(new Hlc(stamp + 1), ReplicaId.Of(99, 1, 0)), body.RootElement);
            await Assert.That(await sjc.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RepairAsync(cluster.Database,
                await sjc.IncarnationAsync(), [new RepairState("items", EntityIds.New().ToString(), state)], token)).IsEqualTo(1);
        }

        var peer = (await cluster.Members.ListAsync(token)).Single(member => member.Name == sjc.Name);
        await Assert.That(await gru.AntiEntropy.PullAsync(cluster.Database, peer, token)).IsNotNull();
        var pages = await ReplicationBytesAsync(sjc, "gru", "anti_entropy:sent");
        // gru also counts the digest reply, which only the requester knows a differing round from.
        await Assert.That(pages).IsGreaterThan(0);
        await Assert.That(await ReplicationBytesAsync(gru, "sjc", "anti_entropy:received")).IsGreaterThan(pages);
    }

    // A machine's replication byte readings of the cluster database with a peer region, by kind and direction.
    private static async Task<long> ReplicationBytesAsync(Machine machine, string peer, string kind) =>
        await machine.Api.Server.Services.GetRequiredService<UsageMeter>().GetObservationsAsync(machine.Cluster.Database,
            new UsageWindow(UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1), CancellationToken.None) is UsageCounts counts
            ? counts.Lifetime.Meters?.Values.Where(reading => reading.Dimension.Meter == "replication_bytes" && reading.Dimension.Region == machine.Region
                && reading.Dimension.PeerRegion == peer && reading.Dimension.Kind == kind).Sum(reading => reading.Count) ?? 0
            : 0;

    [Test]
    public async Task A_restarted_machine_catches_up_from_its_last_ack()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var stamp = Stamp();
        var id = EntityIds.New().ToString();
        await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, id, """{"n":1}"""));
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();
        // sjc acks only after its commit, so seeing the document there does not mean gru has recorded the ack yet.
        using (var deadline = new CancellationTokenSource(Deadline))
        {
            await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, 1, deadline.Token);
        }

        await Assert.That(gru.Source.AckedBy(cluster.Database, sjc.Node)).IsEqualTo(1);

        // Written while sjc is down: gru keeps the groups past sjc's ack and ships them once sjc is back.
        await sjc.StopAsync();
        await gru.CommitAsync(Set(gru, stamp + 1, id, "n", "2"));
        await gru.CommitAsync(Set(gru, stamp + 2, id, "m", "3"));
        await sjc.StartAsync();

        await Assert.That(await sjc.UntilAsync(id, """{"m":3,"n":2}""")).IsTrue();
        using (var deadline = new CancellationTokenSource(Deadline))
        {
            await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, 3, deadline.Token);
        }

        await Assert.That(gru.Source.AckedBy(cluster.Database, sjc.Node)).IsEqualTo(3);
    }

    [Test]
    public async Task A_write_after_an_empty_snapshot_ships_without_another_write()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: false, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var stamp = Stamp();
        var id = EntityIds.New().ToString();
        var drained = NextEmptyReadAsync(gru.Source, cluster.Database);
        await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, id, """{"n":1}"""));
        await drained.WaitAsync(Deadline);
        clock.FreezeTimestamp();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gru.Source.AfterNextEmptyRead(cluster.Database, async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await gru.CommitAsync(Set(gru, stamp + 2, id, "n", "3"));
            committed.SetResult();
        });

        await gru.CommitAsync(Set(gru, stamp + 1, id, "n", "2"));
        await committed.Task.WaitAsync(Deadline);
        using var deadline = new CancellationTokenSource(Deadline);
        await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, 3, deadline.Token);
        await Assert.That(await sjc.ReadAsync(id)).IsEqualTo("""{"n":3}""");
        await Assert.That(gru.Source.AckedBy(cluster.Database, sjc.Node)).IsEqualTo(3);
    }

    [Test]
    public async Task Writes_to_one_database_do_not_read_a_quiet_database_before_frontier_maintenance_is_due()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: false, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var stamp = Stamp();
        var (hotId, quietId) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        var hotDrained = NextEmptyReadAsync(gru.Source, cluster.Database);
        await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, hotId, """{"n":1}"""));
        await hotDrained.WaitAsync(Deadline);
        var quiet = await gru.CreateOtherDatabaseAsync();
        _ = await sjc.CreateOtherDatabaseAsync(quiet.DatabaseId);
        var quietDrained = NextEmptyReadAsync(gru.Source, quiet);
        await gru.CommitAsync(quiet, Metadata(gru, stamp, "items"), Put(gru, stamp, quietId, """{"n":1}"""));
        await quietDrained.WaitAsync(Deadline);
        clock.FreezeTimestamp();
        var quietReads = gru.Source.ReadCount(quiet);

        using var deadline = new CancellationTokenSource(Deadline);
        for (var value = 2; value <= 3; value++)
        {
            hotDrained = NextEmptyReadAsync(gru.Source, cluster.Database);
            await gru.CommitAsync(Set(gru, stamp + (ulong)value, hotId, "n", value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await hotDrained.WaitAsync(deadline.Token);
            await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, value, deadline.Token);
        }

        await Assert.That(await sjc.ReadAsync(hotId)).IsEqualTo("""{"n":3}""");
        await Assert.That(await sjc.ReadAsync(quiet, quietId)).IsEqualTo("""{"n":1}""");
        await Assert.That(gru.Source.ReadCount(quiet)).IsEqualTo(quietReads);
    }

    [Test]
    public async Task Coalesced_writes_beyond_one_batch_drain_without_starving_another_database()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var stamp = Stamp();
        var (busyId, otherId) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gru.Source.AfterNextEmptyRead(cluster.Database, async cancellationToken =>
        {
            reading.SetResult();
            await releaseRead.Task.WaitAsync(cancellationToken);
        });
        sjc.BeforeApply = async (database, groups, cancellationToken) =>
        {
            if (database == cluster.Database && groups[^1].ChangeId > ChangeGroups.MaxGroups + 1)
            {
                _ = secondBatch.TrySetResult();
                await releaseBatch.Task.WaitAsync(cancellationToken);
            }
        };
        try
        {
            await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, busyId, """{"n":0}"""));
            await reading.Task.WaitAsync(Deadline);
            var other = await gru.CreateOtherDatabaseAsync();
            _ = await sjc.CreateOtherDatabaseAsync(other.DatabaseId);
            // The sender is inside its old empty read: all these writes leave one coalesced pending database.
            for (var value = 1; value <= ChangeGroups.MaxGroups + 1; value++)
            {
                await gru.CommitAsync(Set(gru, stamp + (ulong)value, busyId, "n", value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            await gru.CommitAsync(other, Metadata(gru, stamp, "items"), Put(gru, stamp, otherId, """{"n":1}"""));
            releaseRead.SetResult();
            await secondBatch.Task.WaitAsync(Deadline);
            using var deadline = new CancellationTokenSource(Deadline);
            await gru.Source.UntilAckedAsync(other, sjc.Node, 1, deadline.Token);
            await Assert.That(await sjc.ReadAsync(other, otherId)).IsEqualTo("""{"n":1}""");
            await Assert.That(gru.Source.AckedBy(cluster.Database, sjc.Node)).IsLessThan(ChangeGroups.MaxGroups + 2L);

            releaseBatch.SetResult();
            await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, ChangeGroups.MaxGroups + 2L, deadline.Token);
            await Assert.That(await sjc.ReadAsync(busyId)).IsEqualTo($$"""{"n":{{ChangeGroups.MaxGroups + 1}}}""");
            await Assert.That(gru.Source.AckedBy(cluster.Database, sjc.Node)).IsEqualTo(ChangeGroups.MaxGroups + 2L);
        }
        finally
        {
            sjc.BeforeApply = null;
            _ = releaseRead.TrySetResult();
            _ = releaseBatch.TrySetResult();
        }
    }

    [Test]
    public async Task Repeated_dirty_queue_overflow_does_not_restart_discovery_ahead_of_a_later_database()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: false, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var stamp = Stamp();
        var (warmId, id) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        var drained = NextEmptyReadAsync(gru.Source, cluster.Database);
        await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, warmId, """{"n":0}"""));
        await drained.WaitAsync(Deadline);
        var later = await gru.CreateOtherDatabaseAsync();
        _ = await sjc.CreateOtherDatabaseAsync(later.DatabaseId);
        drained = NextEmptyReadAsync(gru.Source, later);
        await gru.CommitAsync(later, Metadata(gru, stamp, "items"), Put(gru, stamp, id, """{"n":0}"""));
        await drained.WaitAsync(Deadline);
        using var deadline = new CancellationTokenSource(Deadline);
        await gru.Source.UntilAckedAsync(later, sjc.Node, 1, deadline.Token);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rounds = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        gru.Source.AfterNextEmptyRead(cluster.Database, async cancellationToken =>
        {
            reading.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        try
        {
            await gru.CommitAsync(Set(gru, stamp + 1, warmId, "n", "1"));
            await reading.Task.WaitAsync(Deadline);
            var before = gru.Source.ReadCount(later);
            // Valid earlier ULIDs exceed the dirty queue's capacity without creating 1,025 database files.
            var candidates = Enumerable.Range(1, 1025).Select(value => cluster.Database with
            {
                DatabaseId = value.ToString("D26", System.Globalization.CultureInfo.InvariantCulture),
            }).ToArray();
            gru.Source.Announce(candidates);
            await gru.CommitAsync(later, Set(gru, stamp + 1, id, "n", "2"));
            // The initialized later copy is overdue before any candidate receives its first discovery deadline.
            clock.AdvanceTimestamp(TimeSpan.FromSeconds(6));
            var reconciliations = 0;
            gru.Source.AfterListing = () =>
            {
                if (++reconciliations == 3)
                {
                    var reads = gru.Source.ReadCount(later);
                    gru.Source.AfterListing = null;
                    rounds.SetResult(reads);
                    return;
                }

                gru.Source.Announce(candidates);
            };
            release.SetResult();
            await Assert.That(await rounds.Task.WaitAsync(deadline.Token)).IsGreaterThan(before)
                .Because("the overdue initialized database must run before repeated discovery can restart its deadline");
            await gru.Source.UntilAckedAsync(later, sjc.Node, 2, deadline.Token);
            await Assert.That(await sjc.ReadAsync(later, id)).IsEqualTo("""{"n":2}""");
        }
        finally
        {
            gru.Source.AfterListing = null;
            _ = release.TrySetResult();
        }
    }

    private static Task NextEmptyReadAsync(FakeChangeGroupSource source, TenantContext database)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AfterNextEmptyRead(database, _ =>
        {
            read.SetResult();
            return Task.CompletedTask;
        });
        return read.Task;
    }

    [Test]
    public async Task A_malformed_batch_is_refused_for_its_database_an_unplaced_database_is_refused_and_a_nameless_batch_ends_the_stream()
    {
        await using var cluster = await Cluster.StartAsync("gru");
        var gru = cluster["gru"];
        using var channel = PeerChannel.Create(gru.Api.Server.BaseAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var origin = ReplicaId.Of(1, 7, 0);
        var other = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        using var call = client.Push();
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome.Node).IsEqualTo(gru.Node.Value);

        // A database this machine does not hold: refused, nothing applied, and the stream goes on.
        var delta = Put(origin, Stamp(), EntityIds.New().ToString(), """{"n":1}""");
        var incarnation = Ulid.NewUlid();
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = ChangeGroups.Wire(other, incarnation), LastChangeId = 1, Groups = ChangeGroups.Encode([new OriginGroup(1, [delta])]) } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Ack.Refused).IsTrue();

        // Bytes that are no LZ4 envelope of groups: refused for that database, and the stream goes on.
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = ChangeGroups.Wire(cluster.Database, incarnation), LastChangeId = 1, Groups = ByteString.CopyFromUtf8("forged") } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Ack.Refused).IsTrue();
        await Assert.That(call.ResponseStream.Current.Ack.Database).IsEqualTo(ChangeGroups.Wire(cluster.Database, incarnation));

        // A batch that names no valid database cannot be refused for one: the receiver ends the stream.
        var nameless = ChangeGroups.Wire(cluster.Database, incarnation);
        nameless.DatabaseId = "not-a-ulid";
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = nameless, LastChangeId = 1, Groups = ChangeGroups.Encode([new OriginGroup(1, [delta])]) } });
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(CancellationToken.None));
        await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task Three_regions_converge_from_local_writes_through_the_cdc_tailer()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc", "fra");
        var (gru, sjc, fra) = (cluster["gru"], cluster["sjc"], cluster["fra"]);
        var (shared, gone, sjcOwn) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());

        // gru creates the collection and two documents in one transaction; once they reached every region, sjc and fra
        // edit different fields of one of them at once, sjc writes a document of its own and fra deletes the other.
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", shared, Json("""{"a":"gru","b":"gru"}"""), CancellationToken.None);
            _ = await transaction.PutAsync("items", gone, Json("""{"n":1}"""), CancellationToken.None);
        });
        foreach (var machine in cluster.Machines)
        {
            await Assert.That(await machine.UntilAsync(gone, """{"n":1}""")).IsTrue().Because(machine.Region);
        }

        var stamp = Stamp();
        await Task.WhenAll(
            sjc.CommitAsync(Set(sjc, stamp, shared, "a", "\"sjc\"")),
            fra.CommitAsync(Set(fra, stamp, shared, "b", "\"fra\"")),
            sjc.WriteAsync(async transaction => await transaction.PutAsync("items", sjcOwn, Json("""{"s":1}"""), CancellationToken.None)),
            fra.WriteAsync(async transaction => await transaction.TombstoneAsync("items", gone, CancellationToken.None)));

        foreach (var machine in cluster.Machines)
        {
            await Assert.That(await machine.UntilAsync(shared, """{"a":"sjc","b":"fra"}""")).IsTrue().Because(machine.Region);
            await Assert.That(await machine.UntilAsync(sjcOwn, """{"s":1}""")).IsTrue().Because(machine.Region);
            await Assert.That(await machine.UntilAbsentAsync(gone)).IsTrue().Because(machine.Region);
        }

        const string Ok = """{"region":"gru","status":"ok","maxLagSeconds":0,"peers":[{"region":"fra","status":"ok","lagSeconds":0},{"region":"sjc","status":"ok","lagSeconds":0}]}""";
        await Assert.That(await gru.HealthAsync(Ok)).IsEqualTo(Ok);
    }

    [Test]
    public async Task The_cdc_tailer_ships_one_group_per_transaction_never_echoes_and_drops_what_every_peer_acked()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (first, second) = (EntityIds.New().ToString(), EntityIds.New().ToString());

        // One transaction: a document rewritten three times, then a second one. Its group holds one delta per document,
        // with the value of the last write, and the collection's metadata document.
        var store = await gru.StoreAsync();
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":1}"""), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":2}"""), CancellationToken.None);
            _ = await transaction.PutAsync("items", second, Json("""{"m":1}"""), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":3}"""), CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        var groups = await gru.GroupsAsync(cluster.Database, 0);
        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].Deltas.Select(delta => $"{delta.Collection}/{delta.Id}").Order(StringComparer.Ordinal))
            .IsEquivalentTo(new[] { $"{DocumentDelta.Collections}/items", $"items/{first}", $"items/{second}" }.Order(StringComparer.Ordinal));
        var delta = groups[0].Deltas.Single(delta => delta.Id == first);
        await Assert.That(Encoding.UTF8.GetString(delta.State.Materialize()!)).IsEqualTo("""{"n":3}""");
        await Assert.That(delta.State.MaxStamp).IsEqualTo(delta.Stamp);

        // sjc joined both documents: its own tailer finds no group to send back, though its CDC holds the joined rows.
        await Assert.That(await sjc.UntilAsync(first, """{"n":3}""")).IsTrue();
        await Assert.That(await sjc.GroupsAsync(cluster.Database, 0)).IsEmpty();

        // sjc acked the group, but gru keeps it for 60 s: a peer whose stream has not reached the database yet needs it.
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, groups[0].ChangeId)).IsTrue();
        await Assert.That((await gru.GroupsAsync(cluster.Database, 0)).Count).IsEqualTo(1);

        // Two minutes on, the next acknowledged group drops everything below its commit: nothing is left to send.
        clock.Offset = TimeSpan.FromMinutes(2);
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", second, Json("""{"m":2}"""), CancellationToken.None));
        var head = await gru.HeadAsync(cluster.Database);
        await Assert.That(await sjc.UntilAsync(second, """{"m":2}""")).IsTrue();
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, head)).IsTrue();
        await gru.TrimAsync();
        await Assert.That(await gru.GroupsAsync(cluster.Database, 0)).IsEmpty();
    }

    [Test]
    public async Task Group_commit_keeps_every_cdc_group_within_its_bounds()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru");
        var gru = cluster["gru"];
        await gru.WriteAsync(async transaction => await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None));

        // Eight queued writes of a 700 KiB document each: group commit batches them, and moves a write that would take its
        // transaction's group past 2 MiB to the next batch instead of failing it.
        var body = Json($$"""{"pad":"{{new string('x', 700 * 1024)}}"}""");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            gru.WriteAsync(async transaction => await transaction.PutAsync("items", EntityIds.New().ToString(), body, CancellationToken.None))));

        // Three 900 KiB documents in one transaction are refused: its group would pass 2 MiB.
        var store = await gru.StoreAsync();
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            var large = Json($$"""{"pad":"{{new string('y', 900 * 1024)}}"}""");
            for (var i = 0; i < 3; i++)
            {
                _ = await transaction.PutAsync("items", EntityIds.New().ToString(), large, CancellationToken.None);
            }

            await Assert.ThrowsAsync<ChangeGroupCapacityException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        List<OriginGroup> groups = [];
        for (var after = 0L; ;)
        {
            var page = await gru.GroupsAsync(cluster.Database, after);
            if (page.Count == 0)
            {
                break;
            }

            groups.AddRange(page);
            after = page[^1].ChangeId;
        }

        await Assert.That(groups.Sum(group => group.Deltas.Count)).IsEqualTo(9);
        await Assert.That(groups.Max(group => group.Deltas.Count)).IsLessThanOrEqualTo(2);
        await Assert.That(groups.Select(group => group.ChangeId)).IsOrderedBy(id => id);
    }

    [Test]
    public async Task A_restarted_sender_resumes_each_peer_from_its_persisted_ack()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();

        // Written while sjc is down, then gru restarts too: gru reads sjc's ack back from the file and ships the rest.
        await sjc.StopAsync();
        var stamp = Stamp();
        await gru.CommitAsync(Set(gru, stamp, id, "n", "2"));
        await gru.CommitAsync(Set(gru, stamp + 1, id, "m", "3"));
        await gru.StopAsync();
        await gru.StartAsync();
        await sjc.StartAsync();

        await Assert.That(await sjc.UntilAsync(id, """{"m":3,"n":2}""")).IsTrue();
    }

    [Test]
    public async Task A_peer_that_leaves_the_machine_list_loses_its_open_stream_until_it_is_listed_again()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (first, second) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(first, """{"n":1}""")).IsTrue();
        await Assert.That(await gru.UntilStreamsAsync(["sjc"])).IsTrue();

        // sjc leaves the peer directory: within a heartbeat gru closes its open stream to it.
        var member = (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "sjc");
        cluster.Members.Leave(member.Name);
        await Assert.That(await gru.UntilStreamsAsync([])).IsTrue();
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", second, Json("""{"n":2}"""), CancellationToken.None));
        await Task.Delay(TimeSpan.FromSeconds(2));
        await Assert.That(await sjc.ReadAsync(second)).IsNull();

        // Listed again, it gets a new stream, which resumes from its ack.
        cluster.Members.Join(member);
        await Assert.That(await sjc.UntilAsync(second, """{"n":2}""")).IsTrue();
    }

    [Test]
    public async Task Tail_lag_grows_while_the_only_peer_is_away_and_returns_to_zero_once_it_acknowledged()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();

        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", id, Json("""{"n":2}"""), CancellationToken.None));
        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_tail_lag_seconds", value => value >= 1)).IsTrue();

        await sjc.StartAsync();
        await Assert.That(await sjc.UntilAsync(id, """{"n":2}""")).IsTrue();
        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_tail_lag_seconds", value => value == 0)).IsTrue();
    }

    [Test]
    public async Task An_index_made_or_dropped_in_one_region_is_made_or_dropped_in_the_other()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var index = new IndexDefinition("by_email", "items", [DocumentField.TryParse("email", out var email) ? email : throw new InvalidOperationException()]);
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.CreateIndexAsync(index, CancellationToken.None);
        });
        await Assert.That(await sjc.UntilIndexesAsync(["by_email"])).IsTrue();

        await gru.WriteAsync(async transaction => await transaction.DropIndexAsync("by_email", CancellationToken.None));
        await Assert.That(await sjc.UntilIndexesAsync([])).IsTrue();
    }

    [Test]
    public async Task Unique_constraints_that_arrive_after_the_documents_they_constrain_hide_the_same_loser_everywhere()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (smaller, larger) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction => await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None));
        var deadline = DateTime.UtcNow + Deadline;
        while (await sjc.CollectionIdAsync("items") is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await Assert.That(await sjc.CollectionIdAsync("items")).IsNotNull();

        // gru stops sending to sjc. It declares email unique and writes the larger id; sjc, which has not heard of the
        // constraint, writes the smaller id with the same email, and that reaches gru, where the smaller id wins.
        var member = (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "sjc");
        cluster.Members.Leave(member.Name);
        await Assert.That(await gru.UntilStreamsAsync([])).IsTrue();
        await gru.WriteAsync(async transaction => await transaction.SetUniqueConstraintsAsync("items", [new UniqueConstraint("email", ["email"])], reindex: false, CancellationToken.None));
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", larger, Json("""{"email":"same@example.test","from":"gru"}"""), CancellationToken.None));
        await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", smaller, Json("""{"email":"same@example.test","from":"sjc"}"""), CancellationToken.None));
        await Assert.That(await gru.UntilUniqueLoserAsync(larger)).IsTrue();

        // Listed again, sjc receives the constraint after both documents: it hides the same loser.
        cluster.Members.Join(member);
        await Assert.That(await sjc.UntilUniqueLoserAsync(larger)).IsTrue();
        await Assert.That(await sjc.ReadAsync(smaller)).IsEqualTo("""{"email":"same@example.test","from":"sjc"}""");
    }

    [Test]
    public async Task Tail_send_and_ack_lag_are_separate_series_and_only_tail_lag_tracks_a_peer_that_is_gone()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();

        // Caught up: each of the three is its own series, at 0; send and ack lag per peer region.
        const string Sjc = "peer_region=\"sjc\"";
        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_send_lag_seconds", value => value == 0, Sjc)).IsTrue();
        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_ack_lag_seconds", value => value == 0, Sjc)).IsTrue();
        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_tail_lag_seconds", value => value == 0)).IsTrue();

        // sjc gone: its stream and with it its send and ack lag series go, while tail lag keeps the age of what it lacks.
        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", id, Json("""{"n":2}"""), CancellationToken.None));
        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_tail_lag_seconds", value => value >= 1)).IsTrue();
        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_send_lag_seconds", _ => false, Sjc, absent: true)).IsTrue();
    }

    [Test]
    public async Task Anti_entropy_restores_a_document_a_replica_lost_after_acknowledging_it_on_the_second_differing_round()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (kept, lost) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", kept, Json("""{"n":1}"""), CancellationToken.None);
            _ = await transaction.PutAsync("items", lost, Json("""{"n":2}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(lost, """{"n":2}""")).IsTrue();

        // sjc loses the document behind the streams' back (its rows deleted from the file): gru acknowledged nothing missing,
        // so no stream sends it again.
        await sjc.LoseAsync("items", lost);
        await Assert.That(await sjc.ReadAsync(lost)).IsNull();

        // The first round sees the bucket differ but waits a round, so in-flight streams settle; the second repairs it.
        await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await Assert.That(await sjc.ReadAsync(lost)).IsNull();
        await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await Assert.That(await sjc.ReadAsync(lost)).IsEqualTo("""{"n":2}""");
        await Assert.That(await sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");

        // A repair is never shipped back: gru's tailer finds no group of sjc's to send for it.
        await Assert.That(await sjc.GroupsAsync(cluster.Database, 0)).IsEmpty();

        // The digest rounds reached gru's listener as real anti-entropy traffic, and gru's push reached sjc's.
        await AssertAuthenticatedAsync(gru);
        await AssertAuthenticatedAsync(sjc);
    }

    [Test]
    public async Task An_idle_stream_acknowledges_up_to_the_head_past_rows_without_groups_of_its_own()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();

        // Frontier bookkeeping grows the CDC head without adding a local group for the peer to acknowledge.
        var before = await gru.HeadAsync(cluster.Database);
        var stamp = gru.Api.Server.Services.GetRequiredService<LocalReplica>().Clock.Tick();
        await gru.Api.Server.Services.GetRequiredService<IChangeGroupSink>()
            .RecordFrontierAsync(cluster.Database, sjc.Node, stamp, default, null, CancellationToken.None);
        var head = await gru.HeadAsync(cluster.Database);
        await Assert.That(head).IsGreaterThan(before);
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, head)).IsTrue();
    }

    [Test]
    public async Task A_peer_silent_for_24_hours_stops_holding_change_groups_and_refills_when_it_returns()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (first, second) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(first, """{"n":1}""")).IsTrue();
        var sjcNode = sjc.Node;
        var acked = await gru.AckedAsync(cluster.Database, sjcNode);

        // Written while sjc is away; 25 hours later sjc no longer holds back retention, so gru drops the group sjc lacks.
        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", second, Json("""{"n":2}"""), CancellationToken.None));
        await Assert.That(await gru.RetainsAsync(cluster.Database, acked)).IsTrue();
        var missing = await gru.HeadAsync(cluster.Database);
        // Turso timestamps CDC with real UTC; age only the disconnected history, keeping recovery rows fresh.
        var engine = gru.Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoStorageEngine>();
        var store = (TerraBase.Engine.Turso.TursoDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
        _ = await store.WithWriterAsync(session =>
        {
            using var age = session.Connection.CreateCommand();
            age.CommandText = "UPDATE turso_cdc SET change_time = unixepoch() - 90000 WHERE change_id <= $through; UPDATE _tb_peers SET seen = min(seen, $seen) WHERE node = $peer;";
            age.Parameters.AddWithValue("$through", missing);
            age.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeMilliseconds());
            age.Parameters.AddWithValue("$peer", unchecked((long)sjcNode.Value));
            return age.ExecuteNonQuery();
        }, "a test aging the disconnected CDC prefix and peer", CancellationToken.None);
        await gru.TrimAsync();
        await Assert.That(await gru.RetainsAsync(cluster.Database, acked)).IsFalse();

        // Back, sjc cannot get the group from the stream any more: gru tells it to refill, and it joins what differs.
        await sjc.StartAsync();
        await Assert.That(sjc.Node).IsEqualTo(sjcNode).Because("a clean stop must preserve the peer identity used by its acknowledgements");
        await Assert.That(await sjc.UntilAsync(second, """{"n":2}""")).IsTrue();
        var refillHead = await gru.HeadAsync(cluster.Database);
        var acknowledged = await gru.UntilAckedAsync(cluster.Database, sjcNode, refillHead);
        await Assert.That(acknowledged).IsTrue().Because(
            $"peer={sjcNode}, returned={sjc.Node}, expected head={refillHead}, current head={await gru.HeadAsync(cluster.Database)}, acked={await gru.AckedAsync(cluster.Database, sjcNode)}");
    }

    // A replica that only joins another region's writes: its sender has no local group to ack past them, so retention raises
    // the peer's ack over the joined rows itself (review of #106, M1); otherwise its CDC keeps every joined row.
    [Test]
    public async Task A_replica_that_only_joins_remote_writes_drops_their_cdc_rows()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var first = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":0}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(first, """{"n":0}""")).IsTrue();
        var last = string.Empty;
        for (var write = 1; write <= 50; write++)
        {
            last = EntityIds.New().ToString();
            var id = last;
            await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", id, Json($$"""{"n":{{write}}}"""), CancellationToken.None));
        }

        await Assert.That(await gru.UntilAsync(last, """{"n":50}""")).IsTrue();
        clock.Offset = TimeSpan.FromMinutes(2);
        var deadline = DateTime.UtcNow + Deadline;
        long rows;
        do
        {
            await Task.Delay(500);
            rows = await gru.CdcRowsAsync();
        }
        while (rows >= 20 && DateTime.UtcNow < deadline);

        await Assert.That(rows).IsLessThan(20);
    }

    // The other side of raising acks over joined rows: sjc's own write is joined on gru (rows sjc's ack may rise over), then
    // sjc is away while gru writes. A retention pass may raise sjc's ack over the joined rows but never past gru's
    // unshipped local group, so the group still ships when sjc returns.
    [Test]
    public async Task Retention_never_raises_an_ack_past_a_local_group_the_peer_lacks()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (first, joined, held) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":1}"""), CancellationToken.None);
        });
        var sjcNode = sjc.Node;
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.HeadAsync(cluster.Database))).IsTrue();
        await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", joined, Json("""{"n":3}"""), CancellationToken.None));
        await Assert.That(await gru.UntilAsync(joined, """{"n":3}""")).IsTrue();
        await sjc.StopAsync();
        var beforeHeld = await gru.HeadAsync(cluster.Database);
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", held, Json("""{"n":2}"""), CancellationToken.None));

        await gru.TrimAsync();
        await Assert.That(await gru.AckedAsync(cluster.Database, sjcNode)).IsLessThanOrEqualTo(beforeHeld);
        await sjc.StartAsync();
        await Assert.That(await sjc.UntilAsync(held, """{"n":2}""")).IsTrue();
    }

    // Review of #169: retention raises _tb_peers.acked over joined rows, but a live stream keeps its own in-memory ack. Once
    // retention cuts past that ack, the sender must not tell a peer that never went silent to refill. sjc loses gru's
    // document behind the streams' back: within 20 s only a refill (a forced compare) would bring it back, since
    // anti-entropy repairs a bucket only after two differing rounds 60 s apart. Two rounds of joined writes and a cut.
    [Test]
    public async Task A_live_peer_is_not_told_to_refill_after_its_ack_rises_over_joined_rows()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var first = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":0}"""), CancellationToken.None);
        });
        var sjcNode = sjc.Node;
        var streamAck = await gru.HeadAsync(cluster.Database);
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, streamAck)).IsTrue().Because("sjc acks gru's first group");
        List<string> rounds = [];
        for (var round = 1; round <= 2; round++)
        {
            var last = string.Empty;
            for (var write = 1; write <= 50; write++)
            {
                last = EntityIds.New().ToString();
                var id = last;
                await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", id, Json($$"""{"n":{{write}}}"""), CancellationToken.None));
            }

            await Assert.That(await gru.UntilAsync(last, """{"n":50}""")).IsTrue().Because($"round {round}: gru joins sjc's 50 writes");
            await sjc.LoseAsync("items", first);
            clock.Offset += TimeSpan.FromMinutes(2);
            await gru.TrimAsync();
            var cut = !await gru.RetainsAsync(cluster.Database, streamAck);
            var dbAck = await gru.AckedAsync(cluster.Database, sjcNode);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            string? back = null;
            while (back is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                back = await sjc.ReadAsync(first);
            }

            rounds.Add($"round {round}: stream ack {streamAck}, _tb_peers.acked {dbAck}, retains-false {cut}, lost document refilled {back is not null}");
            streamAck = await gru.AckedAsync(cluster.Database, sjcNode);
        }

        await Assert.That(rounds.Count(round => round.EndsWith("refilled True", StringComparison.Ordinal))).IsEqualTo(0).Because(string.Join("; ", rounds));
    }

    // Round-2 review, probe A: retention remembers each raise as a range without local groups, and that range must end at
    // the quiet point (the commit before the next local group), never past it. A live stream whose in-memory ack is still
    // the pre-raise ack reads from the end of the remembered range, and would otherwise skip gru's local group.
    [Test]
    public async Task A_read_from_below_a_raise_still_returns_the_local_group_after_it()
    {
        // Only this test drives gru's retention, including before its baseline ACK is captured.
        await using var cluster = await Cluster.StartAsync(cdc: true, clock: null, (region, services) =>
        {
            if (region == "gru")
            {
                var retention = services.Single(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationFactory?.Method.ReturnType == typeof(TerraBase.Engine.Turso.TursoChangeGroupSource));
                services.Remove(retention);
            }
        }, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (first, joined, held) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":1}"""), CancellationToken.None);
        });
        var sjcNode = sjc.Node;
        // The stream acks gru's own last group, never past it: a commit of sjc's frontier can land after that group (as
        // here, recorded as sjc's stream does), and only retention, which this test drives itself, raises the ack over it.
        await gru.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RecordFrontierAsync(
            cluster.Database, sjcNode, sjc.Api.Server.Services.GetRequiredService<LocalReplica>().Clock.Tick(), default, null, CancellationToken.None);
        var streamAck = (await gru.GroupsAsync(cluster.Database, 0))[^1].ChangeId;
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, streamAck)).IsTrue();
        await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", joined, Json("""{"n":3}"""), CancellationToken.None));
        await Assert.That(await gru.UntilAsync(joined, """{"n":3}""")).IsTrue();
        await sjc.StopAsync();
        await Assert.That(await gru.AckedAsync(cluster.Database, sjcNode)).IsEqualTo(streamAck);
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", held, Json("""{"n":2}"""), CancellationToken.None));

        await gru.TrimAsync();
        await Assert.That(await gru.AckedAsync(cluster.Database, sjcNode)).IsGreaterThan(streamAck);
        await Assert.That(await gru.RetainsAsync(cluster.Database, streamAck)).IsTrue();
        var groups = await gru.GroupsAsync(cluster.Database, streamAck);
        await Assert.That(groups.SelectMany(group => group.Deltas).Any(delta => delta.Id == held)).IsTrue().Because("the range a raise remembers ends before the next local group");
    }

    // Round-2 review, probe B: after a cut, neither a remembered range nor a read that starts before the cut may make
    // RetainsAsync spare a refill: the cut rows held gru's local group, which sjc lacks. Within 20 s only a refill brings it
    // back, since anti-entropy repairs a bucket after two differing rounds 60 s apart.
    [Test]
    public async Task A_cut_past_a_local_group_is_never_hidden_from_retains()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (first, joined, held) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":1}"""), CancellationToken.None);
        });
        var sjcNode = sjc.Node;
        var streamAck = await gru.HeadAsync(cluster.Database);
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, streamAck)).IsTrue();
        await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", joined, Json("""{"n":3}"""), CancellationToken.None));
        await Assert.That(await gru.UntilAsync(joined, """{"n":3}""")).IsTrue();
        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", held, Json("""{"n":2}"""), CancellationToken.None));
        await gru.TrimAsync();
        var acked = await gru.AckedAsync(cluster.Database, sjcNode);

        clock.Offset = TimeSpan.FromHours(25);
        await gru.TrimAsync();
        await Assert.That(await gru.RetainsAsync(cluster.Database, acked)).IsFalse();
        // A live stream's in-memory ack stays below the raise: the remembered range from it ends where the cut begins.
        await Assert.That(await gru.RetainsAsync(cluster.Database, streamAck)).IsFalse();
        _ = await gru.GroupsAsync(cluster.Database, acked);
        await Assert.That(await gru.RetainsAsync(cluster.Database, acked)).IsFalse().Because("a read past a cut proves nothing quiet");
        await Assert.That(await gru.RetainsAsync(cluster.Database, streamAck)).IsFalse();
        // Between a cut and the pass that prunes it, the raise's range (streamAck, acked] is still remembered: its end is
        // checked too, so RetainsAsync still sees the cut past it.
        gru.CdcSource.Remember(await gru.IncarnationAsync(), streamAck, acked);
        await Assert.That(await gru.RetainsAsync(cluster.Database, streamAck)).IsFalse().Because("the end of a remembered range is checked");

        await sjc.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        string? back = null;
        while (back is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            back = await sjc.ReadAsync(held);
        }

        await Assert.That(back).IsNotNull().Because("sjc refills the cut local group");
    }

    // Remembered quiet ranges stay bounded (round 2's should-fix: one entry per pass per database, all dropped at once at a
    // global cap, then a refill of every live peer). First 30 passes over joined writes only, inside the 60 s hold so
    // nothing is cut: the ranges touch and merge into one. Then 60 passes with a local write every third round (acked by
    // the live sjc) and the clock moving 30 s a round, so retention keeps cutting and the ranges below it are pruned.
    [Test]
    public async Task Remembered_quiet_ranges_stay_bounded_over_many_retention_passes()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        await gru.WriteAsync(async transaction => _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None));
        var sjcNode = sjc.Node;
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.HeadAsync(cluster.Database))).IsTrue();
        var incarnation = await gru.IncarnationAsync();
        for (var round = 1; round <= 30; round++)
        {
            var joined = EntityIds.New().ToString();
            await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", joined, Json($$"""{"j":{{round}}}"""), CancellationToken.None));
            await Assert.That(await gru.UntilAsync(joined, $$"""{"j":{{round}}}""")).IsTrue();
            await gru.TrimAsync();
        }

        await Assert.That(gru.CdcSource.QuietRanges(incarnation)).IsLessThanOrEqualTo(2).Because("touching quiet ranges merge");
        var most = 0;
        for (var round = 1; round <= 60; round++)
        {
            var joined = EntityIds.New().ToString();
            await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", joined, Json($$"""{"n":{{round}}}"""), CancellationToken.None));
            await Assert.That(await gru.UntilAsync(joined, $$"""{"n":{{round}}}""")).IsTrue();
            if (round % 3 == 0)
            {
                var local = EntityIds.New().ToString();
                await gru.WriteAsync(async transaction => await transaction.PutAsync("items", local, Json("""{"local":true}"""), CancellationToken.None));
                await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.HeadAsync(cluster.Database))).IsTrue();
            }

            clock.Offset += TimeSpan.FromSeconds(30);
            await gru.TrimAsync();
            most = Math.Max(most, gru.CdcSource.QuietRanges(incarnation));
        }

        await Assert.That(most).IsLessThanOrEqualTo(8).Because($"at most {most} ranges over 60 passes");
    }

    // PROBE (review round 3, E): remembered quiet ranges that a local group separates never merge across it, and a range is
    // only followed from inside it. Two raises over sjc's joined writes remember (low, q1] and (top, q2], with gru's local
    // groups held1 and held2 between them. Reads from below the lower range (low) and from between them (mid, held1's
    // commit) must return the local groups after them, also on a second pass, after the reads remembered their own ranges.
    [Test]
    public async Task Probe_r3_quiet_ranges_never_bridge_a_local_group()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (first, joined1, held1, held2, joined2) =
            (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":1}"""), CancellationToken.None);
        });
        var sjcNode = sjc.Node;
        var low = await gru.HeadAsync(cluster.Database);
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, low)).IsTrue();
        await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", joined1, Json("""{"j":1}"""), CancellationToken.None));
        await Assert.That(await gru.UntilAsync(joined1, """{"j":1}""")).IsTrue();
        await gru.TrimAsync();
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", held1, Json("""{"h":1}"""), CancellationToken.None));
        var mid = await gru.HeadAsync(cluster.Database);
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", held2, Json("""{"h":2}"""), CancellationToken.None));
        var top = await gru.HeadAsync(cluster.Database);
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, top)).IsTrue();
        await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", joined2, Json("""{"j":2}"""), CancellationToken.None));
        await Assert.That(await gru.UntilAsync(joined2, """{"j":2}""")).IsTrue();
        await gru.TrimAsync();
        await Assert.That(await gru.AckedAsync(cluster.Database, sjcNode)).IsGreaterThan(top);

        for (var pass = 1; pass <= 2; pass++)
        {
            var fromLow = (await gru.GroupsAsync(cluster.Database, low))
                .SelectMany(group => group.Deltas).Select(delta => delta.Id).ToList();
            await Assert.That(fromLow.Contains(held1) && fromLow.Contains(held2)).IsTrue()
                .Because($"pass {pass}: a read from {low} (ranges {gru.CdcSource.QuietRanges(await gru.IncarnationAsync())}) returns both local groups, got [{string.Join(", ", fromLow)}]");
            var fromMid = (await gru.GroupsAsync(cluster.Database, mid))
                .SelectMany(group => group.Deltas).Select(delta => delta.Id).ToList();
            await Assert.That(fromMid.Contains(held2)).IsTrue()
                .Because($"pass {pass}: a read from {mid}, below the upper range, returns held2, got [{string.Join(", ", fromMid)}]");
        }
    }

    // Reviewer-only probe F: fill the production cap with disjoint subranges of a real remote-only CDC transaction.
    // Losing a needed lower range must request refill, preserve another file's ranges, and never hide a local group.
    [Test]
    public async Task Probe_r3_cap_eviction_refills_without_skipping_local_groups()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru");
        var gru = cluster["gru"];
        var peer = ReplicaId.Of(2, 7, 0);
        var (first, joined, held) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":1}"""), CancellationToken.None);
        });
        var engine = gru.Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoStorageEngine>();
        var store = (TerraBase.Engine.Turso.TursoDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
        // An unstarted source avoids unrelated stream reads merging the intentionally sparse subranges.
        using var source = new TerraBase.Engine.Turso.TursoChangeGroupSource(engine,
            gru.Api.Server.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            gru.Api.Server.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TerraBase.Engine.Turso.TursoChangeGroupSource>>());
        var incarnation = store.Incarnation;
        var low = (await source.HeadAsync(cluster.Database, incarnation, CancellationToken.None))!.Value;
        await Assert.That(await source.AckAsync(cluster.Database, incarnation, peer, low, default, CancellationToken.None)).IsTrue();
        var remote = Put(peer, Stamp(), joined, """{"j":1}""");
        await gru.WriteAsync(async transaction => _ = await transaction.JoinAsync(remote.Collection, remote.Id, remote.State,
            new EntryVersion(remote.Stamp, peer), CancellationToken.None));
        await source.TrimAsync(store, CancellationToken.None);
        var quiet = (await source.AckedAsync(cluster.Database, peer, CancellationToken.None))!.Value.Acked;
        await Assert.That(quiet).IsGreaterThan(low);
        await gru.WriteAsync(async transaction => _ = await transaction.PutAsync("items", held, Json("""{"h":1}"""), CancellationToken.None));
        var heldEnd = (await source.HeadAsync(cluster.Database, incarnation, CancellationToken.None))!.Value;

        var stamp = Stamp();
        await gru.WriteAsync(async transaction =>
        {
            for (var row = 0; row < 5000; row++)
            {
                var delta = Put(peer, stamp, EntityIds.New().ToString(), """{"remote":true}""");
                _ = await transaction.JoinAsync(delta.Collection, delta.Id, delta.State, new EntryVersion(delta.Stamp, peer), CancellationToken.None);
            }
        });
        long newest;
        await using (var transaction = (TerraBase.Engine.Turso.TursoStorageTransaction)await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            var (groups, end, _) = await transaction.ReadChangeGroupsAsync(heldEnd, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, gru.Node, CancellationToken.None);
            await Assert.That(groups.Count).IsEqualTo(0).Because("every seeded subrange is inside proven remote-only CDC rows");
            newest = end;
        }

        await Assert.That(newest - heldEnd).IsGreaterThan(8194);
        // The ack above is no live one and no live link welcomed the peer, so nothing proves the first group or the joins: the
        // shape frontier holds every row from the first group on (FR-25). The probe is about the cut that follows the hold, so
        // the rows up to the first group pass the hold's 24 h bound; the age cut they allow stops below the ack's own cut.
        // Turso does not capture writes to turso_cdc.
        _ = await store.WithWriterAsync(session =>
        {
            using var age = session.Connection.CreateCommand();
            age.CommandText = "UPDATE turso_cdc SET change_time = $aged WHERE change_id <= $low;";
            age.Parameters.AddWithValue("$aged", clock.GetUtcNow().ToUnixTimeSeconds() - 90000);
            age.Parameters.AddWithValue("$low", low);
            return age.ExecuteNonQuery();
        }, "a test ending the shape frontier's hold", CancellationToken.None);
        clock.Offset = TimeSpan.FromMinutes(2);
        await source.TrimAsync(store, CancellationToken.None);
        await Assert.That(await source.RetainsAsync(cluster.Database, incarnation, low, CancellationToken.None)).IsTrue().Because("the lowest remembered range still spares a refill before eviction");
        var other = EntityIds.New();
        source.Remember(other, 0, 1);
        for (var range = 0; range < 4097; range++)
        {
            var from = heldEnd + range * 2;
            source.Remember(store.Incarnation, from, from + 1);
        }

        await Assert.That(source.QuietRanges(store.Incarnation)).IsEqualTo(4096).Because("the cap bounds one file without clearing all ranges");
        await Assert.That(source.QuietRanges(other)).IsEqualTo(1).Because("another file is unaffected by the cap");
        await Assert.That(await source.RetainsAsync(cluster.Database, incarnation, low, CancellationToken.None)).IsFalse().Because("evicting a still-needed low range requests refill");
        var remaining = (await source.ReadAsync(cluster.Database, incarnation, low, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None))!.Groups;
        await Assert.That(remaining.SelectMany(group => group.Deltas).Any(delta => delta.Id == held)).IsTrue().Because("cap eviction cannot skip the local group between the quiet ranges");
        await Assert.That(await source.RetainsAsync(cluster.Database, incarnation, low, CancellationToken.None)).IsFalse().Because("a read below the cut cannot recreate proof for evicted rows");
    }

    // A copy (and so a restore, which copies) must not inherit the source's _tb_peers.acked, an id of the source's
    // turso_cdc: once the copy's own ids passed it, it would be taken as the peer's ack and the copy's groups at or below it
    // never streamed (review of #106, M2). A copy's peer has acknowledged nothing of the copy.
    [Test]
    public async Task A_copied_database_does_not_inherit_its_sources_peer_acks()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
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

        var sjcNode = sjc.Node;
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.HeadAsync(cluster.Database))).IsTrue();
        var stale = await gru.AckedAsync(cluster.Database, sjcNode);
        await Assert.That(stale).IsGreaterThan(0);

        var engine = gru.Api.Server.Services.GetRequiredService<IStorageEngine>();
        var copy = cluster.Database with { DatabaseId = EntityIds.New().ToString() };
        await Assert.That(await engine.CopyDatabaseAsync(cluster.Database, copy, CancellationToken.None)).IsTrue();
        var store = (await engine.OpenDatabaseAsync(copy, CancellationToken.None))!;
        for (var write = 0; write < 500 && await gru.HeadAsync(copy) < stale; write++)
        {
            var id = EntityIds.New().ToString();
            _ = await store.WriteAsync(async (transaction, token) =>
            {
                _ = await transaction.PutAsync("items", id, Json("""{"n":2}"""), CancellationToken.None);
                return true;
            }, static _ => true, CancellationToken.None);
        }

        await Assert.That(await gru.HeadAsync(copy)).IsGreaterThanOrEqualTo(stale);
        await Assert.That(await gru.AckedAsync(copy, sjcNode)).IsEqualTo(0);
    }


    // An index dropped and made again with other fields while a replica is away reaches it through a refill as the final
    // definition only: the replica ends with the new fields, not its old index of that name.
    [Test]
    public async Task A_refill_brings_an_index_redefined_while_away_to_its_new_fields()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        _ = DocumentField.TryParse("n", out var n);
        _ = DocumentField.TryParse("m", out var m);
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.CreateIndexAsync(new IndexDefinition("by_key", "items", [n]), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilIndexesAsync(["by_key"])).IsTrue();
        var sjcNode = sjc.Node;
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.HeadAsync(cluster.Database))).IsTrue();
        var acked = await gru.AckedAsync(cluster.Database, sjcNode);

        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => await transaction.DropIndexAsync("by_key", CancellationToken.None));
        await gru.WriteAsync(async transaction => await transaction.CreateIndexAsync(new IndexDefinition("by_key", "items", [m]), CancellationToken.None));
        clock.Offset = TimeSpan.FromHours(25);
        var deadline = DateTime.UtcNow + Deadline;
        while (await gru.RetainsAsync(cluster.Database, acked) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        await Assert.That(await gru.RetainsAsync(cluster.Database, acked)).IsFalse();
        await sjc.StartAsync();
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.HeadAsync(cluster.Database))).IsTrue();

        var store = await sjc.StoreAsync();
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var index = (await read.ListIndexesAsync(CancellationToken.None)).Single(index => index.Name == "by_key");
        await Assert.That(index.Fields.Select(field => field.Path).ToList()).IsEquivalentTo(["m"]);
    }

    // Two regions make an index of the same name on different collections while apart. Their metadata document joins to
    // one winner (the later creation), and both machines end with that definition, not each with its own.
    [Test]
    public async Task An_index_made_under_one_name_in_two_regions_while_apart_ends_as_the_same_definition_in_both()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        _ = DocumentField.TryParse("n", out var n);
        await gru.WriteAsync(async transaction => await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None));
        var deadline = DateTime.UtcNow + Deadline;
        while (await sjc.CollectionIdAsync("items") is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await Assert.That(await sjc.CollectionIdAsync("items")).IsNotNull();

        // Each makes by_key on n while the other is down: gru on items, then sjc on others.
        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => await transaction.CreateIndexAsync(new IndexDefinition("by_key", "items", [n]), CancellationToken.None));
        await gru.StopAsync();
        await sjc.StartAsync();
        await sjc.WriteAsync(async transaction => await transaction.CreateIndexAsync(new IndexDefinition("by_key", "others", [n]), CancellationToken.None));
        await gru.StartAsync();

        await Assert.That(await gru.UntilIndexAsync("by_key", "others", ["n"])).IsTrue();
        await Assert.That(await sjc.UntilIndexAsync("by_key", "others", ["n"])).IsTrue();
    }

    // A refill runs beside the receiver's frames: a slow one (here the named machine never answers Digest; in production a
    // large database) must not hold back another database's batch past the sender's progress timeout.
    [Test]
    public async Task A_slow_refill_does_not_hold_back_another_databases_batch_past_the_progress_timeout()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();

        var silent = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            cluster.Members.Join(new ClusterMember("zzzz-silent", $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}", "gru", ""));
            using var observed = new CancellationTokenSource(Deadline);
            while (true)
            {
                var view = await sjc.Api.Server.Services.GetRequiredService<IMembership>().RefreshAsync(observed.Token);
                if (view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere).Any(member => member.Name == "zzzz-silent"))
                {
                    break;
                }

                await Task.Delay(50, observed.Token);
            }

            using var push = await RawPush.StartAsync(sjc, "zzzz-silent");
            var other = await sjc.CreateOtherDatabaseAsync();
            var otherId = EntityIds.New().ToString();
            await push.RefillAsync(cluster.Database, 1);
            await push.BatchAsync(other, 1, otherId);
            var acknowledged = await push.AckAsync();
            await Assert.That(acknowledged.Database).IsEqualTo(await push.WireAsync(other));
            await Assert.That(acknowledged is { ChangeId: 1, Refused: false }).IsTrue();
            await Assert.That(await sjc.ReadAsync(other, otherId)).IsEqualTo("""{"n":1}""");
        }
        finally
        {
            silent.Stop();
        }
    }

    // A busy database's writer holds back only that database: another database's batch on the same stream is acked once it
    // commits, and the held one only after its own durable commit.
    [Test]
    public async Task A_database_waiting_for_its_writer_does_not_hold_back_another_databases_ack_on_the_same_stream()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var sjc = cluster["sjc"];
        var peer = (await cluster.Members.ListAsync(CancellationToken.None)).Single(member => member.Region == "gru").Name;
        var held = await sjc.CreateOtherDatabaseAsync();
        var healthy = await sjc.CreateOtherDatabaseAsync();
        var (heldId, healthyId) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        using var push = await RawPush.StartAsync(sjc, peer);
        // A first batch makes the held database's local copy.
        await push.BatchAsync(held, 1, EntityIds.New().ToString());
        await Assert.That((await push.AckAsync()) is { ChangeId: 1, Refused: false }).IsTrue();
        var store = await sjc.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(held, CancellationToken.None)
            ?? throw new InvalidOperationException("the held database is missing");
        var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        try
        {
            await push.BatchAsync(held, 2, heldId);
            await push.BatchAsync(healthy, 1, healthyId);
            var first = await push.AckAsync();
            await Assert.That(first.Database).IsEqualTo(await push.WireAsync(healthy));
            await Assert.That(first is { ChangeId: 1, Refused: false }).IsTrue();
            await Assert.That(await sjc.ReadAsync(healthy, healthyId)).IsEqualTo("""{"n":1}""");
            await Assert.That(await sjc.ReadAsync(held, heldId)).IsNull().Because("the held database's batch waits for its writer, unacknowledged");
        }
        finally
        {
            await writer.DisposeAsync();
        }

        var released = await push.AckAsync();
        await Assert.That(released.Database).IsEqualTo(await push.WireAsync(held));
        await Assert.That(released is { ChangeId: 2, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(held, heldId)).IsEqualTo("""{"n":1}""");
    }

    // A refusal follows its database's frontier in progress: a malformed batch behind a frontier waiting for the writer is
    // refused only after the frontier's stable report. A heartbeat's empty answer needs no frontier, so it comes first.
    [Test]
    public async Task A_refused_batch_behind_an_in_flight_frontier_is_answered_after_the_frontier_report()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var sjc = cluster["sjc"];
        var peer = (await cluster.Members.ListAsync(CancellationToken.None)).Single(member => member.Region == "gru").Name;
        var held = await sjc.CreateOtherDatabaseAsync();
        using var push = await RawPush.StartAsync(sjc, peer);
        // A first batch makes the local copy: a frontier of a database without one records nothing and never waits.
        await push.BatchAsync(held, 1, EntityIds.New().ToString());
        await Assert.That((await push.AckAsync()) is { ChangeId: 1, Refused: false }).IsTrue();
        var store = await sjc.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(held, CancellationToken.None)
            ?? throw new InvalidOperationException("the held database is missing");
        var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        try
        {
            await push.FrontierAsync(held, Stamp());
            await push.MalformedBatchAsync(held, 2);
            await Assert.That(await push.HeartbeatAsync()).IsTrue().Because("the heartbeat's empty answer is the first one while the writer is held");
        }
        finally
        {
            await writer.DisposeAsync();
        }

        var stable = await push.FrameAsync();
        await Assert.That(stable.KindCase).IsEqualTo(AckFrame.KindOneofCase.Stable);
        await Assert.That(stable.Stable.Database).IsEqualTo(await push.WireAsync(held));
        var refused = await push.AckAsync();
        await Assert.That(refused is { Refused: true }).IsTrue();
        await Assert.That(refused.Database).IsEqualTo(await push.WireAsync(held));
    }

    // The sender lists the catalog first: a database its catalog batch registers is applied after that batch commits, not
    // refused as unregistered, also while the catalog's writer is busy. The database is created on gru only, so sjc's
    // catalog has no registration of it until the pushed batch commits.
    [Test]
    public async Task A_database_the_catalog_batch_registers_applies_after_that_batch_while_the_catalogs_writer_is_busy()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var created = cluster.Database with { DatabaseId = EntityIds.New().ToString() };
        var (status, body) = await gru.CommitAsync(new { op = "create_database", organization = created.OrganizationId, project = created.ProjectId, name = "registered", id = created.DatabaseId, replica_regions = new[] { sjc.Region } });
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
        var directory = sjc.Api.Server.Services.GetRequiredService<ITenantDirectory>();
        await Assert.That(await directory.RegisteredAsync(created, CancellationToken.None) is false).IsTrue();
        var catalog = await gru.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        var registration = (await gru.CdcSource.ReadAsync(ChangeGroups.System, catalog.Incarnation, 0, 1024, 1048576, CancellationToken.None))!.Groups;
        var id = EntityIds.New().ToString();
        using var push = await RawPush.StartAsync(sjc, gru.Name);
        push.Pin(ChangeGroups.System, catalog.Incarnation);
        var system = await sjc.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        var writer = await system.BeginTransactionAsync(write: true, CancellationToken.None);
        try
        {
            await push.GroupsAsync(ChangeGroups.System, registration);
            await push.BatchAsync(created, 1, id);
            // The link loop answers a heartbeat once it read every frame before it: both batches run while the writer is held,
            // and the database's batch, waiting for the catalog's, has no answer yet.
            await Assert.That(await push.HeartbeatAsync()).IsTrue();
        }
        finally
        {
            await writer.DisposeAsync();
        }

        var first = await push.AckAsync();
        await Assert.That(first.Database).IsEqualTo(await push.WireAsync(ChangeGroups.System));
        await Assert.That(first is { Refused: false }).IsTrue();
        await Assert.That(first.ChangeId).IsEqualTo((ulong)registration[^1].ChangeId);
        var second = await push.AckAsync();
        await Assert.That(second.Database).IsEqualTo(await push.WireAsync(created));
        await Assert.That(second is { ChangeId: 1, Refused: false }).IsTrue();
        await Assert.That(await directory.RegisteredAsync(created, CancellationToken.None) is true).IsTrue();
        await Assert.That(await sjc.ReadAsync(created, id)).IsEqualTo("""{"n":1}""");
    }

    [Test]
    public async Task Many_databases_share_bounded_refill_admission_across_origins_while_healthy_batches_progress()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var sjc = cluster["sjc"];
        var peer = (await cluster.Members.ListAsync(CancellationToken.None)).Single(member => member.Region == "gru").Name;
        var databases = new List<TenantContext>();
        for (var index = 0; index < 12; index++)
        {
            databases.Add(await sjc.CreateOtherDatabaseAsync());
        }

        var healthy = await sjc.CreateOtherDatabaseAsync();
        var entered = new ConcurrentDictionary<TenantContext, byte>();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sjc.BeforeDigest = async (database, token) =>
        {
            if (databases.Contains(database) || database == cluster.Database)
            {
                _ = entered.TryAdd(database, 0);
                if (entered.Count >= 4)
                {
                    _ = full.TrySetResult();
                }

                await held.Task.WaitAsync(token);
            }
        };
        using var first = await RawPush.StartAsync(sjc, peer);
        using var second = await RawPush.StartAsync(sjc, peer, ReplicaId.Of(1, 8, 0));
        try
        {
            foreach (var database in databases.Take(4))
            {
                await first.RefillAsync(database, 1);
            }

            await full.Task.WaitAsync(Deadline);
            foreach (var database in databases.Skip(4).Append(cluster.Database))
            {
                await second.RefillAsync(database, 1);
            }

            var id = EntityIds.New().ToString();
            var stamp = new Hlc(Stamp());
            // The real sender may reannounce its identity while these streams use synthetic origins.
            var self = sjc.Api.Server.Services.GetRequiredService<LocalReplica>();
            self.Presence.Heard(peer, cluster["gru"].Node, self.Clock.Tick().Milliseconds);
            await Assert.That(await sjc.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RecordFrontierAsync(
                healthy, cluster["gru"].Node, stamp, default, null, TestContext.Current!.Execution.CancellationToken)).IsNotNull();
            await second.BatchAsync(healthy, 1, id, stamp.Value);
            foreach (var database in databases.Skip(4).Append(cluster.Database))
            {
                await Assert.That(await second.AckAsync()).IsEqualTo(new Ack { Database = await second.WireAsync(database), Refused = true })
                    .Because("excess refills are refused before starting work on another origin");
            }

            await Assert.That(await second.AckAsync()).IsEqualTo(new Ack { Database = await second.WireAsync(healthy), ChangeId = 1, Applied = second.LastStamp, Stable = second.LastStamp });
            await Assert.That(await sjc.ReadAsync(healthy, id)).IsEqualTo("""{"n":1}""");
            await Assert.That(entered.Count).IsEqualTo(4);
            await Assert.That(sjc.RefillMemory!.Reserved).IsEqualTo(4 * RefillMemory.BytesPerRefill);
            await second.BatchAsync(cluster.Database, 2, EntityIds.New().ToString());
            await Assert.That(await second.AckAsync()).IsEqualTo(new Ack { Database = await second.WireAsync(cluster.Database), Refused = true, RefillRequired = true });

            held.SetResult();
            for (var index = 0; index < 4; index++)
            {
                await Assert.That((await first.AckAsync()).Refused).IsTrue();
            }

            await Assert.That(sjc.RefillMemory.Reserved).IsEqualTo(0);
            sjc.BeforeDigest = null;
            await second.RefillAsync(cluster.Database, 1);
            await Assert.That(await second.AckAsync()).IsEqualTo(new Ack { Database = await second.WireAsync(cluster.Database), ChangeId = 1 });
            var after = EntityIds.New().ToString();
            await second.BatchAsync(cluster.Database, 2, after);
            await Assert.That((await second.AckAsync()) is { ChangeId: 2, Refused: false }).IsTrue();
            await Assert.That(await sjc.ReadAsync(after)).IsEqualTo("""{"n":1}""");
        }
        finally
        {
            _ = held.TrySetResult();
        }
    }

    [Test]
    public async Task A_refill_without_a_byte_reservation_is_refused_and_can_retry()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var sjc = cluster["sjc"];
        var peer = (await cluster.Members.ListAsync(CancellationToken.None)).Single(member => member.Region == "gru").Name;
        var healthy = await sjc.CreateOtherDatabaseAsync();
        var digests = 0;
        sjc.BeforeDigest = (_, _) =>
        {
            _ = Interlocked.Increment(ref digests);
            return Task.CompletedTask;
        };
        sjc.RefillMemory!.Refuse = true;
        using var push = await RawPush.StartAsync(sjc, peer);
        await push.RefillAsync(cluster.Database, 1);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = await push.WireAsync(cluster.Database), Refused = true })
            .Because("a refill must reserve its bytes before allocating a digest or opening a channel");
        await Assert.That(digests).IsEqualTo(0);
        await Assert.That(sjc.RefillMemory.Reserved).IsEqualTo(0);
        var id = EntityIds.New().ToString();
        await push.BatchAsync(healthy, 1, id);
        await Assert.That((await push.AckAsync()) is { ChangeId: 1, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(healthy, id)).IsEqualTo("""{"n":1}""");

        sjc.RefillMemory.Refuse = false;
        await push.RefillAsync(cluster.Database, 1);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = await push.WireAsync(cluster.Database), ChangeId = 1 });
        await Assert.That(digests).IsEqualTo(1);
        await Assert.That(sjc.RefillMemory.Reserved).IsEqualTo(0);
    }

    // F8: observe the receiver's clean end before allowing Kestrel to tear down the HTTP/2 transport.
    [Test]
    public async Task Shutdown_ends_a_push_stream_still_waiting_for_its_hello()
    {
        var shutdown = new ShutdownBarrier();
        await using var cluster = await Cluster.StartAsync(cdc: false, clock: null,
            configure: (_, services) => services.AddHostedService(_ => shutdown), "gru");
        var gru = cluster["gru"];
        using var channel = PeerChannel.Create(gru.Api.Server.BaseAddress, PeerTls.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
        try
        {
            _ = await call.ResponseHeadersAsync.WaitAsync(Deadline);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ending = call.ResponseStream.MoveNext(timeout.Token);
            shutdown.Hold();
            gru.Api.Server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await shutdown.Entered.Task.WaitAsync(timeout.Token);
            await Assert.That(await ending).IsFalse();
            shutdown.Release.SetResult();
            await gru.Api.Server.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task.WaitAsync(Deadline);
        }
        finally
        {
            _ = shutdown.Release.TrySetResult();
        }
    }

    // A refill certifies its head only for complete coverage: a source that no longer holds the database covered nothing,
    // so the refill is refused, never acknowledged (the sender would move its ack past groups never received).
    [Test]
    public async Task A_refill_from_a_machine_that_does_not_hold_the_database_is_refused()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var sjc = cluster["sjc"];
        var database = cluster.Database with { DatabaseId = EntityIds.New().ToString() };
        var commit = JsonSerializer.Serialize(new
        {
            id = EntityIds.New().ToString(),
            ops = new object[] { new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "sjc-only", id = database.DatabaseId } },
        });
        using (var service = sjc.Api.Service(sjc.Api.User))
        {
            using var created = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(created.IsSuccessStatusCode).IsTrue().Because(await created.Content.ReadAsStringAsync());
        }

        using var push = await RawPush.StartAsync(sjc, (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name);
        await push.RefillAsync(database, 5);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = await push.WireAsync(database), Refused = true });
    }

    // Likewise a refill whose repair page this machine refuses (it stopped holding the database) is refused.
    [Test]
    public async Task A_refill_whose_repair_page_is_refused_is_refused()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var lost = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", lost, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        await sjc.LoseAsync("items", lost);
        sjc.RefusesRepairs = true;

        using var push = await RawPush.StartAsync(sjc, (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name);
        await push.RefillAsync(cluster.Database, 1);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = await push.WireAsync(cluster.Database), Refused = true });
    }

    [Test]
    public async Task A_refill_whose_source_loses_the_database_after_its_digest_is_refused()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var lost = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", lost, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        await sjc.LoseAsync("items", lost);
        gru.LoseOnBucketRead = cluster.Database;

        using var push = await RawPush.StartAsync(sjc, (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name);
        await push.RefillAsync(cluster.Database, 1);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = await push.WireAsync(cluster.Database), Refused = true });
        await Assert.That(await sjc.ReadAsync(lost)).IsNull();
    }

    // A batch that fails to apply (here the stored state of its document no longer decodes) refuses its database only: the
    // stream goes on with the other databases. The damage is corruption, so the copy faults and a certified refill from gru
    // rebuilds it under a new incarnation.
    [Test]
    public async Task A_batch_that_fails_to_apply_is_refused_for_its_database_only()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Json("""{"n":0}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(id, """{"n":0}""")).IsTrue();
        var damaged = await sjc.IncarnationAsync();
        await sjc.DamageAsync("items", id);

        using var push = await RawPush.StartAsync(sjc, (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name);
        var other = await sjc.CreateOtherDatabaseAsync();
        var otherId = EntityIds.New().ToString();
        await push.BatchAsync(cluster.Database, 1, id);
        await push.BatchAsync(other, 1, otherId);
        // Databases apply beside each other, so their acks may come in either order.
        Ack[] answers = [await push.AckAsync(), await push.AckAsync()];
        await Assert.That(answers.Single(ack => ack.Database.DatabaseId == cluster.Database.DatabaseId))
            .IsEqualTo(new Ack { Database = await push.WireAsync(cluster.Database), Refused = true });
        var acknowledged = answers.Single(ack => ack.Database.DatabaseId == other.DatabaseId);
        await Assert.That(acknowledged.Database).IsEqualTo(await push.WireAsync(other));
        await Assert.That(acknowledged is { ChangeId: 1, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(other, otherId)).IsEqualTo("""{"n":1}""");

        await Assert.That(await sjc.UntilRebuiltAsync(cluster.Database, damaged)).IsTrue();
        await Assert.That(await sjc.UntilAsync(id, """{"n":0}""")).IsTrue();
    }

    // A retention trim between the sender's read and its retention check must not ship the groups after the hole as if
    // nothing were missing: the sender sees the cut and the peer refills.
    [Test]
    public async Task A_trim_during_a_read_makes_the_sender_refill_rather_than_ship_past_the_hole()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var stamp = Stamp();
        var (id, trimmed, kept) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, id, """{"n":1}"""));
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();
        using (var deadline = new CancellationTokenSource(Deadline))
        {
            await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, 1, deadline.Token);
        }

        // While sjc is down gru writes two groups, and retention drops the first of them during gru's next read for sjc.
        await sjc.StopAsync();
        await Assert.That(await gru.UntilStreamsAsync([])).IsTrue();
        await gru.CommitAsync(Put(gru, stamp + 1, trimmed, """{"n":2}"""));
        await gru.CommitAsync(Put(gru, stamp + 2, kept, """{"n":3}"""));
        gru.Source.CutBeforeNextRead(cluster.Database, 2);
        await sjc.StartAsync();

        // sjc refills, so the trimmed group's document arrives too (anti-entropy alone would take two 60 s rounds).
        await Assert.That(await sjc.UntilAsync(trimmed, """{"n":2}""")).IsTrue();
        await Assert.That(await sjc.UntilAsync(kept, """{"n":3}""")).IsTrue();
        using (var deadline = new CancellationTokenSource(Deadline))
        {
            await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, 3, deadline.Token);
        }
    }

    // A copied peer row (review of A4, finding 3): the sender's cursor requires a refill even though the change log still
    // retains every group after the ack. The sender sends a refill pinned to its copy before any batch, keeps asking after
    // a refused one, and goes back to batches only once the refill is acknowledged. The log is whole after the ack, so the
    // refill names that ack, not the head, and the newer group then ships in a batch.
    [Test]
    public async Task A_cursor_that_requires_a_refill_refills_before_any_batch_until_the_refill_is_acknowledged()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var stamp = Stamp();
        var (lost, pending, live) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, lost, """{"n":1}"""));
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        var peer = sjc.Node;
        using (var deadline = new CancellationTokenSource(Deadline))
        {
            await gru.Source.UntilAckedAsync(cluster.Database, peer, 1, deadline.Token);
        }

        // sjc loses a document no group will ship again, so only a refill restores it. While sjc is down its row requires a
        // refill and gru writes one more group, which the log still retains after the ack.
        await sjc.LoseAsync("items", lost);
        await sjc.StopAsync();
        await Assert.That(await gru.UntilStreamsAsync([])).IsTrue();
        gru.Source.RequireRefill(cluster.Database, peer);
        await gru.CommitAsync(Put(gru, stamp + 1, pending, """{"n":2}"""));
        await Assert.That(await gru.Source.RetainsAsync(cluster.Database, await gru.IncarnationAsync(), 1, CancellationToken.None)).IsTrue();

        // The refill comes first: its repair is held, and while it is in flight no batch has brought the newer group.
        var held = sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await sjc.StartAsync();
        await sjc.RepairWaits.Task.WaitAsync(Deadline);
        await Assert.That(await sjc.ReadAsync(pending)).IsNull();

        // The refill fails and is refused: the cursor still requires one, so a second refill restores both documents and
        // the batch after its ack brings the newer group's change id.
        sjc.HeldRepairs = null;
        held.SetException(new IOException("a test fails the repair"));
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        await Assert.That(await sjc.UntilAsync(pending, """{"n":2}""")).IsTrue();
        using (var deadline = new CancellationTokenSource(Deadline))
        {
            await gru.Source.UntilAckedAsync(cluster.Database, peer, 2, deadline.Token);
        }

        await Assert.That(gru.Source.RequiresRefill(cluster.Database, peer)).IsFalse();

        // Then a batch brings the next group: repairs are held, so no further refill could.
        var blocked = sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await gru.CommitAsync(Put(gru, stamp + 2, live, """{"n":3}"""));
            await Assert.That(await sjc.UntilAsync(live, """{"n":3}""")).IsTrue();
        }
        finally
        {
            sjc.HeldRepairs = null;
            blocked.SetResult();
        }
    }

    [Test]
    public async Task Coverage_is_checked_again_inside_every_receiver_writer()
    {
        TerraBase.Engine.Turso.TursoDatabaseStore? store = null;
        var reset = false;
        ulong resetAfter = 0;
        TenantContext? clusterDatabase = null;
        await using var cluster = await Cluster.StartAsync(cdc: false, clock: null, configure: (region, services) =>
        {
            if (region == "sjc")
            {
                services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
                {
                    if (step == "replication.apply.committed" && store is not null && database == clusterDatabase && !reset)
                    {
                        using var command = store.CurrentWriter()!.Connection.CreateCommand();
                        command.CommandText = "UPDATE _tb_peers SET applied = 0 WHERE node = $origin AND applied = $applied;";
                        command.Parameters.AddWithValue("$origin", unchecked((long)ReplicaId.Of(1, 7, 0).Value));
                        command.Parameters.AddWithValue("$applied", unchecked((long)resetAfter));
                        reset = command.ExecuteNonQuery() == 1;
                    }
                });
            }
        }, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var stamp = Stamp();
        var seeded = EntityIds.New().ToString();
        await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, seeded, """{"n":0}"""));
        await Assert.That(await sjc.UntilAsync(seeded, """{"n":0}""")).IsTrue();
        clusterDatabase = cluster.Database;
        using var push = await RawPush.StartAsync(sjc, gru.Name);
        await push.FrontierAsync(cluster.Database, stamp);
        _ = await push.FrameAsync();
        var engine = sjc.Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoStorageEngine>();
        store = (TerraBase.Engine.Turso.TursoDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
        var origin = ReplicaId.Of(1, 7, 0);
        var first = EntityIds.New().ToString();
        var final = EntityIds.New().ToString();
        resetAfter = stamp + 1;
        await push.GroupsAsync(cluster.Database,
        [
            // Idempotent deltas cross the writer count bound without turning this ordering test into a write-load test.
            new OriginGroup(1, [.. Enumerable.Repeat(Put(origin, stamp + 1, first, """{"n":1}"""), ChangeGroups.MaxDeltas)]),
            new OriginGroup(2, [Put(origin, stamp + 2, final, """{"n":2}""")]),
        ], stamp);
        await Assert.That((await push.AckAsync()) is { Refused: true, RefillRequired: true }).IsTrue();
        await Assert.That(reset).IsTrue();
        await Assert.That(await sjc.ReadAsync(first)).IsEqualTo("""{"n":1}""");
        await Assert.That(await sjc.ReadAsync(final)).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Receiver_rollback_requires_a_refill_before_a_batch_or_idle_frontier_advances(bool idle)
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (lost, pending, live) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        var stamp = Stamp();
        await gru.CommitAsync(Metadata(gru, stamp, "items"), Put(gru, stamp, lost, """{"n":1}"""));
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        using (var deadline = new CancellationTokenSource(Deadline))
        {
            await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, 1, deadline.Token);
        }

        // ACK 1 may precede membership coverage; rollback needs a persisted nonzero expectation.
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await gru.Source.AckedAsync(cluster.Database, sjc.Node, token) is { Stable.Value: > 0 }, Deadline);

        var origin = gru.Node;
        if (idle)
        {
            await gru.StopAsync();
        }

        await sjc.LoseAsync("items", lost, origin);
        var held = sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (idle)
            {
                await gru.StartAsync();
            }
            else
            {
                await gru.CommitAsync(Put(gru, stamp + 1, pending, """{"n":2}"""));
            }

            await sjc.RepairWaits.Task.WaitAsync(Deadline);
            await Assert.That(await sjc.ReadAsync(lost)).IsNull();
            await Assert.That(await sjc.ReadAsync(pending)).IsNull().Because("no later group crosses the receiver's missing coverage");
            await Assert.That(gru.Source.AckedBy(cluster.Database, sjc.Node)).IsEqualTo(1);
            sjc.HeldRepairs = null;
            held.SetResult();
            await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
            if (!idle)
            {
                await Assert.That(await sjc.UntilAsync(pending, """{"n":2}""")).IsTrue();
                using var deadline = new CancellationTokenSource(Deadline);
                await gru.Source.UntilAckedAsync(cluster.Database, sjc.Node, 2, deadline.Token);
            }

            var blocked = sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await gru.CommitAsync(Put(gru, stamp + 2, live, """{"n":3}"""));
                await Assert.That(await sjc.UntilAsync(live, """{"n":3}""")).IsTrue()
                    .Because("the successful refill durably published coverage, so ordinary batches can resume");
            }
            finally
            {
                sjc.HeldRepairs = null;
                blocked.SetResult();
            }
        }
        finally
        {
            sjc.HeldRepairs = null;
            _ = held.TrySetResult();
        }
    }

    // The changes feed relies on this order (R8): a database's later batches on a link never join before its refill's
    // repairs commit, through a failed refill, a retry and a second refill, while other databases' batches go on.
    [Test]
    public async Task A_databases_later_batches_wait_for_its_refill_to_commit_through_a_failure_a_retry_and_a_second_refill()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (lost, first, second) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", lost, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        using var push = await RawPush.StartAsync(sjc, (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name);
        var database = await push.WireAsync(cluster.Database);
        var refused = new Ack { Database = database, Refused = true };

        // sjc lacks a document gru holds, so a refill from gru has a repair to make, and sjc holds it before it joins. Meanwhile
        // the database's batch and a second refill of it are refused, and another database's batch is acknowledged.
        await sjc.LoseAsync("items", lost);
        var held = sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = await sjc.CreateOtherDatabaseAsync();
        await push.RefillAsync(cluster.Database, 1);
        await push.BatchAsync(cluster.Database, 2, first);
        await push.RefillAsync(cluster.Database, 1);
        await push.BatchAsync(other, 1, first);
        await Assert.That(await push.AckAsync()).IsEqualTo(refused);
        await Assert.That(await push.AckAsync()).IsEqualTo(refused);
        var acknowledged = await push.AckAsync();
        await Assert.That(acknowledged.Database).IsEqualTo(await push.WireAsync(other));
        await Assert.That(acknowledged is { ChangeId: 1, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(other, first)).IsEqualTo("""{"n":1}""");

        // The repair fails: the refill is refused, and the database's batch still waits.
        held.SetException(new IOException("a test fails the repair"));
        await Assert.That(await push.AckAsync()).IsEqualTo(refused);
        await push.BatchAsync(cluster.Database, 2, first);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = database, Refused = true, RefillRequired = true });
        await Assert.That(await sjc.ReadAsync(first)).IsNull();

        // Asked again, the refill commits before its ack, and only then the database's next batch joins.
        sjc.HeldRepairs = null;
        await push.RefillAsync(cluster.Database, 1);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = database, ChangeId = 1 });
        await Assert.That(await sjc.ReadAsync(lost)).IsEqualTo("""{"n":1}""");
        await push.BatchAsync(cluster.Database, 2, first);
        await Assert.That((await push.AckAsync()) is { ChangeId: 2, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(first)).IsEqualTo("""{"n":1}""");

        // A second refill in a row holds the database's batches back the same way.
        await sjc.LoseAsync("items", lost);
        held = sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await push.RefillAsync(cluster.Database, 3);
        await push.BatchAsync(cluster.Database, 4, second);
        await Assert.That(await push.AckAsync()).IsEqualTo(refused);
        held.SetResult();
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = database, ChangeId = 3 });
        await Assert.That(await sjc.ReadAsync(lost)).IsEqualTo("""{"n":1}""");
        await Assert.That(await sjc.ReadAsync(second)).IsNull();
        await push.BatchAsync(cluster.Database, 4, second);
        await Assert.That((await push.AckAsync()) is { ChangeId: 4, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(second)).IsEqualTo("""{"n":1}""");
    }

    // The same order across a reconnect (R8): a newer stream of the origin starts once the older one ended with its refill,
    // and a refill that ended without committing keeps the database's batches waiting on the newer stream.
    [Test]
    public async Task A_refill_cut_short_by_a_reconnect_keeps_its_databases_batches_waiting_on_the_next_stream()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (lost, later) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", lost, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        var peer = (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name;
        var database = ChangeGroups.Wire(cluster.Database, await gru.IncarnationAsync());

        await sjc.LoseAsync("items", lost);
        sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var older = await RawPush.StartAsync(sjc, peer);
        await older.RefillAsync(cluster.Database, 1);
        await sjc.RepairWaits.Task.WaitAsync(Deadline);

        using var newer = await RawPush.StartAsync(sjc, peer);
        await newer.BatchAsync(cluster.Database, 2, later);
        await Assert.That(await newer.AckAsync()).IsEqualTo(new Ack { Database = database, Refused = true, RefillRequired = true });
        await Assert.That(await sjc.ReadAsync(later)).IsNull();

        sjc.HeldRepairs = null;
        await newer.RefillAsync(cluster.Database, 1);
        await Assert.That(await newer.AckAsync()).IsEqualTo(new Ack { Database = database, ChangeId = 1 });
        await Assert.That(await sjc.ReadAsync(lost)).IsEqualTo("""{"n":1}""");
        await newer.BatchAsync(cluster.Database, 2, later);
        await Assert.That((await newer.AckAsync()) is { ChangeId: 2, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(later)).IsEqualTo("""{"n":1}""");
    }

    // A refill that failed keeps the database cut across the sender's incarnations (review of A4, finding 3): a copy or
    // restore of the sender starts a new change log that never asks for the refill the old one owed, so its batches are
    // refused until a refill of the new copy commits. Change ids restart with each incarnation: a lower id of the new one
    // joins after the old one's higher id.
    [Test]
    public async Task A_failed_refill_keeps_the_database_cut_after_the_sender_becomes_a_new_incarnation()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (lost, before, held, other) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", lost, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        await sjc.LoseAsync("items", lost);
        using var push = await RawPush.StartAsync(sjc, (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name);

        // Control on another database: the old copy's id 5, then the new copy's id 1, both join.
        var second = await sjc.CreateOtherDatabaseAsync();
        push.Pin(second, Ulid.NewUlid());
        await push.BatchAsync(second, 5, before);
        await Assert.That((await push.AckAsync()) is { ChangeId: 5, Refused: false }).IsTrue();
        push.Pin(second, Ulid.NewUlid());
        await push.BatchAsync(second, 1, other);
        var renewedOther = await push.AckAsync();
        await Assert.That((renewedOther.Database, renewedOther.ChangeId, renewedOther.Refused)).IsEqualTo((await push.WireAsync(second), 1UL, false));
        await Assert.That(await sjc.ReadAsync(second, other)).IsEqualTo("""{"n":1}""");

        // The old copy X is no copy gru holds: its refill is refused at the digest, and the database stays cut.
        var old = Ulid.NewUlid();
        push.Pin(cluster.Database, old);
        await push.RefillAsync(cluster.Database, 5);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database, old), Refused = true });
        await push.BatchAsync(cluster.Database, 6, held);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database, old), Refused = true, RefillRequired = true });

        // The sender is now gru's real copy Y, whose change ids restart: its batch is still refused.
        var renewed = await gru.IncarnationAsync();
        push.Pin(cluster.Database, renewed);
        var wire = ChangeGroups.Wire(cluster.Database, renewed);
        await push.BatchAsync(cluster.Database, 2, held);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = wire, Refused = true, RefillRequired = true }).Because("the cut outlives the old incarnation");
        await Assert.That(await sjc.ReadAsync(held)).IsNull();
        await Assert.That(await sjc.ReadAsync(lost)).IsNull();

        // A refill pinned to Y commits, repairing what the cut stood for; then the same batch joins.
        await push.RefillAsync(cluster.Database, 1);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = wire, ChangeId = 1 });
        await Assert.That(await sjc.ReadAsync(lost)).IsEqualTo("""{"n":1}""");
        await push.BatchAsync(cluster.Database, 2, held);
        var joined = await push.AckAsync();
        await Assert.That((joined.Database, joined.ChangeId, joined.Refused)).IsEqualTo((wire, 2UL, false));
        await Assert.That(await sjc.ReadAsync(held)).IsEqualTo("""{"n":1}""");
    }

    [Test]
    public async Task A_rebuilt_sender_without_peer_rows_refills_a_receivers_failed_coverage_before_live_batches()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (lost, pending, live) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", lost, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, await gru.HeadAsync(cluster.Database))).IsTrue();

        // Keep both hosts alive while discovery pauses the sender and its storage is rebuilt without peer rows.
        var member = (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "sjc");
        cluster.Members.Leave(member.Name);
        await Assert.That(await gru.UntilStreamsAsync([])).IsTrue();
        await sjc.LoseAsync("items", lost);
        using (var push = await RawPush.StartAsync(sjc, gru.Name, gru.Node))
        {
            push.Pin(cluster.Database, Ulid.NewUlid());
            await push.RefillAsync(cluster.Database, 1);
            await Assert.That((await push.AckAsync()).Refused).IsTrue();
            await push.CompleteAsync();
        }

        var sender = (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru");
        cluster.Members.Leave(sender.Name);
        await Assert.That(await sjc.UntilStreamsAsync([])).IsTrue();

        var storage = gru.Api.Server.Services.GetRequiredService<IRebuildStorage>();
        var old = await gru.IncarnationAsync();
        var renewed = await storage.SwapAsync(cluster.Database, old, storage.SidePath(cluster.Database, old), CancellationToken.None);
        await Assert.That(renewed).IsNotNull();
        await Assert.That(renewed).IsNotEqualTo(old);
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", lost, Json("""{"n":1}"""), CancellationToken.None);
            _ = await transaction.PutAsync("items", pending, Json("""{"n":2}"""), CancellationToken.None);
        });
        await Assert.That(await gru.Groups.RetainsAsync(cluster.Database, renewed!.Value, 0, CancellationToken.None)).IsTrue();
        var engine = gru.Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoStorageEngine>();
        var store = (TerraBase.Engine.Turso.TursoDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
        var peers = await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM _tb_peers;";
            return (long)command.ExecuteScalar()!;
        }, "a test checking the rebuilt sender has no peer rows", CancellationToken.None);
        await Assert.That(peers).IsEqualTo(0);
        await using (var read = (TerraBase.Engine.Turso.TursoStorageTransaction)await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await read.GetAckedAsync(sjc.Node, renewed.Value, CancellationToken.None)).IsNull();
        }

        var digested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gru.BeforeDigest = (database, token) =>
        {
            if (database == cluster.Database)
            {
                _ = digested.TrySetResult();
            }
            return Task.CompletedTask;
        };
        var release = sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            cluster.Members.Join(member);
            cluster.Members.Join(sender);
            await digested.Task.WaitAsync(Deadline);
            await sjc.RepairWaits.Task.WaitAsync(Deadline);
            await Assert.That(await sjc.ReadAsync(lost)).IsNull();
            await Assert.That(await sjc.ReadAsync(pending)).IsNull().Because("no live batch crosses unfinished coverage");
            await Assert.That(await gru.Groups.AckedAsync(cluster.Database, sjc.Node, CancellationToken.None))
                .IsEqualTo(new SourceCursor(renewed.Value, 0)).Because("the sender initialized its missing scope without a persisted refill requirement");
            await Assert.That(await gru.AckedAsync(cluster.Database, sjc.Node)).IsEqualTo(0);

            sjc.HeldRepairs = null;
            release.SetResult();
            await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
            await Assert.That(await sjc.UntilAsync(pending, """{"n":2}""")).IsTrue();
            await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, await gru.HeadAsync(cluster.Database))).IsTrue();
            await gru.WriteAsync(async transaction =>
                _ = await transaction.PutAsync("items", live, Json("""{"n":3}"""), CancellationToken.None));
            await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, await gru.HeadAsync(cluster.Database))).IsTrue();
            await Assert.That(await sjc.ReadAsync(live)).IsEqualTo("""{"n":3}""");
        }
        finally
        {
            _ = release.TrySetResult();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Concurrent_replacements_of_one_origin_keep_only_the_latest_stream_and_its_refill_cut(bool throwingCancellation)
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (lost, later) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", lost, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        await sjc.LoseAsync("items", lost);
        var peer = (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name;
        sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sjc.HeldRepairExit = exit;
        using var older = await RawPush.StartAsync(sjc, peer);
        CancellationTokenRegistration registration = default;
        try
        {
            await older.RefillAsync(cluster.Database, 1);
            await sjc.RepairWaits.Task.WaitAsync(Deadline);
            var replacing = 0;
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var middleCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sjc.Api.Server.Services.GetRequiredService<PeerStreamReceiver>().Replacing = (origin, cancellationToken) =>
            {
                if (origin == ReplicaId.Of(1, 7, 0))
                {
                    if (Interlocked.Increment(ref replacing) == 1)
                    {
                        registration = cancellationToken.Register(() =>
                        {
                            _ = middleCancelled.TrySetResult();
                            if (throwingCancellation)
                            {
                                throw new InvalidOperationException("a test cancellation callback fails");
                            }
                        });
                        _ = first.TrySetResult();
                    }
                    else
                    {
                        _ = both.TrySetResult();
                    }
                }

                return Task.CompletedTask;
            };

            using var middle = await RawPush.OpenAsync(sjc, peer);
            await first.Task.WaitAsync(Deadline);
            using var latest = await RawPush.OpenAsync(sjc, peer);
            await both.Task.WaitAsync(Deadline);
            await middleCancelled.Task.WaitAsync(Deadline);
            exit.SetResult();

            await Assert.That(await latest.WelcomeAsync()).IsTrue();
            await Assert.That(await middle.WelcomeAsync()).IsFalse().Because("the latest replacement cancels its predecessor before either can join a batch");
            await Assert.That(sjc.RefillMemory!.Reserved).IsEqualTo(0).Because("cancelled predecessor work releases its byte reservation before handover");
            await latest.BatchAsync(cluster.Database, 2, later);
            await Assert.That(await latest.AckAsync()).IsEqualTo(new Ack { Database = await latest.WireAsync(cluster.Database), Refused = true, RefillRequired = true });
            await Assert.That(await sjc.ReadAsync(later)).IsNull();

            sjc.HeldRepairs = null;
            sjc.HeldRepairExit = null;
            await latest.RefillAsync(cluster.Database, 1);
            await Assert.That(await latest.AckAsync()).IsEqualTo(new Ack { Database = await latest.WireAsync(cluster.Database), ChangeId = 1 });
            await Assert.That(await sjc.ReadAsync(lost)).IsEqualTo("""{"n":1}""");
            await latest.BatchAsync(cluster.Database, 2, later);
            await Assert.That((await latest.AckAsync()) is { ChangeId: 2, Refused: false }).IsTrue();
            await Assert.That(await sjc.ReadAsync(later)).IsEqualTo("""{"n":1}""");
        }
        finally
        {
            registration.Dispose();
            _ = exit.TrySetResult();
        }
    }

    [Test]
    public async Task A_replacement_still_starts_when_its_predecessor_disposes_its_token_during_handover()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var sjc = cluster["sjc"];
        var peer = (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name;
        using var older = await RawPush.StartAsync(sjc, peer);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sjc.Api.Server.Services.GetRequiredService<PeerStreamReceiver>().Replacing = (origin, cancellationToken) =>
        {
            if (origin != ReplicaId.Of(1, 7, 0))
            {
                return Task.CompletedTask;
            }

            _ = observed.TrySetResult();
            return release.Task.WaitAsync(cancellationToken);
        };
        try
        {
            using var replacement = await RawPush.OpenAsync(sjc, peer);
            await observed.Task.WaitAsync(Deadline);
            await older.CompleteAsync();
            release.SetResult();
            await Assert.That(await replacement.WelcomeAsync()).IsTrue();
            var id = EntityIds.New().ToString();
            await replacement.BatchAsync(cluster.Database, 1, id);
            await Assert.That((await replacement.AckAsync()) is { ChangeId: 1, Refused: false }).IsTrue();
            await Assert.That(await sjc.ReadAsync(id)).IsEqualTo("""{"n":1}""");
        }
        finally
        {
            _ = release.TrySetResult();
        }
    }

    [Test]
    public async Task Shutdown_ends_a_replacement_after_its_predecessor_already_finished()
    {
        var shutdown = new ShutdownBarrier();
        await using var cluster = await Cluster.StartAsync(cdc: false, clock: null,
            configure: (_, services) => services.AddHostedService(_ => shutdown), "gru");
        var gru = cluster["gru"];
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gru.Api.Server.Services.GetRequiredService<PeerStreamReceiver>().Replacing = (_, _) =>
        {
            _ = observed.TrySetResult();
            return release.Task;
        };
        using var older = await RawPush.StartAsync(gru, gru.Name);
        try
        {
            using var replacement = await RawPush.OpenAsync(gru, gru.Name);
            await observed.Task.WaitAsync(Deadline);
            await older.CompleteAsync();
            _ = await replacement.HeadersAsync().WaitAsync(Deadline);
            var ending = replacement.WelcomeAsync();
            shutdown.Hold();
            gru.Api.Server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            release.SetResult();
            await shutdown.Entered.Task.WaitAsync(Deadline);
            await Assert.That(await ending).IsFalse();
            shutdown.Release.SetResult();
            await gru.Api.Server.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task.WaitAsync(Deadline);
        }
        finally
        {
            _ = release.TrySetResult();
            _ = shutdown.Release.TrySetResult();
        }
    }

    [Test]
    public async Task Shutdown_ends_a_replacement_waiting_for_its_predecessors_refill()
    {
        var shutdown = new ShutdownBarrier();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock: null, configure: (region, services) =>
        {
            if (region == "sjc")
            {
                services.AddHostedService(_ => shutdown);
            }
        }, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var lost = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", lost, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(lost, """{"n":1}""")).IsTrue();
        await sjc.LoseAsync("items", lost);
        var peer = (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name;
        sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sjc.HeldRepairExit = exit;
        using var older = await RawPush.StartAsync(sjc, peer);
        try
        {
            await older.RefillAsync(cluster.Database, 1);
            await sjc.RepairWaits.Task.WaitAsync(Deadline);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sjc.Api.Server.Services.GetRequiredService<PeerStreamReceiver>().Replacing = (origin, cancellationToken) =>
            {
                if (origin == ReplicaId.Of(1, 7, 0))
                {
                    _ = observed.TrySetResult();
                }

                return Task.CompletedTask;
            };
            using var replacement = await RawPush.OpenAsync(sjc, peer);
            await observed.Task.WaitAsync(Deadline);
            // The accepted response ends on the real shutdown signal, before Kestrel closes its transport.
            _ = await replacement.HeadersAsync().WaitAsync(Deadline);
            var ending = replacement.WelcomeAsync();
            shutdown.Hold();
            sjc.Api.Server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            exit.SetResult();
            await shutdown.Entered.Task.WaitAsync(Deadline);
            await Assert.That(await ending).IsFalse();
            shutdown.Release.SetResult();
            await sjc.Api.Server.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task.WaitAsync(Deadline);
        }
        finally
        {
            _ = shutdown.Release.TrySetResult();
            _ = exit.TrySetResult();
        }
    }

    // Decision 0013a, placement: on a machine of a cell a new database is placed in at least two regions. Created without
    // replica regions it is placed in every other serving region, and created in one region alone it is refused. An update
    // may still remove every replica region (the database then takes no writes until one is added back, and can be deleted).
    [Test]
    public async Task In_a_cell_a_new_database_is_placed_in_every_other_serving_region_and_never_created_in_one_region_alone()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc", "fra");
        var gru = cluster["gru"];
        var database = cluster.Database;
        await Assert.That(await gru.UntilStreamsAsync(["fra", "sjc"])).IsTrue();

        var (status, body) = await gru.CommitAsync(new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "defaulted", id = EntityIds.New().ToString() });
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
        await Assert.That(ReplicaRegions(body)).IsEquivalentTo(["fra", "sjc"]);

        (status, body) = await gru.CommitAsync(new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "alone", id = EntityIds.New().ToString(), replica_regions = Array.Empty<string>() });
        await Assert.That(status).IsEqualTo(HttpStatusCode.BadRequest).Because(body);
        await Assert.That(body).Contains("placement_needs_two_regions");

        (status, body) = await gru.CommitAsync(new { op = "set_database_regions", organization = database.OrganizationId, project = database.ProjectId, database = database.DatabaseId, replica_regions = Array.Empty<string>() });
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
    }

    // While no other region serves, a machine of a cell cannot place a new database in two regions by default: it fails
    // clearly, and creates nothing, rather than placing it in its own region alone.
    [Test]
    public async Task A_new_database_without_replica_regions_fails_clearly_while_no_other_region_serves()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var (gru, database) = (cluster["gru"], cluster.Database);
        await cluster["sjc"].StopAsync();
        await Assert.That(await gru.UntilStreamsAsync([])).IsTrue();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await gru.Api.Server.Services.GetRequiredService<IRemoteDurability>()
                .OtherRegionsAsync(token)).Count == 0, Deadline);
        var (status, body) = await gru.CommitAsync(new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "lonely", id = EntityIds.New().ToString() });
        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(body);
        await Assert.That(body).Contains("remote_region_unavailable");
    }

    // The replicaRegions of the first database body in a commit's answer.
    private static string[] ReplicaRegions(string body) =>
        [.. (FindReplicaRegions(JsonElement.Parse(body)) ?? throw new InvalidOperationException($"no replicaRegions in {body}")).EnumerateArray().Select(region => region.GetString()!)];

    private static JsonElement? FindReplicaRegions(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.TryGetProperty("replicaRegions", out var regions) ? regions
            : element.EnumerateObject().Select(property => FindReplicaRegions(property.Value)).FirstOrDefault(found => found is not null),
        JsonValueKind.Array => element.EnumerateArray().Select(FindReplicaRegions).FirstOrDefault(found => found is not null),
        _ => null,
    };

    [Test]
    public async Task A_tombstone_collected_while_a_peer_was_away_is_not_brought_back_by_its_refill_or_by_repairs()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (deleted, kept) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", deleted, Json("""{"n":1}"""), CancellationToken.None);
            _ = await transaction.PutAsync("items", kept, Json("""{"n":2}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(deleted, """{"n":1}""")).IsTrue();

        // sjc leaves the directory holding the live document; gru deletes it. A day after observing its absence gru can collect.
        var away = sjc.Node;
        await sjc.StopAsync();
        await Assert.That(await gru.UntilUnlistedAsync(away)).IsTrue();
        await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", deleted, CancellationToken.None));
        await Assert.That(await gru.HasStateAsync("items", deleted)).IsTrue();
        var states = gru.Api.Server.Services.GetRequiredService<IReplicaStates>();
        var (page, _) = await states.ReadBucketsAsync(cluster.Database, await gru.IncarnationAsync(),
            [TerraBase.Engine.Turso.TursoStorageTransaction.Bucket("items", deleted)], null,
            ChangeGroups.MaxDeltas, ChangeGroups.MaxBatchBytes, CancellationToken.None)
            ?? throw new InvalidOperationException("the deletion fixture refused its source copy");
        var deletion = page.Single(state => state.Collection == "items" && state.Id == deleted);
        clock.Offset = TimeSpan.FromHours(25);
        await Assert.That(await gru.UntilNoStateAsync("items", deleted)).IsTrue();

        // Missing deletion evidence keeps the returning copy's refill pending; healthy collected copies stay absent.
        await sjc.StartAsync();
        // Discovery lists the restarted fixture after startup; verify its catalog before driving repairs.
        await sjc.Api.VerifyCatalogAsync(TestContext.Current!.Execution.CancellationToken);
        await Assert.That(await sjc.AntiEntropy.RefillAsync(cluster.Database, gru.Name,
            await states.HorizonAsync(cluster.Database, CancellationToken.None), await gru.IncarnationAsync(), CancellationToken.None)).IsNull();
        await Assert.That(await sjc.ReadAsync(deleted)).IsEqualTo("""{"n":1}""");
        for (var round = 0; round < 3; round++)
        {
            await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
            await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        }

        await Assert.That(await gru.ReadAsync(deleted)).IsNull();
        await Assert.That(await gru.HasStateAsync("items", deleted)).IsFalse();
        // A retained tombstone provides actual deletion evidence, so the pending receiver can finish safely.
        await Assert.That(await sjc.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RepairAsync(cluster.Database,
            await sjc.IncarnationAsync(), [deletion], CancellationToken.None)).IsNotNull();
        await Assert.That(await sjc.UntilAbsentAsync(deleted)).IsTrue();
        await Assert.That(await sjc.ReadAsync(deleted)).IsNull();
        await Assert.That(await sjc.ReadAsync(kept)).IsEqualTo("""{"n":2}""");
    }

    [Test]
    public async Task A_tombstone_waits_for_every_live_peer_and_is_collected_everywhere_once_they_hold_it()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();

        // sjc is out of gru's list for a while (no stream), but was heard from recently: its stable, from before the delete,
        // holds gru's horizon, so ten minutes on the tombstone is still there.
        var member = (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "sjc");
        cluster.Members.Leave(member.Name);
        await Assert.That(await gru.UntilStreamsAsync([])).IsTrue();
        await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", id, CancellationToken.None));
        clock.Offset = TimeSpan.FromMinutes(10);
        await Task.Delay(TimeSpan.FromSeconds(12));
        await Assert.That(await gru.HasStateAsync("items", id)).IsTrue();

        // Listed again, sjc applies the delete and reports stable stamps past it: both collect the tombstone.
        cluster.Members.Join(member);
        await Assert.That(await sjc.UntilAbsentAsync(id)).IsTrue();
        clock.Offset = TimeSpan.FromMinutes(20);
        await Assert.That(await gru.UntilNoStateAsync("items", id)).IsTrue();
        await Assert.That(await sjc.UntilNoStateAsync("items", id)).IsTrue();
    }

    [Test]
    public async Task Review176_equal_stamp_node_tie_converges_in_opposite_arrival_orders()
    {
        await using var cluster = await Cluster.StartAsync("gru", "sjc");
        var stamp = Stamp();
        var low = ReplicaId.Of(1, 7, 0);
        var high = ReplicaId.Of(2, 7, 0);
        var older = Review176Metadata(low, stamp, DocumentDelta.Indexes, "by_key", """{"collection":"items","fields":["n"]}""");
        var winner = Review176Metadata(high, stamp, DocumentDelta.Indexes, "by_key", """{"collection":"others","fields":["m","n"]}""");
        await Review176PushAsync(cluster, cluster["gru"], low, older);
        await Review176PushAsync(cluster, cluster["gru"], high, winner);
        await Review176PushAsync(cluster, cluster["sjc"], high, winner);
        await Review176PushAsync(cluster, cluster["sjc"], low, older);
        foreach (var machine in cluster.Machines)
        {
            var store = await machine.StoreAsync();
            await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            var index = (await read.ListIndexesAsync(CancellationToken.None)).Single();
            await Assert.That(index.Collection).IsEqualTo("others");
            await Assert.That(index.Fields.Select(field => field.Path).ToArray()).IsEquivalentTo(["m", "n"]);
        }
    }

    [Test]
    public async Task Review176_definition_changes_the_digest_and_repairs_the_physical_index()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var origin = ReplicaId.Of(1, 7, 0);
        var stamp = Stamp();
        var before = Review176Metadata(origin, stamp, DocumentDelta.Indexes, "by_key", """{"collection":"items","fields":["n"]}""");
        var after = Review176Metadata(origin, stamp + 1, DocumentDelta.Indexes, "by_key", """{"collection":"items","fields":["m"]}""");
        foreach (var machine in cluster.Machines)
        {
            await Review176PushAsync(cluster, machine, origin, before);
        }

        // Same version with different payloads isolates whether the digest actually hashes the definition bytes.
        await Review176PushAsync(cluster, cluster["gru"], origin,
            Review176Metadata(origin, stamp, DocumentDelta.Indexes, "payload_key", """{"collection":"items","fields":["n"]}"""));
        await Review176PushAsync(cluster, cluster["sjc"], origin,
            Review176Metadata(origin, stamp, DocumentDelta.Indexes, "payload_key", """{"collection":"items","fields":["m"]}"""));
        var payloadGru = await Review176DigestAsync(cluster, cluster["gru"]);
        var payloadSjc = await Review176DigestAsync(cluster, cluster["sjc"]);
        await Assert.That(payloadGru.SequenceEqual(payloadSjc)).IsFalse();

        var oldDigest = await Review176DigestAsync(cluster, cluster["gru"]);
        await Review176PushAsync(cluster, cluster["gru"], origin, after);
        var newDigest = await Review176DigestAsync(cluster, cluster["gru"]);
        await Assert.That(oldDigest.SequenceEqual(newDigest)).IsFalse();
        await cluster["sjc"].AntiEntropy.RunRoundAsync(CancellationToken.None);
        await cluster["sjc"].AntiEntropy.RunRoundAsync(CancellationToken.None);
        var store = await cluster["sjc"].StoreAsync();
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var index = (await read.ListIndexesAsync(CancellationToken.None)).Single(index => index.Name == "by_key");
        await Assert.That(index.Fields.Single().Path).IsEqualTo("m");
        await Assert.That((await Review176DigestAsync(cluster, cluster["sjc"])).SequenceEqual(newDigest)).IsTrue();
    }

    [Test]
    public async Task Review176_rebuild_and_unique_reprojection_preserve_documents_and_reader_snapshots()
    {
        await using var cluster = await Cluster.StartAsync("gru");
        var machine = cluster["gru"];
        var origin = ReplicaId.Of(1, 7, 0);
        var stamp = Stamp();
        var ids = Enumerable.Range(0, 3).Select(_ => EntityIds.New().ToString()).Order(StringComparer.Ordinal).ToArray();
        string[] bodies = ["""{"m":10,"n":1}""", """{"m":20,"n":1}""", """{"m":20,"n":2}"""];
        await Review176PushAsync(cluster, machine, origin,
            Review176Metadata(origin, stamp, DocumentDelta.Indexes, "by_key", """{"collection":"items","fields":["n"]}"""),
            Put(origin, stamp, ids[0], bodies[0]), Put(origin, stamp, ids[1], bodies[1]), Put(origin, stamp, ids[2], bodies[2]),
            Review176Metadata(origin, stamp, DocumentDelta.Unique, "items", """{"constraints":[{"name":"key","fields":["n"],"normalization":"none"}]}"""));
        var store = await machine.StoreAsync();
        await using (var initial = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That((await initial.GetAsync("items", ids[1], CancellationToken.None))!.UniqueConflict).IsTrue();
        }

        _ = DocumentField.TryParse("n", out var n);
        var readers = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
                var index = (await read.ListIndexesAsync(CancellationToken.None)).Single();
                var rows = new List<string>();
                await foreach (var document in read.QuerySqlAsync(new SqlQuery("items", null, [new SqlOrder(n, false)], 10), CancellationToken.None))
                {
                    rows.Add(document.Id);
                }

                string[] expected = index.Fields.Single().Path == "n" ? [ids[0], ids[2]] : [ids[0], ids[1]];
                await Assert.That(rows).IsEquivalentTo(expected);
            }
        }).ToArray();
        await Review176PushAsync(cluster, machine, origin,
            Review176Metadata(origin, stamp + 1, DocumentDelta.Indexes, "by_key", """{"collection":"items","fields":["m"]}"""),
            Review176Metadata(origin, stamp + 1, DocumentDelta.Unique, "items", """{"constraints":[{"name":"key","fields":["m"],"normalization":"none"}]}"""));
        await Task.WhenAll(readers);
        await using (var read = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            var index = (await read.ListIndexesAsync(CancellationToken.None)).Single();
            await Assert.That(index.Fields.Single().Path).IsEqualTo("m");
            for (var i = 0; i < ids.Length; i++)
            {
                var document = await read.GetAsync("items", ids[i], CancellationToken.None);
                await Assert.That(Encoding.UTF8.GetString(document!.Json.Span)).IsEqualTo(bodies[i]);
                await Assert.That(document.UniqueConflict).IsEqualTo(i == 2);
            }
        }

        await using (var write = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await Assert.ThrowsAsync<UniqueConstraintException>(async () =>
                _ = await write.PutAsync("items", EntityIds.New().ToString(), Json("""{"m":20,"n":9}"""), CancellationToken.None));
        }

        await Review176PushAsync(cluster, machine, origin,
            Review176Metadata(origin, stamp + 2, DocumentDelta.Unique, "items", """{"constraints":[]}"""));
        await using (var restored = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            foreach (var id in ids)
            {
                await Assert.That((await restored.GetAsync("items", id, CancellationToken.None))!.UniqueConflict).IsFalse();
            }
        }

        await machine.StopAsync();
        await machine.StartAsync();
        var reopened = await machine.StoreAsync();
        await using var persisted = await reopened.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That((await persisted.ListIndexesAsync(CancellationToken.None)).Single().Fields.Single().Path).IsEqualTo("m");
        for (var i = 0; i < ids.Length; i++)
        {
            await Assert.That(Encoding.UTF8.GetString((await persisted.GetAsync("items", ids[i], CancellationToken.None))!.Json.Span)).IsEqualTo(bodies[i]);
        }
    }

    private static ChangeDelta Review176Metadata(ReplicaId node, ulong stamp, string collection, string id, string json)
    {
        var at = new EntryVersion(new Hlc(stamp), node);
        using var body = JsonDocument.Parse(json);
        return new ChangeDelta(collection, id, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }

    private static async Task Review176PushAsync(Cluster cluster, Machine machine, ReplicaId origin, params ChangeDelta[] deltas)
    {
        using var channel = PeerChannel.Create(machine.Api.Server.BaseAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        using var timeout = new CancellationTokenSource(Deadline);
        using var call = client.Push(cancellationToken: timeout.Token);
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "fra" } });
        _ = await call.ResponseStream.MoveNext(timeout.Token);
        await call.RequestStream.WriteAsync(new PushFrame
        {
            Batch = new Batch { Database = ChangeGroups.Wire(cluster.Database, Ulid.NewUlid()), LastChangeId = 1, Groups = ChangeGroups.Encode([new OriginGroup(1, deltas)]) },
        });
        await Assert.That(await call.ResponseStream.MoveNext(timeout.Token)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Ack.Refused).IsFalse();
        await call.RequestStream.CompleteAsync();
    }

    private static async Task<byte[]> Review176DigestAsync(Cluster cluster, Machine machine)
    {
        using var channel = PeerChannel.Create(machine.Api.Server.BaseAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var incarnation = await machine.IncarnationAsync();
        var digest = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(cluster.Database, incarnation) });
        await Assert.That(digest.Held).IsTrue();
        await Assert.That(new Ulid(digest.Incarnation.Span)).IsEqualTo(incarnation);
        return digest.Buckets.ToByteArray();
    }


    // Retention raises a connected peer's stored ack over joined rows, past the stream's own ack (review of #169): the stream
    // goes on from the raised ack, and the peer is never told to refill. gru's and sjc's writes interleave, with a retention
    // pass each round. sjc lost gru's first document behind the streams' back: only a refill (a forced compare) brings it
    // back that soon, since anti-entropy repairs a bucket after two differing rounds 60 s apart.
    [Test]
    public async Task A_connected_peer_streams_on_without_a_refill_while_retention_raises_its_ack_over_joined_rows()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var first = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":0}"""), CancellationToken.None);
        });
        var sjcNode = sjc.Node;
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.HeadAsync(cluster.Database))).IsTrue();
        await sjc.LoseAsync("items", first);

        for (var round = 1; round <= 3; round++)
        {
            // sjc's writes, joined on gru, with one of gru's own in the middle: the stream's ack ends at gru's group.
            var (local, remote, localHead) = (EntityIds.New().ToString(), string.Empty, 0L);
            for (var write = 1; write <= 20; write++)
            {
                remote = EntityIds.New().ToString();
                var id = remote;
                await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", id, Json($$"""{"r":{{round}}}"""), CancellationToken.None));
                if (write == 10)
                {
                    await gru.WriteAsync(async transaction => await transaction.PutAsync("items", local, Json($$"""{"l":{{round}}}"""), CancellationToken.None));
                    localHead = await gru.HeadAsync(cluster.Database);
                }
            }

            await Assert.That(await gru.UntilAsync(remote, $$"""{"r":{{round}}}""")).IsTrue();
            await Assert.That(await sjc.UntilAsync(local, $$"""{"l":{{round}}}""")).IsTrue();

            // After the hold, retention cuts past gru's group (so past the stream's ack) and keeps every row after the stored ack.
            clock.Offset += TimeSpan.FromMinutes(2);
            await gru.TrimAsync();
            var stored = await gru.AckedAsync(cluster.Database, sjcNode);
            await Assert.That(await gru.RowsRetainedAsync(localHead)).IsFalse().Because($"round {round}: the cut passes the stream's ack");
            await Assert.That(await gru.RowsRetainedAsync(stored)).IsTrue().Because($"round {round}: rows after the stored ack stay");

            // gru's next write ships from the raised ack, and no refill brought the lost document back meanwhile.
            var next = EntityIds.New().ToString();
            await gru.WriteAsync(async transaction => await transaction.PutAsync("items", next, Json($$"""{"next":{{round}}}"""), CancellationToken.None));
            await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.HeadAsync(cluster.Database))).IsTrue();
            await Assert.That(await sjc.ReadAsync(next)).IsEqualTo($$"""{"next":{{round}}}""");
            await Assert.That(await sjc.ReadAsync(first)).IsNull().Because($"round {round}: a refill brought back the document sjc lost");
        }
    }

    // The first stamp of a test: now, so convergence lags stay meaningful; later operations add logical ticks.
    private static ulong Stamp() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16;

    private static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);

    private static ChangeDelta Put(Machine machine, ulong stamp, string id, string json) => Put(machine.Node, stamp, id, json);

    private static ChangeDelta Put(ReplicaId node, ulong stamp, string id, string json)
    {
        var at = new EntryVersion(new Hlc(stamp), node);
        using var body = JsonDocument.Parse(json);
        return new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }

    private static ChangeDelta Set(Machine machine, ulong stamp, string id, string field, string json)
    {
        var at = new EntryVersion(new Hlc(stamp), machine.Node);
        using var value = JsonDocument.Parse(json);
        return new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Set(at, field, value.RootElement).DeltaAt(at.Stamp));
    }

    // The collection's metadata document, as a local creation writes it in the same transaction.
    private static ChangeDelta Metadata(Machine machine, ulong stamp, string collection)
    {
        var at = new EntryVersion(new Hlc(stamp), machine.Node);
        using var body = JsonDocument.Parse($$"""{"created_at":{{(long)(stamp >> 16)}},"entity_id":"{{EntityIds.New()}}"}""");
        return new ChangeDelta(DocumentDelta.Collections, collection, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }

    // The machine's internal listener recorded its peers' requests and none arrived without the generated credential.
    private static async Task AssertAuthenticatedAsync(Machine machine)
    {
        using var scraper = new HttpClient { BaseAddress = machine.Api.Server.MetricsAddress, Timeout = Deadline };
        var (matched, unmatched) = await PeerCredentialObservationTests.CountsAsync(scraper);
        await Assert.That(matched).IsGreaterThan(0).Because(machine.Region);
        await Assert.That(unmatched).IsEqualTo(0).Because(machine.Region);
    }

    // Hold only transport teardown: ApplicationStopping still cancels the real receiver. Kestrel can otherwise
    // send GOAWAY before its queued gRPC trailers, making clean handler completion look like an Internal error.
    private sealed class ShutdownBarrier : IHostedLifecycleService
    {
        // Startup failures must never wait for a test body that has not started.
        private Task _released = Task.CompletedTask;

        public void Hold() => _released = Release.Task;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StoppingAsync(CancellationToken cancellationToken)
        {
            _ = Entered.TrySetResult();
            await _released.WaitAsync(cancellationToken);
        }

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>One machine per region, all listed in one fake DNS, each holding the same database.</summary>
    internal sealed class Cluster : IAsyncDisposable
    {
        private readonly Dictionary<string, Machine> _machines = [with(StringComparer.Ordinal)];

        public FakeMemberStore Members { get; } = new();

        public string User { get; } = EntityIds.New().ToString();

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        // Whether the machines keep their CDC tailer as the change group source, instead of a fake.
        public bool Cdc { get; private init; }

        // The machines' host clock, which stamps and CDC retention read, when a test moves it forward.
        public ShiftedTime? Clock { get; private init; }

        public IEnumerable<Machine> Machines => _machines.Values;

        public Machine this[string region] => _machines[region];

        public static Task<Cluster> StartAsync(params string[] regions) => StartAsync(cdc: false, regions);

        public static Task<Cluster> StartAsync(bool cdc, params string[] regions) => StartAsync(cdc, clock: null, regions);

        public static Task<Cluster> StartAsync(bool cdc, ShiftedTime? clock, params string[] regions) =>
            StartAsync(cdc, clock, configure: null, regions);

        public static async Task<Cluster> StartAsync(bool cdc, ShiftedTime? clock, Action<string, IServiceCollection>? configure, params string[] regions)
        {
            var cluster = new Cluster { Cdc = cdc, Clock = clock };
            // A cell places every database in two regions or more (decision 0013): one region adds a region no machine serves.
            string[] placement = regions.Length == 1 ? [.. regions, "unserved"] : regions;
            try
            {
                foreach (var region in regions)
                {
                    var machine = new Machine(cluster, region, [.. placement.Where(other => other != region)]);
                    cluster._machines[region] = machine;
                    await machine.StartAsync(configure is null ? null : services => configure(region, services));
                }

                return cluster;
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var machine in _machines.Values)
            {
                try
                {
                    await machine.DisposeAsync();
                }
                // A host stopping cancels its in-flight replication calls (an anti-entropy compare, a push): that is the
                // shutdown, and it must not replace a failed assertion of the test that is unwinding through here.
                catch (Exception error) when (error is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled })
                {
                }
            }
        }
    }

    internal sealed class Machine(Cluster cluster, string region, string[] replicas) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-peer-streams-").FullName;
        private ManagementApi? _api;

        // The engine of the host running now: hosted services read the source while it starts, before _api is assigned.
        private IStorageEngine? _sourceEngine;

        public string Region { get; } = region;

        // The fake names this machine's own file of each database: the copy a refill's anti-entropy digests. It outlives a
        // restart (its groups and acks with it) and asks whichever engine the running host resolved. The system database
        // opens as the CDC source opens it, so a catalog pin compares its persisted incarnation.
        public FakeChangeGroupSource Source => field ??= new(async (database, token) =>
        {
            var engine = _sourceEngine ?? throw new InvalidOperationException($"{Region} is stopped");
            return database == ChangeGroups.System
                ? (await engine.OpenSystemDatabaseAsync(token)).Incarnation
                : (await engine.OpenDatabaseAsync(database, token))?.Incarnation;
        });

        public string Name { get; } = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];

        public Cluster Cluster => cluster;

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{Region} is stopped");

        public ReplicaId Node => Api.Server.Services.GetRequiredService<LocalReplica>().Id;

        // While set, this machine's repairs wait for it before they join (a faulted one fails them): a test holds a refill open.
        public TaskCompletionSource? HeldRepairs { get; set; }

        // A cancelled repair stays alive until this test releases its cleanup.
        public TaskCompletionSource? HeldRepairExit { get; set; }

        // While set, this machine's sink refuses repairs, as when it stops holding the database.
        public bool RefusesRepairs { get; set; }

        // The source becomes unavailable between a successful digest and its first bucket page.
        public TenantContext? LoseOnBucketRead { get; set; }

        public Func<TenantContext, CancellationToken, Task>? BeforeDigest { get; set; }

        public Func<TenantContext, IReadOnlyList<OriginGroup>, CancellationToken, Task>? BeforeApply { get; set; }

        public RefillMemory? RefillMemory { get; private set; }

        // Completes once a repair waits for HeldRepairs.
        public TaskCompletionSource RepairWaits { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private IStorageEngine Engine => Api.Server.Services.GetRequiredService<IStorageEngine>();

        public async Task StartAsync(Action<IServiceCollection>? configure = null)
        {
            var first = !File.Exists(Path.Combine(_directory, "_system.db"));
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = Region;
                settings["node-name"] = Name;
                settings["data-directory"] = _directory;
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(cluster.Members);
                services.AddSingleton<IChangeGroupSink>(provider => new HeldSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), this));
                services.AddSingleton<IReplicaStates>(provider => new LosingStates(provider.GetRequiredService<TerraBase.Engine.Turso.TursoChangeGroupSource>(), this));
                var memory = services.Single(service => service.ServiceType == typeof(IMemoryBudget));
                services.AddSingleton<IMemoryBudget>(provider => RefillMemory = new RefillMemory((IMemoryBudget)memory.ImplementationFactory!(provider)));
                if (!cluster.Cdc)
                {
                    services.AddSingleton<IChangeGroupSource>(provider =>
                    {
                        _sourceEngine = provider.GetRequiredService<IStorageEngine>();
                        return Source;
                    });
                }
                configure?.Invoke(services);
            }, time: cluster.Clock, userId: cluster.User);
            if (first)
            {
                await RegisterAsync();
            }
        }

        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
            _sourceEngine = null;
        }

        // The source the peer streams read: the CDC tailer, or the fake.
        public IChangeGroupSource Groups => Api.Server.Services.GetRequiredService<IChangeGroupSource>();

        public async Task<IDatabaseStore> StoreAsync() =>
            await Engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");

        public async Task<TenantContext> CreateOtherDatabaseAsync(string? databaseId = null)
        {
            var database = cluster.Database with { DatabaseId = databaseId ?? EntityIds.New().ToString() };
            var commit = JsonSerializer.Serialize(new
            {
                id = EntityIds.New().ToString(),
                ops = new object[] { new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = $"other-{database.DatabaseId.ToLowerInvariant()}", id = database.DatabaseId } },
            });
            using var service = Api.Service(Api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
            return database;
        }

        // Commits the deltas here, as a local write does, then hands them to the fake source as one group (the CDC tailer
        // finds them itself).
        public Task CommitAsync(params ChangeDelta[] deltas) => CommitAsync(cluster.Database, deltas);

        public async Task CommitAsync(TenantContext database, params ChangeDelta[] deltas)
        {
            var store = await Engine.OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the database is missing");
            await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                foreach (var delta in deltas)
                {
                    _ = await transaction.JoinAsync(delta.Collection, delta.Id, delta.State, new EntryVersion(delta.Stamp, Node), CancellationToken.None);
                }

                _ = await transaction.CommitAsync(CancellationToken.None);
            }

            if (!cluster.Cdc)
            {
                _ = Source.Add(database, deltas);
            }
        }

        // Runs `work` as one queued write of the database's group commit, as the document API does.
        public async Task WriteAsync(Func<IStorageTransaction, Task> work)
        {
            var store = await StoreAsync();
            _ = await store.WriteAsync(async (transaction, _) =>
            {
                await work(transaction);
                return true;
            }, static _ => true, CancellationToken.None);
        }

        // Whether the document's materialized JSON becomes `expected` within the deadline.
        public async Task<bool> UntilAsync(string id, string expected)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                if (await ReadAsync(id) == expected)
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        // Whether the copy comes back under an incarnation other than `damaged` within the deadline.
        public async Task<bool> UntilRebuiltAsync(TenantContext database, Ulid damaged)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                if (await HeldIncarnationAsync(database) is { } incarnation && incarnation != damaged)
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        // Whether `peer` acknowledges `changeId` within the deadline.
        public async Task<bool> UntilAckedAsync(TenantContext database, ReplicaId peer, long changeId)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (await AckedAsync(database, peer) < changeId)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                await Task.Delay(50);
            }

            return true;
        }

        // Whether the database's index names become exactly `names` within the deadline.
        public async Task<bool> UntilIndexesAsync(string[] names)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                var store = await StoreAsync();
                await using (var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None))
                {
                    if ((await transaction.ListIndexesAsync(CancellationToken.None)).Select(index => index.Name).SequenceEqual(names))
                    {
                        return true;
                    }
                }

                await Task.Delay(100);
            }

            return false;
        }

        public async Task<bool> UntilIndexAsync(string name, string collection, string[] fields)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                var store = await StoreAsync();
                await using (var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None))
                {
                    if ((await transaction.ListIndexesAsync(CancellationToken.None)).FirstOrDefault(index => index.Name == name) is { } index
                        && index.Collection == collection && index.Fields.Select(field => field.Path).SequenceEqual(fields))
                    {
                        return true;
                    }
                }

                await Task.Delay(100);
            }

            return false;
        }

        // Whether the document becomes a hidden uniqueness loser within the deadline.
        public async Task<bool> UntilUniqueLoserAsync(string id)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                var store = await StoreAsync();
                await using (var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None))
                {
                    if (await transaction.GetAsync("items", id, CancellationToken.None) is { UniqueConflict: true })
                    {
                        return true;
                    }
                }

                await Task.Delay(100);
            }

            return false;
        }

        public TerraBase.Engine.Turso.TursoChangeGroupSource CdcSource => Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoChangeGroupSource>();

        // The database file's incarnation, which keys the CDC source's remembered quiet ranges and which its reads and acks name.
        public Task<Ulid> IncarnationAsync() => IncarnationAsync(cluster.Database);

        public async Task<Ulid> IncarnationAsync(TenantContext database) =>
            await HeldIncarnationAsync(database) ?? throw new InvalidOperationException("the database is missing");

        public async Task<Ulid?> HeldIncarnationAsync(TenantContext database) => (await Engine.OpenDatabaseAsync(database, CancellationToken.None))?.Incarnation;

        // The source's reads of the copy in place, which never refuse its own incarnation.
        public async Task<long> HeadAsync(TenantContext database) =>
            await Groups.HeadAsync(database, await IncarnationAsync(database), CancellationToken.None) ?? throw new InvalidOperationException("the copy refused its own incarnation");

        public async Task<bool> RetainsAsync(TenantContext database, long after) =>
            await Groups.RetainsAsync(database, await IncarnationAsync(database), after, CancellationToken.None) ?? throw new InvalidOperationException("the copy refused its own incarnation");

        public async Task<IReadOnlyList<OriginGroup>> GroupsAsync(TenantContext database, long after) =>
            (await Groups.ReadAsync(database, await IncarnationAsync(database), after, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)
                ?? throw new InvalidOperationException("the copy refused its own incarnation")).Groups;

        // The peer's ack of the copy in place: a cursor of another incarnation would be a stale ack.
        public async Task<long> AckedAsync(TenantContext database, ReplicaId peer)
        {
            var cursor = await Groups.AckedAsync(database, peer, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");
            return cursor.Incarnation == await IncarnationAsync(database) ? cursor.Acked : throw new InvalidOperationException("the cursor names another incarnation");
        }

        // One retention pass over the database, as the CDC source's 5 s loop runs it.
        public async Task TrimAsync()
        {
            var engine = Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoStorageEngine>();
            var store = (TerraBase.Engine.Turso.TursoDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
            await Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoChangeGroupSource>().TrimAsync(store, CancellationToken.None);
        }

        // The rows of the database's turso_cdc.
        // Whether every CDC row after `after` is still in the file: the rows themselves, no quiet range skipped.
        public async Task<bool> RowsRetainedAsync(long after)
        {
            var engine = Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoStorageEngine>();
            var store = (await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
            var transaction = (TerraBase.Engine.Turso.TursoStorageTransaction)await store.BeginTransactionAsync(write: false, CancellationToken.None);
            await using (transaction)
            {
                return await transaction.RetainsAsync(after, CancellationToken.None);
            }
        }

        public async Task<long> CdcRowsAsync()
        {
            var engine = Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoStorageEngine>();
            var store = (TerraBase.Engine.Turso.TursoDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
            return await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM turso_cdc;";
                return (long)command.ExecuteScalar()!;
            }, "a test counting CDC rows", CancellationToken.None);
        }

        public AntiEntropy AntiEntropy => Api.Server.Services.GetRequiredService<AntiEntropy>();

        // Deletes rows without a change group; an origin also rolls its persisted applied frontier back.
        public async Task LoseAsync(string collection, string id, ReplicaId? origin = null)
        {
            var engine = Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoStorageEngine>();
            var store = (TerraBase.Engine.Turso.TursoDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
            _ = await store.WithWriterAsync(session =>
            {
                using var transaction = session.Connection.BeginTransaction();
                using var command = session.Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM \"{collection}\" WHERE id = $id; DELETE FROM _tb_state WHERE collection = $collection AND id = $id; UPDATE _tb_peers SET applied = 0 WHERE node = $origin;";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$collection", collection);
                command.Parameters.AddWithValue("$origin", unchecked((long)(origin?.Value ?? 0)));
                var changed = command.ExecuteNonQuery();
                MerkleFixture.Rebuild(session, transaction);
                transaction.Commit();
                return changed;
            }, "a test losing a document", CancellationToken.None);
            store.ForgetCollections();
        }

        // Overwrites the document's stored state with bytes that do not decode, as a damaged file would hold.
        public async Task DamageAsync(string collection, string id)
        {
            var engine = Api.Server.Services.GetRequiredService<TerraBase.Engine.Turso.TursoStorageEngine>();
            var store = (TerraBase.Engine.Turso.TursoDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
            _ = await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "UPDATE _tb_state SET state = '!' WHERE collection = $collection AND id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$collection", collection);
                return command.ExecuteNonQuery();
            }, "a test damaging a document's state", CancellationToken.None);
        }

        // Whether this machine holds a state row of the document (live or a tombstone).
        public async Task<bool> HasStateAsync(string collection, string id)
        {
            var states = Api.Server.Services.GetRequiredService<IReplicaStates>();
            var bucket = TerraBase.Engine.Turso.TursoStorageTransaction.Bucket(collection, id);
            var (page, _) = (await states.ReadBucketsAsync(cluster.Database, await IncarnationAsync(), [bucket], null, ChangeGroups.MaxDeltas, int.MaxValue, CancellationToken.None))
                ?? throw new InvalidOperationException("the copy refused its own incarnation");
            return page.Any(state => state.Collection == collection && state.Id == id);
        }

        // Whether the document's state row goes (collected) within the deadline.
        public async Task<bool> UntilNoStateAsync(string collection, string id)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (await HasStateAsync(collection, id))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                await Task.Delay(200);
            }

            return true;
        }

        public async Task<bool> UntilUnlistedAsync(ReplicaId peer)
        {
            var presence = Api.Server.Services.GetRequiredService<LocalReplica>().Presence;
            var deadline = DateTime.UtcNow + Deadline;
            while (!presence.Absent(peer, long.MaxValue))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                await Task.Delay(100);
            }

            return true;
        }

        // Whether the outbound streams that report connected become exactly `regions` within the deadline.
        public async Task<bool> UntilStreamsAsync(string[] regions)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                using var response = await Api.Server.Client.GetAsync(new Uri("/replication", UriKind.Relative));
                var peers = JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("peers").EnumerateArray()
                    .Select(peer => peer.GetProperty("region").GetString()!).Order(StringComparer.Ordinal).ToArray();
                if (peers.SequenceEqual(regions.Order(StringComparer.Ordinal)))
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        // Whether the scraped gauge `name` (its series with `label`, when given) satisfies `holds` within the deadline; with
        // `absent`, whether that series is gone.
        public async Task<bool> UntilMetricAsync(string name, Func<double, bool> holds, string? label = null, bool absent = false)
        {
            using var metrics = new HttpClient { BaseAddress = Api.Server.MetricsAddress };
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                var line = (await metrics.GetStringAsync(new Uri("/metrics", UriKind.Relative))).Split('\n')
                    .FirstOrDefault(line => line.StartsWith(name, StringComparison.Ordinal) && !line.StartsWith(name + "_", StringComparison.Ordinal)
                        && (label is null || line.Contains(label, StringComparison.Ordinal)));
                // name{labels} value [timestamp]
                var sample = line?[(Math.Max(line.LastIndexOf('}'), name.Length - 1) + 1)..].Trim().Split(' ')[0];
                if (absent ? line is null
                    : double.TryParse(sample, System.Globalization.CultureInfo.InvariantCulture, out var value) && holds(value))
                {
                    return true;
                }

                await Task.Delay(200);
            }

            return false;
        }

        // Whether the document is deleted, or never arrived, within the deadline.
        public async Task<bool> UntilAbsentAsync(string id)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                if (await ReadAsync(id) is null)
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        public async Task<string?> CollectionIdAsync(string collection)
        {
            var store = await Engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None);
            await using var transaction = await store!.BeginTransactionAsync(write: false, CancellationToken.None);
            return (await transaction.GetCollectionAsync(collection, CancellationToken.None))?.Id;
        }

        // The summary once it reads `expected`, or the last one at the deadline: lag settles after the last acks.
        public async Task<string> HealthAsync(string expected)
        {
            var deadline = DateTime.UtcNow + Deadline;
            string body;
            do
            {
                using var response = await Api.Server.Client.GetAsync(new Uri("/health/replication", UriKind.Relative));
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                body = JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetRawText();
                await Task.Delay(100);
            }
            while (body != expected && DateTime.UtcNow < deadline);

            return body;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_api is not null)
                {
                    await StopAsync();
                }
            }
            finally
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        public Task<string?> ReadAsync(string id) => ReadAsync(cluster.Database, id);

        public async Task<string?> ReadAsync(TenantContext database, string id)
        {
            if (await Engine.OpenDatabaseAsync(database, CancellationToken.None) is not { } store)
            {
                return null;
            }

            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            try
            {
                return await transaction.GetAsync("items", id, CancellationToken.None) is { Deleted: false } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // One management commit of `op` as the cluster's user: its status and body.
        public async Task<(HttpStatusCode Status, string Body)> CommitAsync(object op)
        {
            var commit = JsonSerializer.Serialize(new { id = EntityIds.New().ToString(), ops = new[] { op } });
            using var service = Api.Service(Api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // The organization, project and database under the cluster's ids, placed in every region of the cluster.
        private async Task RegisterAsync()
        {
            var database = cluster.Database;
            var commit = JsonSerializer.Serialize(new
            {
                id = EntityIds.New().ToString(),
                ops = new object[]
                {
                    new { op = "create_organization", id = database.OrganizationId, name = $"peers-{database.OrganizationId}" },
                    new { op = "create_project", organization = database.OrganizationId, id = database.ProjectId, name = "peers" },
                    new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "main", id = database.DatabaseId, replica_regions = replicas },
                },
            });
            using var service = Api.Service(Api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>A machine's sink whose applies and repairs wait while the test holds them.</summary>
    private sealed class HeldSink(IChangeGroupSink sink, Machine machine) : IChangeGroupSink
    {
        public ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken) =>
            sink.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);

        public async ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null)
        {
            if (machine.BeforeApply is { } before)
            {
                await before(database, groups, cancellationToken);
            }

            return await sink.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);
        }

        public async ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken)
        {
            if (machine.HeldRepairs is { } held)
            {
                _ = machine.RepairWaits.TrySetResult();
                try
                {
                    await held.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (machine.HeldRepairExit is { } exit)
                {
                    await exit.Task;
                    throw;
                }
            }

            return machine.RefusesRepairs ? null : await sink.RepairAsync(database, incarnation, states, cancellationToken);
        }
    }

    private sealed class LosingStates(IReplicaStates states, Machine machine) : IReplicaStates
    {
        public ValueTask<Hlc> StableAsync(TenantContext database, CancellationToken cancellationToken) => states.StableAsync(database, cancellationToken);

        public ValueTask<Hlc> HorizonAsync(TenantContext database, CancellationToken cancellationToken) => states.HorizonAsync(database, cancellationToken);

        public ValueTask<int?> ForgetAsync(
            TenantContext database, Ulid incarnation, IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken) =>
            states.ForgetAsync(database, incarnation, keys, horizon, cancellationToken);

        private readonly ConcurrentDictionary<TenantContext, byte> _lost = new();

        public async ValueTask<ReplicaDigest?> DigestAsync(TenantContext database, Ulid? expected, CancellationToken cancellationToken)
        {
            if (machine.BeforeDigest is { } before)
            {
                await before(database, cancellationToken);
            }

            return _lost.ContainsKey(database) ? null : await states.DigestAsync(database, expected, cancellationToken);
        }

        public ValueTask<bool> MatchedAsync(TenantContext database, ReplicaDigest digest, CancellationToken cancellationToken) =>
            states.MatchedAsync(database, digest, cancellationToken);

        // An empty page, then refused reads: the closing pin check detects that the source lost the database.
        public ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)?> ReadBucketsAsync(
            TenantContext database, Ulid incarnation, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken)
        {
            // A lost copy refuses every later read pinned to it, as storage does: the closing check of a refill among them.
            if (_lost.ContainsKey(database))
            {
                return ValueTask.FromResult<(IReadOnlyList<RepairState>, BucketCursor?)?>(null);
            }

            if (machine.LoseOnBucketRead == database && buckets.Count > 0)
            {
                _ = _lost.TryAdd(database, 0);
                return ValueTask.FromResult<(IReadOnlyList<RepairState>, BucketCursor?)?>(([], null));
            }

            return states.ReadBucketsAsync(database, incarnation, buckets, after, maxStates, maxBytes, cancellationToken);
        }
    }

    internal sealed class RefillMemory(IMemoryBudget inner) : IMemoryBudget
    {
        public const long BytesPerRefill = (2 * 4096 * 16) + (4L * ReplicationProtocol.MaxApplicationBytes);
        private long _reserved;
        public long Reserved => Interlocked.Read(ref _reserved);
        public bool Refuse { get; set; }

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            if (lane != MemoryLane.Bulk || bytes != BytesPerRefill)
            {
                return inner.TryReserve(lane, bytes, tenant);
            }

            if (Refuse || inner.TryReserve(lane, bytes, tenant) is not { } reservation)
            {
                return null;
            }

            _ = Interlocked.Add(ref _reserved, bytes);
            return new Reservation(this, reservation);
        }

        private sealed class Reservation(RefillMemory memory, IMemoryReservation inner) : IMemoryReservation
        {
            private int _released;
            public long Bytes => inner.Bytes;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    _ = Interlocked.Add(ref memory._reserved, -Bytes);
                    inner.Dispose();
                }
            }
        }
    }

    /// <summary>A push stream a test drives frame by frame, from a made-up origin that names <c>peer</c> for its refills.</summary>
    private sealed class RawPush : IDisposable
    {
        private static readonly ReplicaId Origin = ReplicaId.Of(1, 7, 0);
        private readonly Grpc.Net.Client.GrpcChannel _channel;
        private readonly AsyncDuplexStreamingCall<PushFrame, AckFrame> _call;
        private readonly ReplicaId _origin;
        private readonly Machine? _peer;
        private readonly Dictionary<TenantContext, Ulid> _incarnations = [];

        public ulong LastStamp { get; private set; }

        private RawPush(Machine machine, string peer, ReplicaId? origin)
        {
            _origin = origin ?? Origin;
            _peer = machine.Cluster.Machines.FirstOrDefault(listed => listed.Name == peer);
            _channel = PeerChannel.Create(machine.Api.Server.BaseAddress, PeerTls.Credential);
            _call = new Features.Replication.V1.Replication.ReplicationClient(_channel).Push();
        }

        // Says hello and waits for the welcome.
        public static async Task<RawPush> StartAsync(Machine machine, string peer, ReplicaId? origin = null)
        {
            var push = await OpenAsync(machine, peer, origin);
            try
            {
                await Assert.That(await push.WelcomeAsync()).IsTrue();
                return push;
            }
            catch
            {
                push.Dispose();
                throw;
            }
        }

        public static async Task<RawPush> OpenAsync(Machine machine, string peer, ReplicaId? origin = null)
        {
            var push = new RawPush(machine, peer, origin);
            await push._call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = push._origin.Value, Region = "gru", Machine = peer } });
            return push;
        }

        public async Task<bool> WelcomeAsync()
        {
            using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
            return await _call.ResponseStream.MoveNext(timeout.Token) && _call.ResponseStream.Current.Welcome is not null;
        }

        public Task<Metadata> HeadersAsync() => _call.ResponseHeadersAsync;

        public async Task CompleteAsync()
        {
            await _call.RequestStream.CompleteAsync();
            using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
            await Assert.That(await _call.ResponseStream.MoveNext(timeout.Token)).IsFalse();
        }

        // The database as this stream's frames name it, and its acks echo: of the named peer's copy, the one a refill digests;
        // of a made-up copy when the peer is no machine of the cluster or does not hold the database.
        public async Task<Features.Replication.V1.Database> WireAsync(TenantContext database)
        {
            if (!_incarnations.TryGetValue(database, out var incarnation))
            {
                _incarnations[database] = incarnation = (_peer is null ? null : await _peer.HeldIncarnationAsync(database)) ?? Ulid.NewUlid();
            }

            return ChangeGroups.Wire(database, incarnation);
        }

        // From now on this stream's frames name `incarnation` for the database: the sender was copied or restored.
        public void Pin(TenantContext database, Ulid incarnation) => _incarnations[database] = incarnation;

        public async Task RefillAsync(TenantContext database, ulong changeId) =>
            await _call.RequestStream.WriteAsync(new PushFrame { Refill = new Refill { Database = await WireAsync(database), ChangeId = changeId } });

        // One group putting {"n":1} under `id`.
        public async Task BatchAsync(TenantContext database, ulong changeId, string id, ulong? stamp = null)
        {
            LastStamp = stamp ?? Stamp();
            await _call.RequestStream.WriteAsync(new PushFrame
            {
                Batch = new Batch
                {
                    Database = await WireAsync(database),
                    LastChangeId = changeId,
                    Groups = ChangeGroups.Encode([new OriginGroup((long)changeId, [Put(_origin, LastStamp, id, """{"n":1}""")])]),
                },
            });
        }

        // The groups as one batch at the last one's change id.
        public async Task GroupsAsync(TenantContext database, IReadOnlyList<OriginGroup> groups, ulong expected = 0) =>
            await _call.RequestStream.WriteAsync(new PushFrame
            {
                Batch = new Batch { Database = await WireAsync(database), LastChangeId = (ulong)groups[^1].ChangeId, Groups = ChangeGroups.Encode(groups), Expect = expected },
            });

        // Whether the next answer is the heartbeat's: an empty frame.
        public async Task<bool> HeartbeatAsync()
        {
            await _call.RequestStream.WriteAsync(new PushFrame { Heartbeat = new Heartbeat() });
            using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
            return await _call.ResponseStream.MoveNext(timeout.Token) && _call.ResponseStream.Current.KindCase == AckFrame.KindOneofCase.None;
        }

        // The next ack, within the sender's progress timeout.
        public async Task<Ack> AckAsync()
        {
            using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
            return await _call.ResponseStream.MoveNext(timeout.Token) ? _call.ResponseStream.Current.Ack : throw new IOException("the stream ended");
        }

        // A frontier of the database at `hlc`; it has no answer of its own until its stable report.
        public async Task FrontierAsync(TenantContext database, ulong hlc) =>
            await _call.RequestStream.WriteAsync(new PushFrame { Frontier = new Frontier { Database = await WireAsync(database), Hlc = hlc } });

        // A batch whose groups are no application envelope: it is refused, once its database's frontier in progress ended.
        public async Task MalformedBatchAsync(TenantContext database, ulong changeId) =>
            await _call.RequestStream.WriteAsync(new PushFrame
            {
                Batch = new Batch { Database = await WireAsync(database), LastChangeId = changeId, Groups = ByteString.CopyFromUtf8("not an envelope") },
            });

        // The next answer of any kind, within the sender's progress timeout.
        public async Task<AckFrame> FrameAsync()
        {
            using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
            return await _call.ResponseStream.MoveNext(timeout.Token) ? _call.ResponseStream.Current : throw new IOException("the stream ended");
        }

        public void Dispose()
        {
            _call.Dispose();
            _channel.Dispose();
        }
    }

    /// <summary>The system clock moved forward by <see cref="Offset"/>; timers keep real time, so streams keep running.</summary>
    internal sealed class ShiftedTime : TimeProvider
    {
        private long _offset;
        private long _timestamp;
        private long _timestampAdvance;

        public TimeSpan Offset
        {
            get => TimeSpan.FromTicks(Interlocked.Read(ref _offset));
            set => Interlocked.Exchange(ref _offset, value.Ticks);
        }

        // Freeze only after initial peer discovery and shipping; UTC and timers keep advancing normally.
        public void FreezeTimestamp() => Interlocked.Exchange(ref _timestamp, System.GetTimestamp());

        public void AdvanceTimestamp(TimeSpan elapsed) => Interlocked.Add(ref _timestampAdvance, (long)(elapsed.TotalSeconds * TimestampFrequency));

        public override long GetTimestamp()
        {
            var frozen = Interlocked.Read(ref _timestamp);
            return (frozen > 0 ? frozen : System.GetTimestamp()) + Interlocked.Read(ref _timestampAdvance);
        }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }
}
