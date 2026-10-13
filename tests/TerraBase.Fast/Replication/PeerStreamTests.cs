using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
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
    public async Task A_malformed_batch_ends_the_stream_and_an_unplaced_database_is_refused()
    {
        await using var cluster = await Cluster.StartAsync("gru");
        var gru = cluster["gru"];
        using var channel = PeerChannel.Create(gru.Api.Server.BaseAddress);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var origin = ReplicaId.Of(1, 7, 0);
        var other = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        using var call = client.Push();
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome.Node).IsEqualTo(gru.Node.Value);

        // A database this machine does not hold: refused, nothing applied, and the stream goes on.
        var delta = Put(origin, Stamp(), EntityIds.New().ToString(), """{"n":1}""");
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = ChangeGroups.Wire(other), LastChangeId = 1, Groups = ChangeGroups.Encode([new OriginGroup(1, [delta])]) } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Ack.Refused).IsTrue();

        // Bytes that are no LZ4 envelope of groups: the receiver ends the stream.
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = ChangeGroups.Wire(cluster.Database), LastChangeId = 1, Groups = ByteString.CopyFromUtf8("forged") } });
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

        var groups = await gru.Groups.ReadAsync(cluster.Database, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, stale: false, CancellationToken.None);
        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].Deltas.Select(delta => $"{delta.Collection}/{delta.Id}").Order(StringComparer.Ordinal))
            .IsEquivalentTo(new[] { $"{DocumentDelta.Collections}/items", $"items/{first}", $"items/{second}" }.Order(StringComparer.Ordinal));
        var delta = groups[0].Deltas.Single(delta => delta.Id == first);
        await Assert.That(Encoding.UTF8.GetString(delta.State.Materialize()!)).IsEqualTo("""{"n":3}""");
        await Assert.That(delta.State.MaxStamp).IsEqualTo(delta.Stamp);

        // sjc joined both documents: its own tailer finds no group to send back, though its CDC holds the joined rows.
        await Assert.That(await sjc.UntilAsync(first, """{"n":3}""")).IsTrue();
        await Assert.That(await sjc.Groups.ReadAsync(cluster.Database, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, stale: false, CancellationToken.None)).IsEmpty();

        // sjc acked the group, but gru keeps it for 60 s: a peer whose stream has not reached the database yet needs it.
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, groups[0].ChangeId)).IsTrue();
        await Assert.That((await gru.Groups.ReadAsync(cluster.Database, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, stale: false, CancellationToken.None)).Count).IsEqualTo(1);

        // Two minutes on, the next acknowledged group drops everything below its commit: nothing is left to send.
        clock.Offset = TimeSpan.FromMinutes(2);
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", second, Json("""{"m":2}"""), CancellationToken.None));
        await Assert.That(await sjc.UntilAsync(second, """{"m":2}""")).IsTrue();
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, groups[0].ChangeId + 1)).IsTrue();
        await Assert.That(await gru.Groups.ReadAsync(cluster.Database, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, stale: false, CancellationToken.None)).IsEmpty();
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
            var page = await gru.Groups.ReadAsync(cluster.Database, after, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, stale: false, CancellationToken.None);
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

        // sjc leaves the list (as Fly's DNS drops a stopped machine): within a heartbeat gru closes its open stream to it.
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

    // The newest CDC rows of a machine that only received since its last own write hold nothing to ship; the peer's
    // acknowledgement still moves past them, so retention can drop them (they would otherwise stay until the next own
    // write, or until the peer is cut as 1 GiB behind and made to refill).
    [Test]
    public async Task A_peers_acknowledgement_moves_past_groups_this_machine_only_received()
    {
        await using var cluster = await Cluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (mine, theirs) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", mine, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(mine, """{"n":1}""")).IsTrue();

        await sjc.WriteAsync(async transaction => _ = await transaction.PutAsync("items", theirs, Json("""{"m":2}"""), CancellationToken.None));
        await Assert.That(await gru.UntilAsync(theirs, """{"m":2}""")).IsTrue();
        await Assert.That(await gru.UntilAckedByAsync(cluster.Database, sjc.Node)).IsTrue();
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
        await gru.WriteAsync(async transaction => await transaction.SetUniqueConstraintsAsync("items", [new UniqueConstraint("email", ["email"])], CancellationToken.None));
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
        await Assert.That(await sjc.Groups.ReadAsync(cluster.Database, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, stale: false, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task A_peer_silent_for_24_hours_stops_holding_change_groups_and_refills_when_it_returns()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (first, second) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(first, """{"n":1}""")).IsTrue();
        var sjcNode = sjc.Node;
        var acked = await gru.Groups.AckedAsync(cluster.Database, sjcNode, CancellationToken.None);

        // Written while sjc is away; 25 hours later sjc no longer holds back retention, so gru drops the group sjc lacks.
        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", second, Json("""{"n":2}"""), CancellationToken.None));
        await Assert.That(await gru.Groups.RetainsAsync(cluster.Database, acked, CancellationToken.None)).IsTrue();
        clock.Offset = TimeSpan.FromHours(25);
        var deadline = DateTime.UtcNow + Deadline;
        while (await gru.Groups.RetainsAsync(cluster.Database, acked, CancellationToken.None) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        await Assert.That(await gru.Groups.RetainsAsync(cluster.Database, acked, CancellationToken.None)).IsFalse();

        // Back, sjc cannot get the group from the stream any more: gru tells it to refill, and it joins what differs.
        await sjc.StartAsync();
        await Assert.That(await sjc.UntilAsync(second, """{"n":2}""")).IsTrue();
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.Groups.HeadAsync(cluster.Database, CancellationToken.None))).IsTrue();
    }

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

        // sjc goes away holding the live document; gru deletes it. A day later sjc no longer holds back gru's horizon, so gru
        // collects the tombstone: nothing on gru remembers the document ever existed.
        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", deleted, CancellationToken.None));
        await Assert.That(await gru.HasStateAsync("items", deleted)).IsTrue();
        clock.Offset = TimeSpan.FromHours(25);
        await Assert.That(await gru.UntilNoStateAsync("items", deleted)).IsTrue();

        // sjc comes back with its old live copy. Its refill copies rather than merges, and neither side's repairs bring the
        // document back, whatever the rounds pull from the other.
        await sjc.StartAsync();
        await Assert.That(await sjc.UntilAbsentAsync(deleted)).IsTrue();
        for (var round = 0; round < 3; round++)
        {
            await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
            await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        }

        await Assert.That(await gru.ReadAsync(deleted)).IsNull();
        await Assert.That(await gru.HasStateAsync("items", deleted)).IsFalse();
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
        var deletion = (await gru.StateAsync("items", id)).MaxStamp;
        clock.Offset = TimeSpan.FromMinutes(10);
        await Assert.That(await gru.UntilStableAsync(new Hlc(deletion.Value + (120_000UL << 16)))).IsTrue().Because("sjc still streams to gru");
        await Task.Delay(TimeSpan.FromSeconds(6));
        await Assert.That(await gru.HasStateAsync("items", id)).IsTrue();

        // Listed again, sjc applies the delete and reports stable stamps past it: both collect the tombstone.
        cluster.Members.Join(member);
        await Assert.That(await sjc.UntilAbsentAsync(id)).IsTrue();
        clock.Offset = TimeSpan.FromMinutes(20);
        await Assert.That(await gru.UntilNoStateAsync("items", id)).IsTrue();
        await Assert.That(await sjc.UntilNoStateAsync("items", id)).IsTrue();
    }

    [Test]
    public async Task A_stale_queued_edit_of_a_document_deleted_and_collected_meanwhile_loses_to_the_deletion_everywhere()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (deleted, other) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", deleted, Json("""{"a":"base"}"""), CancellationToken.None);
            _ = await transaction.PutAsync("items", other, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(deleted, """{"a":"base"}""")).IsTrue();

        // sjc edits the document while it cannot ship (gru is not in its list), then goes away with the edit queued in its CDC.
        await sjc.StaleEditAsync(cluster, deleted);

        // gru deletes the document, newer than sjc's edit; a day later it collects the tombstone.
        await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", deleted, CancellationToken.None));
        clock.Offset = TimeSpan.FromHours(25);
        await Assert.That(await gru.UntilNoStateAsync("items", deleted)).IsTrue();

        // sjc restarts: its queued edit ships (stale: a whole state), and gru's refill copies to it. The newer deletion wins
        // everywhere; nothing re-stamps the edit to win.
        await sjc.StartAsync();
        await Assert.That(await sjc.UntilAckedByAsync(cluster.Database, gru.Node)).IsTrue();
        await Assert.That(await sjc.UntilAbsentAsync(deleted)).IsTrue();
        await Assert.That(await gru.ReadAsync(deleted)).IsNull();
        await Assert.That(await gru.HasStateAsync("items", deleted)).IsFalse();

        // Still after gru restarts and both sides run repair rounds.
        await gru.StopAsync();
        await gru.StartAsync();
        for (var round = 0; round < 3; round++)
        {
            await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
            await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        }

        await Assert.That(await gru.ReadAsync(deleted)).IsNull();
        await Assert.That(await sjc.ReadAsync(deleted)).IsNull();
        await Assert.That(await gru.ReadAsync(other)).IsEqualTo("""{"n":1}""");
        await Assert.That(await sjc.ReadAsync(other)).IsEqualTo("""{"n":1}""");
    }

    [Test]
    public async Task A_stale_queued_create_of_a_document_no_peer_ever_held_reaches_every_region()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var created = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction => await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None));
        var deadline = DateTime.UtcNow + Deadline;
        while (await sjc.CollectionIdAsync("items") is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // sjc creates a document while it cannot ship, and goes away a day; no other region ever held it, so it is not a
        // deletion's loser: it ships and stays.
        await sjc.StaleEditAsync(cluster, created, create: true);
        clock.Offset = TimeSpan.FromHours(25);
        await sjc.StartAsync();
        await Assert.That(await gru.UntilAsync(created, """{"a":"stale"}""")).IsTrue();
        for (var round = 0; round < 3; round++)
        {
            await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
            await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        }

        await Assert.That(await sjc.UntilAsync(created, """{"a":"stale"}""")).IsTrue();
        await Assert.That(await gru.ReadAsync(created)).IsEqualTo("""{"a":"stale"}""");
    }

    [Test]
    public async Task A_refill_that_fails_for_one_database_does_not_hold_back_the_others_on_the_stream()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (first, second) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        foreach (var database in new[] { cluster.Database, cluster.Second })
        {
            await gru.WriteAsync(database, async transaction =>
            {
                _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
                _ = await transaction.PutAsync("items", first, Json("""{"n":1}"""), CancellationToken.None);
            });
            await Assert.That(await sjc.UntilAsync(database, first, """{"n":1}""")).IsTrue();
        }

        // Both databases get a write while sjc is away a day, so gru no longer holds what sjc lacks: both need a refill.
        var sjcNode = sjc.Node;
        await sjc.StopAsync();
        foreach (var database in new[] { cluster.Database, cluster.Second })
        {
            var acked = await gru.Groups.AckedAsync(database, sjcNode, CancellationToken.None);
            await gru.WriteAsync(database, async transaction => await transaction.PutAsync("items", second, Json("""{"n":2}"""), CancellationToken.None));
            clock.Offset = TimeSpan.FromHours(25);
            var deadline = DateTime.UtcNow + Deadline;
            while (await gru.Groups.RetainsAsync(database, acked, CancellationToken.None) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            await Assert.That(await gru.Groups.RetainsAsync(database, acked, CancellationToken.None)).IsFalse();
        }

        // The database gru ships first cannot refill on sjc. The other one still refills over the same stream.
        var stuck = await gru.Groups.ListDatabasesAsync(CancellationToken.None).FirstAsync(database => database == cluster.Database || database == cluster.Second);
        var other = stuck == cluster.Database ? cluster.Second : cluster.Database;
        cluster.FailDigests = stuck;
        await sjc.StartAsync();
        await Assert.That(await sjc.UntilAsync(other, second, """{"n":2}""")).IsTrue();
        await Assert.That(await sjc.ReadAsync(stuck, second)).IsNull();

        // Once the fault clears, the refused refill is tried again and the stuck database catches up too.
        cluster.FailDigests = null;
        await Assert.That(await sjc.UntilAsync(stuck, second, """{"n":2}""")).IsTrue();
    }

    // A partition, not an outage: gru and sjc keep running (and listed) through a cut longer than a day, and each writes a
    // document meanwhile. Their CDC retention gives up on the other, so each refills the other on the heal; a refill drops only
    // what its source collected, never what its source has not received yet, so both acknowledged writes survive everywhere.
    [Test]
    public async Task Writes_made_on_both_sides_of_a_partition_longer_than_a_day_survive_the_refills_and_reach_both_sides()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (onGru, onSjc, before) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", before, Json("""{"n":0}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(before, """{"n":0}""")).IsTrue();
        await Assert.That(await gru.UntilAckedByAsync(cluster.Database, sjc.Node)).IsTrue();
        await Assert.That(await sjc.UntilAckedByAsync(cluster.Database, gru.Node)).IsTrue();

        cluster.Cut("gru", "sjc");
        await Assert.That(await gru.UntilStreamsAsync([])).IsTrue();
        await Assert.That(await sjc.UntilStreamsAsync([])).IsTrue();
        var gruAcked = await gru.Groups.AckedAsync(cluster.Database, sjc.Node, CancellationToken.None);
        var sjcAcked = await sjc.Groups.AckedAsync(cluster.Database, gru.Node, CancellationToken.None);
        await gru.WriteAsync(async transaction => _ = await transaction.PutAsync("items", onGru, Json("""{"n":1}"""), CancellationToken.None));
        await sjc.WriteAsync(async transaction => _ = await transaction.PutAsync("items", onSjc, Json("""{"n":2}"""), CancellationToken.None));

        clock.Offset = TimeSpan.FromHours(25);
        var deadline = DateTime.UtcNow + Deadline;
        while ((await gru.Groups.RetainsAsync(cluster.Database, gruAcked, CancellationToken.None)
            || await sjc.Groups.RetainsAsync(cluster.Database, sjcAcked, CancellationToken.None)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        await Assert.That(await gru.Groups.RetainsAsync(cluster.Database, gruAcked, CancellationToken.None)).IsFalse();
        await Assert.That(await sjc.Groups.RetainsAsync(cluster.Database, sjcAcked, CancellationToken.None)).IsFalse();

        cluster.Heal();
        await Assert.That(await gru.UntilAckedByAsync(cluster.Database, sjc.Node)).IsTrue();
        await Assert.That(await sjc.UntilAckedByAsync(cluster.Database, gru.Node)).IsTrue();
        for (var round = 0; round < 3; round++)
        {
            await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
            await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        }

        var seen = $"gru: onGru={await gru.ReadAsync(onGru) ?? "missing"} onSjc={await gru.ReadAsync(onSjc) ?? "missing"}; sjc: onGru={await sjc.ReadAsync(onGru) ?? "missing"} onSjc={await sjc.ReadAsync(onSjc) ?? "missing"}";
        await Assert.That(seen).IsEqualTo("""gru: onGru={"n":1} onSjc={"n":2}; sjc: onGru={"n":1} onSjc={"n":2}""");
    }

    // The same partition with a deletion: sjc edits the document, then gru deletes it. sjc stays listed, so gru keeps the
    // tombstone however long the cut lasts (it cannot know sjc holds no older version), and the newer deletion wins on both
    // sides after the heal.
    [Test]
    public async Task A_region_cut_off_but_listed_holds_back_collection_and_the_newer_deletion_wins_after_the_heal()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var deleted = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", deleted, Json("""{"a":"base"}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(deleted, """{"a":"base"}""")).IsTrue();

        cluster.Cut("gru", "sjc");
        await Assert.That(await gru.UntilStreamsAsync([])).IsTrue();
        await Assert.That(await sjc.UntilStreamsAsync([])).IsTrue();
        using var value = JsonDocument.Parse("\"stale\"");
        var stale = value.RootElement.Clone();
        await sjc.WriteAsync(async transaction =>
            _ = await transaction.PatchAsync("items", deleted, Json("{}"), [new FieldOperation(FieldOperationKind.Set, "a", stale)], CancellationToken.None));
        clock.Offset = TimeSpan.FromHours(1);
        await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", deleted, CancellationToken.None));
        clock.Offset = TimeSpan.FromHours(26);
        await Task.Delay(TimeSpan.FromSeconds(12));
        await Assert.That(await gru.HasStateAsync("items", deleted)).IsTrue().Because("sjc is listed: only cut off, it holds gru's horizon");

        cluster.Heal();
        await Assert.That(await gru.UntilAckedByAsync(cluster.Database, sjc.Node)).IsTrue();
        await Assert.That(await sjc.UntilAckedByAsync(cluster.Database, gru.Node)).IsTrue();
        for (var round = 0; round < 3; round++)
        {
            await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
            await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        }

        await Assert.That(await gru.ReadAsync(deleted)).IsNull().Because("gru deleted it after sjc's edit");
        await Assert.That(await sjc.ReadAsync(deleted)).IsNull().Because("gru deleted it after sjc's edit");
    }

    // sjc, away from -30 h, runs briefly at -10 h cut off from gru, edits, and stops again; gru deletes at -8 h. Whether gru
    // saw that brief run in its machine list or not (it decides whether gru evicts sjc and collects the tombstone by +1 h),
    // the edit, 11 h old when it ships, never brings the document back over the newer deletion.
    [Test]
    public async Task A_queued_edit_younger_than_a_day_never_comes_back_over_a_newer_deletion()
    {
        var clock = new ShiftedTime { Offset = TimeSpan.FromHours(-30) };
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var deleted = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", deleted, Json("""{"a":"base"}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(deleted, """{"a":"base"}""")).IsTrue();
        await Assert.That(await gru.UntilAckedByAsync(cluster.Database, sjc.Node)).IsTrue();

        await sjc.StopAsync();
        cluster.Cut("gru", "sjc");
        clock.Offset = TimeSpan.FromHours(-10);
        await sjc.StartAsync();
        using var value = JsonDocument.Parse("\"stale\"");
        var stale = value.RootElement.Clone();
        await sjc.WriteAsync(async transaction =>
            _ = await transaction.PatchAsync("items", deleted, Json("{}"), [new FieldOperation(FieldOperationKind.Set, "a", stale)], CancellationToken.None));
        await sjc.StopAsync();

        clock.Offset = TimeSpan.FromHours(-8);
        await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", deleted, CancellationToken.None));
        clock.Offset = TimeSpan.FromHours(1);
        await Task.Delay(TimeSpan.FromSeconds(12));

        cluster.Heal();
        await sjc.StartAsync();
        await Assert.That(await sjc.UntilAckedByAsync(cluster.Database, gru.Node)).IsTrue();
        for (var round = 0; round < 3; round++)
        {
            await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
            await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        }

        await Assert.That(await gru.ReadAsync(deleted)).IsNull().Because("gru deleted it 2 h after sjc's edit");
        await Assert.That(await sjc.UntilAbsentAsync(deleted)).IsTrue().Because("gru deleted it 2 h after sjc's edit");
    }

    // The receiver decides, not the group's age: once gru collected a deletion, a delta stamped before it for a document gru
    // no longer holds is refused until it comes as a whole state. That state loses to the deletion where gru held the
    // document, and joins where gru never did; a delta newer than every collection joins as usual.
    [Test]
    public async Task A_delta_older_than_a_collected_deletion_is_judged_by_the_receivers_watermark_as_a_whole_state()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (deleted, created, fresh) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", deleted, Json("""{"a":"base"}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(deleted, """{"a":"base"}""")).IsTrue();
        var held = await gru.StateAsync("items", deleted);
        var old = (ulong)clock.GetUtcNow().ToUnixTimeMilliseconds() << 16;
        await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", deleted, CancellationToken.None));
        await Assert.That(await sjc.UntilAbsentAsync(deleted)).IsTrue();
        clock.Offset = TimeSpan.FromMinutes(5);
        await Assert.That(await gru.UntilNoStateAsync("items", deleted)).IsTrue();

        // A machine gru never heard from ships an edit stamped before the deletion, as a plain delta.
        var origin = ReplicaId.Of(0, 0x7777, 0);
        using var channel = PeerChannel.Create(gru.Api.Server.BaseAddress);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "fra", Machine = "forged" } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        using var value = JsonDocument.Parse("\"stale\"");
        var at = new EntryVersion(new Hlc(old), origin);
        var edit = DocumentState.Bottom.Set(at, "a", value.RootElement);
        async Task<Ack> SendAsync(ulong changeId, ChangeDelta delta, bool whole)
        {
            await call.RequestStream.WriteAsync(new PushFrame
            {
                Batch = new Batch { Database = ChangeGroups.Wire(cluster.Database), LastChangeId = changeId, Groups = ChangeGroups.Encode([new OriginGroup((long)changeId, [delta], whole)]) },
            });
            await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
            return call.ResponseStream.Current.Ack;
        }

        var refused = await SendAsync(1, new ChangeDelta("items", deleted, at.Stamp, edit), whole: false);
        await Assert.That(refused is { Refused: true, Stale: true }).IsTrue();
        await Assert.That(await gru.HasStateAsync("items", deleted)).IsFalse();

        // As the whole state that machine held (gru's document plus the edit) it loses: gru held the document and collected it.
        var applied = await SendAsync(2, new ChangeDelta("items", deleted, at.Stamp, held.Join(edit)), whole: true);
        await Assert.That(applied.Refused).IsFalse();
        await Assert.That(await gru.ReadAsync(deleted)).IsNull();
        await Assert.That(await gru.HasStateAsync("items", deleted)).IsFalse();

        // A document gru never held, created as long ago: refused as a delta, kept as a whole state.
        var create = Put(origin, old + 1, created, """{"n":1}""");
        await Assert.That((await SendAsync(3, create, whole: false)) is { Refused: true, Stale: true }).IsTrue();
        await Assert.That((await SendAsync(4, create, whole: true)).Refused).IsFalse();
        await Assert.That(await gru.ReadAsync(created)).IsEqualTo("""{"n":1}""");

        // A delta newer than every collection joins as a delta.
        await Assert.That((await SendAsync(5, Put(origin, (ulong)clock.GetUtcNow().ToUnixTimeMilliseconds() << 16, fresh, """{"n":2}"""), whole: false)).Refused).IsFalse();
        await Assert.That(await gru.ReadAsync(fresh)).IsEqualTo("""{"n":2}""");
    }

    // Three regions; only the gru-fra link is cut, for more than a day, and fra stays listed. gru then deletes a document fra
    // holds live; sjc gets the deletion. fra still counts at sjc (its reported stable never passes gru's deletion), so sjc
    // keeps the tombstone, and nothing fra holds pulls the document back; after the heal the deletion reaches fra too.
    [Test]
    public async Task A_third_region_keeps_a_tombstone_a_listed_region_lacks_and_never_pulls_the_document_back()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc", "fra");
        var (gru, sjc, fra) = (cluster["gru"], cluster["sjc"], cluster["fra"]);
        var deleted = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", deleted, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(deleted, """{"n":1}""")).IsTrue();
        await Assert.That(await fra.UntilAsync(deleted, """{"n":1}""")).IsTrue();

        cluster.Cut("gru", "fra");
        await Assert.That(await gru.UntilStreamsAsync(["sjc"])).IsTrue();
        await Assert.That(await fra.UntilStreamsAsync(["sjc"])).IsTrue();
        clock.Offset = TimeSpan.FromHours(25);
        await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", deleted, CancellationToken.None));
        await Assert.That(await sjc.UntilAbsentAsync(deleted)).IsTrue();
        clock.Offset = TimeSpan.FromHours(25) + TimeSpan.FromMinutes(3);
        await Task.Delay(TimeSpan.FromSeconds(12));
        await Assert.That(await sjc.HasStateAsync("items", deleted)).IsTrue().Because("fra lacks the deletion and is listed");
        for (var round = 0; round < 3; round++)
        {
            await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        }

        await Assert.That(await sjc.ReadAsync(deleted)).IsNull();

        cluster.Heal();
        await Assert.That(await fra.UntilAbsentAsync(deleted)).IsTrue();
        await Assert.That(await gru.ReadAsync(deleted)).IsNull();
        await Assert.That(await sjc.ReadAsync(deleted)).IsNull();
    }

    // R6's restore of an older copy, after a collection: sjc is restored to a copy taken before gru's deletion, once both
    // collected its tombstone. sjc applied less from gru than it once reported, so gru refills it before anything else applies
    // there; the refill drops the document gru collected, and gru never pulls it back.
    [Test]
    public async Task A_region_restored_to_a_copy_from_before_a_collected_deletion_refills_and_never_brings_the_document_back()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var deleted = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", deleted, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(deleted, """{"n":1}""")).IsTrue();
        await Assert.That(await gru.UntilAckedByAsync(cluster.Database, sjc.Node)).IsTrue();

        await sjc.StopAsync();
        var copy = Directory.CreateTempSubdirectory("terrabase-peer-streams-copy-").FullName;
        try
        {
            CopyDirectory(sjc.DataDirectory, copy);
            await sjc.StartAsync();

            await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", deleted, CancellationToken.None));
            await Assert.That(await sjc.UntilAbsentAsync(deleted)).IsTrue();
            clock.Offset = TimeSpan.FromMinutes(5);
            await Assert.That(await gru.UntilNoStateAsync("items", deleted)).IsTrue();
            await Assert.That(await sjc.UntilNoStateAsync("items", deleted)).IsTrue();

            await sjc.StopAsync();
            Directory.Delete(sjc.DataDirectory, recursive: true);
            CopyDirectory(copy, sjc.DataDirectory);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }

        await sjc.StartAsync();
        await Assert.That(await sjc.UntilAbsentAsync(deleted)).IsTrue();
        await Assert.That(await sjc.UntilAckedByAsync(cluster.Database, gru.Node)).IsTrue();
        for (var round = 0; round < 3; round++)
        {
            await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
            await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        }

        await Assert.That($"gru={await gru.ReadAsync(deleted) ?? "missing"} sjc={await sjc.ReadAsync(deleted) ?? "missing"}").IsEqualTo("gru=missing sjc=missing");
    }

    // gru deletes a document while sjc is away; a day later gru and fra restart with sjc still away. sjc is silent and
    // unlisted, but neither has itself been up a day, so neither can tell sjc gone from sjc missed: gru keeps the tombstone
    // sjc lacks (and fra, which restarted too, neither collects it nor refills gru past it).
    [Test]
    public async Task A_machine_back_after_a_day_keeps_a_tombstone_its_away_peer_lacks_until_it_has_itself_been_up_a_day()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc", "fra");
        var (gru, sjc, fra) = (cluster["gru"], cluster["sjc"], cluster["fra"]);
        var deleted = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", deleted, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(deleted, """{"n":1}""")).IsTrue();
        await Assert.That(await fra.UntilAsync(deleted, """{"n":1}""")).IsTrue();

        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", deleted, CancellationToken.None));
        await Assert.That(await fra.UntilAbsentAsync(deleted)).IsTrue();
        await gru.StopAsync();
        await fra.StopAsync();
        clock.Offset = TimeSpan.FromHours(25);
        await gru.StartAsync();
        await fra.StartAsync();
        await Assert.That(await gru.UntilStreamsAsync(["fra"])).IsTrue();
        await Assert.That(await fra.UntilStreamsAsync(["gru"])).IsTrue();
        // Frontiers go at once on a new stream and collection runs every 5 s: a horizon free of sjc would have collected by now.
        await Task.Delay(TimeSpan.FromSeconds(12));
        await Assert.That(await gru.HasStateAsync("items", deleted)).IsTrue();
    }

    // A deleted document's row records the last operation joined into it, which can be older than its newest entry (an
    // older delta joined after a newer one). Collection reads the entries, not the row: the state waits for its newest entry.
    [Test]
    public async Task A_deleted_state_waits_for_its_newest_entry_even_when_an_older_join_came_last()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (late, control) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction => _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None));
        await Assert.That(await sjc.UntilCollectionAsync("items")).IsTrue();
        ulong Ago(TimeSpan age)
        {
            return (ulong)(clock.GetUtcNow() - age).ToUnixTimeMilliseconds() << 16;
        }

        ChangeDelta Op(string id, ulong stamp, Func<DocumentState, EntryVersion, DocumentState> op)
        {
            var at = new EntryVersion(new Hlc(stamp), gru.Node);
            return new ChangeDelta("items", id, at.Stamp, op(DocumentState.Bottom, at).DeltaAt(at.Stamp));
        }

        // `late`: deleted 9 min ago, a field unset 10 s ago, then another unset 5 min ago joined last. `control`: deleted 5 min ago.
        await gru.CommitAsync(Put(gru, Ago(TimeSpan.FromMinutes(10)), late, """{"f":1,"g":2}"""), Put(gru, Ago(TimeSpan.FromMinutes(10)), control, """{"n":1}"""));
        await gru.CommitAsync(Op(late, Ago(TimeSpan.FromMinutes(9)), (state, at) => state.Delete(at)), Op(control, Ago(TimeSpan.FromMinutes(5)), (state, at) => state.Delete(at)));
        await gru.CommitAsync(Op(late, Ago(TimeSpan.FromSeconds(10)), (state, at) => state.Unset(at, "f")));
        await gru.CommitAsync(Op(late, Ago(TimeSpan.FromMinutes(5)), (state, at) => state.Unset(at, "g")));

        // The horizon passes 5 min ago (the control goes) but not 10 s ago; one more collection pass changes nothing.
        await Assert.That(await gru.UntilNoStateAsync("items", control)).IsTrue();
        await Task.Delay(TimeSpan.FromSeconds(6));
        await Assert.That(await gru.HasStateAsync("items", late)).IsTrue();
        clock.Offset = TimeSpan.FromMinutes(3);
        await Assert.That(await gru.UntilNoStateAsync("items", late)).IsTrue();
    }

    // sjc loses a document behind the streams' back (a lost apply, not a collection) and gru refills from sjc: the document
    // is above sjc's collection watermark, so sjc never collected it, and the refill keeps it; anti-entropy gives it back.
    [Test]
    public async Task A_refill_keeps_a_document_its_source_lost_rather_than_collected_and_anti_entropy_gives_it_back()
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

        var basis = await sjc.Api.Server.Services.GetRequiredService<IReplicaStates>().RefillBasisAsync(cluster.Database, CancellationToken.None);
        await Assert.That(await gru.AntiEntropy.RefillAsync(cluster.Database, sjc.Name, basis!, CancellationToken.None)).IsNotNull();
        await Assert.That(await gru.ReadAsync(lost)).IsEqualTo("""{"n":1}""");

        await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await Assert.That(await sjc.ReadAsync(lost)).IsEqualTo("""{"n":1}""");
    }

    // sjc writes during a partition, its CDC gives up on gru a day later, then sjc stays away a day: gru evicts it and
    // collects past that write, which it never received. A refill of sjc from gru drops what gru collected, never the write
    // gru lacks because it never got it; once the link heals, the write reaches gru.
    [Test]
    public async Task A_refill_from_a_source_that_collected_past_a_write_it_never_received_keeps_that_write()
    {
        var clock = new ShiftedTime();
        await using var cluster = await Cluster.StartAsync(cdc: true, clock, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (deleted, written) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", deleted, Json("""{"n":1}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(deleted, """{"n":1}""")).IsTrue();
        await Assert.That(await gru.UntilAckedByAsync(cluster.Database, sjc.Node)).IsTrue();
        await Assert.That(await sjc.UntilAckedByAsync(cluster.Database, gru.Node)).IsTrue();

        cluster.Cut("gru", "sjc");
        await Assert.That(await sjc.UntilStreamsAsync([])).IsTrue();
        var sjcAcked = await sjc.Groups.AckedAsync(cluster.Database, gru.Node, CancellationToken.None);
        await sjc.WriteAsync(async transaction => _ = await transaction.PutAsync("items", written, Json("""{"n":2}"""), CancellationToken.None));
        clock.Offset = TimeSpan.FromHours(25);
        var deadline = DateTime.UtcNow + Deadline;
        while (await sjc.Groups.RetainsAsync(cluster.Database, sjcAcked, CancellationToken.None) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        await Assert.That(await sjc.Groups.RetainsAsync(cluster.Database, sjcAcked, CancellationToken.None)).IsFalse();
        await gru.WriteAsync(async transaction => await transaction.TombstoneAsync("items", deleted, CancellationToken.None));
        await sjc.StopAsync();
        clock.Offset = TimeSpan.FromHours(50);
        await Assert.That(await gru.UntilNoStateAsync("items", deleted)).IsTrue();

        // sjc is back but gru cannot reach it, so nothing pulls the write to gru before sjc refills from gru.
        cluster.Heal();
        cluster.CutOneWay("gru", "sjc");
        await sjc.StartAsync();
        var basis = await gru.Api.Server.Services.GetRequiredService<IReplicaStates>().RefillBasisAsync(cluster.Database, CancellationToken.None);
        await Assert.That(await sjc.AntiEntropy.RefillAsync(cluster.Database, gru.Name, basis!, CancellationToken.None)).IsNotNull();
        await Assert.That(await sjc.ReadAsync(written)).IsEqualTo("""{"n":2}""").Because("gru never received it");
        await Assert.That(await sjc.ReadAsync(deleted)).IsNull().Because("gru collected its deletion");

        cluster.Heal();
        await Assert.That(await gru.UntilAsync(written, """{"n":2}""")).IsTrue();
        await Assert.That(await gru.ReadAsync(deleted)).IsNull();
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // Anti-entropy's view of the replicas, failing to read one database's digests while asked to.
    private sealed class FailingDigests(IReplicaStates inner, Func<TenantContext?> failing) : IReplicaStates
    {
        public ValueTask<byte[]?> DigestAsync(TenantContext database, CancellationToken cancellationToken) =>
            failing() == database ? throw new IOException("a digest read that fails") : inner.DigestAsync(database, cancellationToken);

        public ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)> ReadBucketsAsync(
            TenantContext database, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken) =>
            inner.ReadBucketsAsync(database, buckets, after, maxStates, maxBytes, cancellationToken);

        public ValueTask<Hlc> StableAsync(TenantContext database, CancellationToken cancellationToken) => inner.StableAsync(database, cancellationToken);

        public ValueTask<RefillBasis?> RefillBasisAsync(TenantContext database, CancellationToken cancellationToken) => inner.RefillBasisAsync(database, cancellationToken);

        public ValueTask<int> ForgetAsync(TenantContext database, IReadOnlyList<(string Collection, string Id)> keys, RefillBasis basis, CancellationToken cancellationToken) =>
            inner.ForgetAsync(database, keys, basis, cancellationToken);

        public ValueTask AdoptAsync(TenantContext database, RefillBasis basis, CancellationToken cancellationToken) => inner.AdoptAsync(database, basis, cancellationToken);

        public ValueTask<Hlc?> AppliedFromAsync(TenantContext database, ReplicaId origin, CancellationToken cancellationToken) =>
            inner.AppliedFromAsync(database, origin, cancellationToken);

        public ValueTask<Hlc> ReportedStableAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken) =>
            inner.ReportedStableAsync(database, peer, cancellationToken);
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

    /// <summary>One machine per region, all listed in one fake DNS, each holding the same database.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        private readonly Dictionary<string, Machine> _machines = [with(StringComparer.Ordinal)];

        public FakeMemberStore Members { get; } = new();

        // Region pairs whose link is cut, both ways: they stay listed, so neither is gone, but nothing reaches across.
        private readonly HashSet<(string, string)> _cuts = [];

        public void Cut(string a, string b)
        {
            lock (_cuts)
            {
                _cuts.Add((a, b));
                _cuts.Add((b, a));
            }
        }

        // Only `viewer` loses its route to `region`: the other way still works.
        public void CutOneWay(string viewer, string region)
        {
            lock (_cuts)
            {
                _cuts.Add((viewer, region));
            }
        }

        public void Heal()
        {
            lock (_cuts)
            {
                _cuts.Clear();
            }
        }

        private bool IsCut(string viewer, string region)
        {
            lock (_cuts)
            {
                return _cuts.Contains((viewer, region));
            }
        }

        public Cluster()
        {
            // A cut pair lists each other without a peer address: neither streams nor repairs reach across.
            Members.Map = (viewer, member) =>
                _machines.Values.FirstOrDefault(machine => machine.Name == viewer) is { } seer && IsCut(seer.Region, member.Region)
                    ? member with { PeerAddress = string.Empty }
                    : member;

            Database = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
            Second = Database with { DatabaseId = EntityIds.New().ToString() };
        }

        public TenantContext Database { get; }

        // A second database of the same project, placed like the first: what one database's trouble must not hold back.
        public TenantContext Second { get; }

        // While set, the machines' anti-entropy cannot read this database's digests: a refill of it fails.
        public TenantContext? FailDigests { get; set; }

        // Whether the machines keep their CDC tailer as the change group source, instead of a fake.
        public bool Cdc { get; private init; }

        // The machines' host clock, which stamps and CDC retention read, when a test moves it forward.
        public ShiftedTime? Clock { get; private init; }

        public IEnumerable<Machine> Machines => _machines.Values;

        public Machine this[string region] => _machines[region];

        public static Task<Cluster> StartAsync(params string[] regions) => StartAsync(cdc: false, regions);

        public static Task<Cluster> StartAsync(bool cdc, params string[] regions) => StartAsync(cdc, clock: null, regions);

        public static async Task<Cluster> StartAsync(bool cdc, ShiftedTime? clock, params string[] regions)
        {
            var cluster = new Cluster { Cdc = cdc, Clock = clock };
            try
            {
                foreach (var region in regions)
                {
                    var machine = new Machine(cluster, region, [.. regions.Where(other => other != region)]);
                    cluster._machines[region] = machine;
                    await machine.StartAsync();
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
                await machine.DisposeAsync();
            }
        }
    }

    private sealed class Machine(Cluster cluster, string region, string[] replicas) : IAsyncDisposable
    {
        public string Name { get; } = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];

        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("terrabase-peer-streams-").FullName;
        private ManagementApi? _api;

        public string Region { get; } = region;

        public FakeChangeGroupSource Source { get; } = new();

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{Region} is stopped");

        public ReplicaId Node => Api.Server.Services.GetRequiredService<LocalReplica>().Id;

        private IStorageEngine Engine => Api.Server.Services.GetRequiredService<IStorageEngine>();

        public async Task StartAsync()
        {
            var first = !File.Exists(Path.Combine(DataDirectory, "_system.db"));
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = Region;
                settings["node-name"] = Name;
                settings["data-directory"] = DataDirectory;
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(cluster.Members);
                if (!cluster.Cdc)
                {
                    services.AddSingleton<IChangeGroupSource>(Source);
                }
                else
                {
                    services.AddSingleton<IReplicaStates>(provider =>
                        new FailingDigests(provider.GetRequiredService<TerraBase.Engine.Sqlite.TursoChangeGroupSource>(), () => cluster.FailDigests));
                }
            }, time: cluster.Clock);
            if (first)
            {
                await RegisterAsync();
            }
        }

        // Stops this machine and waits until no running machine lists it any more: a test that moves the clock next must not
        // have a peer record it as listed at the moved time from a view read before it stopped.
        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
            foreach (var other in cluster.Machines.Where(machine => machine._api is not null))
            {
                var presence = other.Api.Server.Services.GetRequiredService<LocalReplica>().Presence;
                var deadline = DateTime.UtcNow + Deadline;
                while (presence.Lists(Name) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                }

                await Assert.That(presence.Lists(Name)).IsFalse();
            }
        }

        // The source the peer streams read: the CDC tailer, or the fake.
        public IChangeGroupSource Groups => Api.Server.Services.GetRequiredService<IChangeGroupSource>();

        public Task<IDatabaseStore> StoreAsync() => StoreAsync(cluster.Database);

        public async Task<IDatabaseStore> StoreAsync(TenantContext database) =>
            await Engine.OpenDatabaseAsync(database, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");

        // Commits the deltas here, as a local write does, then hands them to the fake source as one group (the CDC tailer
        // finds them itself).
        public async Task CommitAsync(params ChangeDelta[] deltas)
        {
            var store = await StoreAsync();
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
                _ = Source.Add(cluster.Database, deltas);
            }
        }

        // Runs `work` as one queued write of the database's group commit, as the document API does.
        public Task WriteAsync(Func<IStorageTransaction, Task> work) => WriteAsync(cluster.Database, work);

        public async Task WriteAsync(TenantContext database, Func<IStorageTransaction, Task> work)
        {
            var store = await StoreAsync(database);
            _ = await store.WriteAsync(async (transaction, _) =>
            {
                await work(transaction);
                return true;
            }, static _ => true, CancellationToken.None);
        }

        // Whether the document's materialized JSON becomes `expected` within the deadline.
        public Task<bool> UntilAsync(string id, string expected) => UntilAsync(cluster.Database, id, expected);

        public async Task<bool> UntilAsync(TenantContext database, string id, string expected)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                if (await ReadAsync(database, id) == expected)
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
            while (await Groups.AckedAsync(database, peer, CancellationToken.None) < changeId)
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

        public AntiEntropy AntiEntropy => Api.Server.Services.GetRequiredService<AntiEntropy>();

        // Deletes the document's rows from the file directly, as a lost apply would leave it: no change group says so.
        public async Task LoseAsync(string collection, string id)
        {
            var engine = Api.Server.Services.GetRequiredService<TerraBase.Engine.Sqlite.SqliteStorageEngine>();
            var store = (TerraBase.Engine.Sqlite.SqliteDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
            _ = await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = $"DELETE FROM \"{collection}\" WHERE id = $id; DELETE FROM _tb_state WHERE collection = $collection AND id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$collection", collection);
                return command.ExecuteNonQuery();
            }, "a test losing a document", CancellationToken.None);
            store.ForgetCollections();
        }

        // Whether this machine holds a state row of the document (live or a tombstone).
        public async Task<bool> HasStateAsync(string collection, string id)
        {
            var states = Api.Server.Services.GetRequiredService<IReplicaStates>();
            var bucket = TerraBase.Engine.Sqlite.SqliteStorageTransaction.Bucket(collection, id);
            var (page, _) = await states.ReadBucketsAsync(cluster.Database, [bucket], null, 4096, int.MaxValue, CancellationToken.None);
            return page.Any(state => state.Collection == collection && state.Id == id);
        }

        // The document's state on this machine, which must hold one.
        public async Task<DocumentState> StateAsync(string collection, string id)
        {
            var states = Api.Server.Services.GetRequiredService<IReplicaStates>();
            var bucket = TerraBase.Engine.Sqlite.SqliteStorageTransaction.Bucket(collection, id);
            var (page, _) = await states.ReadBucketsAsync(cluster.Database, [bucket], null, 4096, int.MaxValue, CancellationToken.None);
            return page.Single(state => state.Collection == collection && state.Id == id).State;
        }

        // Whether this machine's stable stamp of the database reaches `stamp` within the deadline.
        public async Task<bool> UntilStableAsync(Hlc stamp)
        {
            var states = Api.Server.Services.GetRequiredService<IReplicaStates>();
            var deadline = DateTime.UtcNow + Deadline;
            while ((await states.StableAsync(cluster.Database, CancellationToken.None)).Value < stamp.Value)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                await Task.Delay(200);
            }

            return true;
        }

        // Whether the collection exists here within the deadline.
        public async Task<bool> UntilCollectionAsync(string collection)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (await CollectionIdAsync(collection) is null)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                await Task.Delay(100);
            }

            return true;
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

        // Writes {"a":"stale"} to the document (a create, or an edit of field a) while this machine is cut off from every
        // peer (they leave its list, so no stream ships it), checks that no peer got it, then stops this machine and lists
        // the peers again: the write stays queued in its CDC until it returns.
        public async Task StaleEditAsync(Cluster cluster, string id, bool create = false)
        {
            var peers = (await cluster.Members.ListAsync(CancellationToken.None)).Where(member => member.Region != Region).ToList();
            peers.ForEach(member => cluster.Members.Leave(member.Name));
            await Assert.That(await UntilStreamsAsync([])).IsTrue();
            using var value = JsonDocument.Parse("\"stale\"");
            var stale = value.RootElement.Clone();
            await WriteAsync(async transaction => _ = create
                ? await transaction.PutAsync("items", id, Json("""{"a":"stale"}"""), CancellationToken.None)
                : await transaction.PatchAsync("items", id, Json("{}"), [new FieldOperation(FieldOperationKind.Set, "a", stale)], CancellationToken.None));
            await Task.Delay(TimeSpan.FromSeconds(2));
            foreach (var other in cluster.Machines.Where(machine => machine != this))
            {
                await Assert.That((await other.ReadAsync(id))?.Contains("stale", StringComparison.Ordinal) ?? false).IsFalse();
            }

            await StopAsync();
            peers.ForEach(cluster.Members.Join);
        }

        // Whether `peer` acknowledges every change group this machine has, within the deadline.
        public async Task<bool> UntilAckedByAsync(TenantContext database, ReplicaId peer)
        {
            var head = await Groups.HeadAsync(database, CancellationToken.None);
            return await UntilAckedAsync(database, peer, head);
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
            if (_api is not null)
            {
                await StopAsync();
            }

            Directory.Delete(DataDirectory, recursive: true);
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
                    new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "second", id = cluster.Second.DatabaseId, replica_regions = replicas },
                },
            });
            using var service = Api.Service(Api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>The system clock moved forward by <see cref="Offset"/>; timers keep real time, so streams keep running.</summary>
    private sealed class ShiftedTime : TimeProvider
    {
        private long _offset;

        public TimeSpan Offset
        {
            get => TimeSpan.FromTicks(Interlocked.Read(ref _offset));
            set => Interlocked.Exchange(ref _offset, value.Ticks);
        }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }
}
