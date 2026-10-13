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
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
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

        var groups = await gru.Groups.ReadAsync(cluster.Database, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None);
        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].Deltas.Select(delta => $"{delta.Collection}/{delta.Id}").Order(StringComparer.Ordinal))
            .IsEquivalentTo(new[] { $"{DocumentDelta.Collections}/items", $"items/{first}", $"items/{second}" }.Order(StringComparer.Ordinal));
        var delta = groups[0].Deltas.Single(delta => delta.Id == first);
        await Assert.That(Encoding.UTF8.GetString(delta.State.Materialize()!)).IsEqualTo("""{"n":3}""");
        await Assert.That(delta.State.MaxStamp).IsEqualTo(delta.Stamp);

        // sjc joined both documents: its own tailer finds no group to send back, though its CDC holds the joined rows.
        await Assert.That(await sjc.UntilAsync(first, """{"n":3}""")).IsTrue();
        await Assert.That(await sjc.Groups.ReadAsync(cluster.Database, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)).IsEmpty();

        // sjc acked the group, but gru keeps it for 60 s: a peer whose stream has not reached the database yet needs it.
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, groups[0].ChangeId)).IsTrue();
        await Assert.That((await gru.Groups.ReadAsync(cluster.Database, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)).Count).IsEqualTo(1);

        // Two minutes on, the next acknowledged group drops everything below its commit: nothing is left to send.
        clock.Offset = TimeSpan.FromMinutes(2);
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", second, Json("""{"m":2}"""), CancellationToken.None));
        await Assert.That(await sjc.UntilAsync(second, """{"m":2}""")).IsTrue();
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, groups[0].ChangeId + 1)).IsTrue();
        await Assert.That(await gru.Groups.ReadAsync(cluster.Database, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)).IsEmpty();
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
            var page = await gru.Groups.ReadAsync(cluster.Database, after, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None);
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
        await Assert.That(await sjc.Groups.ReadAsync(cluster.Database, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)).IsEmpty();
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
            await Assert.That(acknowledged.Database).IsEqualTo(ChangeGroups.Wire(other));
            await Assert.That(acknowledged is { ChangeId: 1, Refused: false }).IsTrue();
            await Assert.That(await sjc.ReadAsync(other, otherId)).IsEqualTo("""{"n":1}""");
        }
        finally
        {
            silent.Stop();
        }
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
            await second.BatchAsync(healthy, 1, id);
            foreach (var database in databases.Skip(4).Append(cluster.Database))
            {
                await Assert.That(await second.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(database), Refused = true })
                    .Because("excess refills are refused before starting work on another origin");
            }

            await Assert.That(await second.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(healthy), ChangeId = 1, Applied = second.LastStamp });
            await Assert.That(await sjc.ReadAsync(healthy, id)).IsEqualTo("""{"n":1}""");
            await Assert.That(entered.Count).IsEqualTo(4);
            await Assert.That(sjc.RefillMemory!.Reserved).IsEqualTo(4 * RefillMemory.BytesPerRefill);
            await second.BatchAsync(cluster.Database, 2, EntityIds.New().ToString());
            await Assert.That(await second.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database), Refused = true });

            held.SetResult();
            for (var index = 0; index < 4; index++)
            {
                await Assert.That((await first.AckAsync()).Refused).IsTrue();
            }

            await Assert.That(sjc.RefillMemory.Reserved).IsEqualTo(0);
            sjc.BeforeDigest = null;
            await second.RefillAsync(cluster.Database, 1);
            await Assert.That(await second.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database), ChangeId = 1 });
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
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database), Refused = true })
            .Because("a refill must reserve its bytes before allocating a digest or opening a channel");
        await Assert.That(digests).IsEqualTo(0);
        await Assert.That(sjc.RefillMemory.Reserved).IsEqualTo(0);
        var id = EntityIds.New().ToString();
        await push.BatchAsync(healthy, 1, id);
        await Assert.That((await push.AckAsync()) is { ChangeId: 1, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(healthy, id)).IsEqualTo("""{"n":1}""");

        sjc.RefillMemory.Refuse = false;
        await push.RefillAsync(cluster.Database, 1);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database), ChangeId = 1 });
        await Assert.That(digests).IsEqualTo(1);
        await Assert.That(sjc.RefillMemory.Reserved).IsEqualTo(0);
    }

    // Shutdown ends a push stream that has not finished its hello (F8): the handler returns at once and the call ends cleanly,
    // rather than holding the host's graceful drain until its connection is aborted.
    [Test]
    public async Task Shutdown_ends_a_push_stream_still_waiting_for_its_hello()
    {
        await using var cluster = await Cluster.StartAsync("gru");
        var gru = cluster["gru"];
        using var channel = PeerChannel.Create(gru.Api.Server.BaseAddress);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
        await Assert.That(await gru.UntilMetricAsync("http_server_active_requests", value => value >= 1, "http_request_method=\"POST\"")).IsTrue();

        gru.Api.Server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(await call.ResponseStream.MoveNext(timeout.Token)).IsFalse();
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
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(database), Refused = true });
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
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database), Refused = true });
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
        gru.LoseOnBucketRead = true;

        using var push = await RawPush.StartAsync(sjc, (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name);
        await push.RefillAsync(cluster.Database, 1);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database), Refused = true });
        await Assert.That(await sjc.ReadAsync(lost)).IsNull();
    }

    // A batch that fails to apply (here the stored state of its document no longer decodes) refuses its database only: the
    // stream goes on with the other databases, and the batch joins once it is sent again after the damage is gone.
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
        await sjc.DamageAsync("items", id);

        using var push = await RawPush.StartAsync(sjc, (await cluster.Members.ListAsync(CancellationToken.None)).Single(listed => listed.Region == "gru").Name);
        var other = await sjc.CreateOtherDatabaseAsync();
        var otherId = EntityIds.New().ToString();
        await push.BatchAsync(cluster.Database, 1, id);
        await push.BatchAsync(other, 1, otherId);
        await Assert.That(await push.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database), Refused = true });
        var acknowledged = await push.AckAsync();
        await Assert.That(acknowledged.Database).IsEqualTo(ChangeGroups.Wire(other));
        await Assert.That(acknowledged is { ChangeId: 1, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(other, otherId)).IsEqualTo("""{"n":1}""");

        await sjc.LoseAsync("items", id);
        await push.BatchAsync(cluster.Database, 1, id);
        await Assert.That((await push.AckAsync()) is { ChangeId: 1, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(id)).IsEqualTo("""{"n":1}""");
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
        var database = ChangeGroups.Wire(cluster.Database);
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
        await Assert.That(acknowledged.Database).IsEqualTo(ChangeGroups.Wire(other));
        await Assert.That(acknowledged is { ChangeId: 1, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(other, first)).IsEqualTo("""{"n":1}""");

        // The repair fails: the refill is refused, and the database's batch still waits.
        held.SetException(new IOException("a test fails the repair"));
        await Assert.That(await push.AckAsync()).IsEqualTo(refused);
        await push.BatchAsync(cluster.Database, 2, first);
        await Assert.That(await push.AckAsync()).IsEqualTo(refused);
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
        var database = ChangeGroups.Wire(cluster.Database);

        await sjc.LoseAsync("items", lost);
        sjc.HeldRepairs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var older = await RawPush.StartAsync(sjc, peer);
        await older.RefillAsync(cluster.Database, 1);
        await sjc.RepairWaits.Task.WaitAsync(Deadline);

        using var newer = await RawPush.StartAsync(sjc, peer);
        await newer.BatchAsync(cluster.Database, 2, later);
        await Assert.That(await newer.AckAsync()).IsEqualTo(new Ack { Database = database, Refused = true });
        await Assert.That(await sjc.ReadAsync(later)).IsNull();

        sjc.HeldRepairs = null;
        await newer.RefillAsync(cluster.Database, 1);
        await Assert.That(await newer.AckAsync()).IsEqualTo(new Ack { Database = database, ChangeId = 1 });
        await Assert.That(await sjc.ReadAsync(lost)).IsEqualTo("""{"n":1}""");
        await newer.BatchAsync(cluster.Database, 2, later);
        await Assert.That((await newer.AckAsync()) is { ChangeId: 2, Refused: false }).IsTrue();
        await Assert.That(await sjc.ReadAsync(later)).IsEqualTo("""{"n":1}""");
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
            await Assert.That(await latest.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database), Refused = true });
            await Assert.That(await sjc.ReadAsync(later)).IsNull();

            sjc.HeldRepairs = null;
            sjc.HeldRepairExit = null;
            await latest.RefillAsync(cluster.Database, 1);
            await Assert.That(await latest.AckAsync()).IsEqualTo(new Ack { Database = ChangeGroups.Wire(cluster.Database), ChangeId = 1 });
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
    public async Task Shutdown_ends_a_replacement_waiting_for_its_predecessors_refill()
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
            sjc.Api.Server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            exit.SetResult();
            await Assert.That(await replacement.WelcomeAsync()).IsFalse();
        }
        finally
        {
            _ = exit.TrySetResult();
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

    /// <summary>One machine per region, all listed in one fake DNS, each holding the same database.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        private readonly Dictionary<string, Machine> _machines = [with(StringComparer.Ordinal)];

        public FakeMemberStore Members { get; } = new();

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

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
        private readonly string _name = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-peer-streams-").FullName;
        private ManagementApi? _api;

        public string Region { get; } = region;

        public FakeChangeGroupSource Source { get; } = new();

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{Region} is stopped");

        public ReplicaId Node => Api.Server.Services.GetRequiredService<LocalReplica>().Id;

        // While set, this machine's repairs wait for it before they join (a faulted one fails them): a test holds a refill open.
        public TaskCompletionSource? HeldRepairs { get; set; }

        // A cancelled repair stays alive until this test releases its cleanup.
        public TaskCompletionSource? HeldRepairExit { get; set; }

        // While set, this machine's sink refuses repairs, as when it stops holding the database.
        public bool RefusesRepairs { get; set; }

        // The source becomes unavailable between a successful digest and its first bucket page.
        public bool LoseOnBucketRead { get; set; }

        public Func<TenantContext, CancellationToken, Task>? BeforeDigest { get; set; }

        public RefillMemory? RefillMemory { get; private set; }

        // Completes once a repair waits for HeldRepairs.
        public TaskCompletionSource RepairWaits { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private IStorageEngine Engine => Api.Server.Services.GetRequiredService<IStorageEngine>();

        public async Task StartAsync()
        {
            var first = !File.Exists(Path.Combine(_directory, "_system.db"));
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = Region;
                settings["node-name"] = _name;
                settings["data-directory"] = _directory;
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(cluster.Members);
                services.AddSingleton<IChangeGroupSink>(provider => new HeldSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), this));
                services.AddSingleton<IReplicaStates>(provider => new LosingStates(provider.GetRequiredService<TerraBase.Engine.Sqlite.TursoChangeGroupSource>(), this));
                var memory = services.Single(service => service.ServiceType == typeof(IMemoryBudget));
                services.AddSingleton<IMemoryBudget>(provider => RefillMemory = new RefillMemory((IMemoryBudget)memory.ImplementationFactory!(provider)));
                if (!cluster.Cdc)
                {
                    services.AddSingleton<IChangeGroupSource>(Source);
                }
            }, time: cluster.Clock);
            if (first)
            {
                await RegisterAsync();
            }
        }

        public async Task StopAsync()
        {
            await Api.DisposeAsync();
            _api = null;
        }

        // The source the peer streams read: the CDC tailer, or the fake.
        public IChangeGroupSource Groups => Api.Server.Services.GetRequiredService<IChangeGroupSource>();

        public async Task<IDatabaseStore> StoreAsync() =>
            await Engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None) ?? throw new InvalidOperationException("the database is missing");

        public async Task<TenantContext> CreateOtherDatabaseAsync()
        {
            var database = cluster.Database with { DatabaseId = EntityIds.New().ToString() };
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

        // Overwrites the document's stored state with bytes that do not decode, as a damaged file would hold.
        public async Task DamageAsync(string collection, string id)
        {
            var engine = Api.Server.Services.GetRequiredService<TerraBase.Engine.Sqlite.SqliteStorageEngine>();
            var store = (TerraBase.Engine.Sqlite.SqliteDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
            _ = await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "UPDATE _tb_state SET state = '!' WHERE collection = $collection AND id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$collection", collection);
                return command.ExecuteNonQuery();
            }, "a test damaging a document's state", CancellationToken.None);
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

            Directory.Delete(_directory, recursive: true);
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
                },
            });
            using var service = Api.Service(Api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>A machine's sink whose repairs wait while the test holds them.</summary>
    private sealed class HeldSink(IChangeGroupSink sink, Machine machine) : IChangeGroupSink
    {
        public ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, CancellationToken cancellationToken) =>
            sink.ApplyAsync(database, origin, groups, cancellationToken);

        public async ValueTask<int?> RepairAsync(TenantContext database, IReadOnlyList<RepairState> states, CancellationToken cancellationToken)
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

            return machine.RefusesRepairs ? null : await sink.RepairAsync(database, states, cancellationToken);
        }
    }

    private sealed class LosingStates(IReplicaStates states, Machine machine) : IReplicaStates
    {
        private bool _lost;

        public async ValueTask<byte[]?> DigestAsync(TenantContext database, CancellationToken cancellationToken)
        {
            if (machine.BeforeDigest is { } before)
            {
                await before(database, cancellationToken);
            }

            return Volatile.Read(ref _lost) ? null : await states.DigestAsync(database, cancellationToken);
        }

        public ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)> ReadBucketsAsync(
            TenantContext database, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken)
        {
            if (machine.LoseOnBucketRead)
            {
                Volatile.Write(ref _lost, true);
                return ValueTask.FromResult<(IReadOnlyList<RepairState>, BucketCursor?)>(([], null));
            }

            return states.ReadBucketsAsync(database, buckets, after, maxStates, maxBytes, cancellationToken);
        }
    }

    private sealed class RefillMemory(IMemoryBudget inner) : IMemoryBudget
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

        public ulong LastStamp { get; private set; }

        private RawPush(Machine machine, ReplicaId? origin)
        {
            _origin = origin ?? Origin;
            _channel = PeerChannel.Create(machine.Api.Server.BaseAddress);
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
            var push = new RawPush(machine, origin);
            await push._call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = push._origin.Value, Region = "gru", Machine = peer } });
            return push;
        }

        public async Task<bool> WelcomeAsync()
        {
            using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
            return await _call.ResponseStream.MoveNext(timeout.Token) && _call.ResponseStream.Current.Welcome is not null;
        }

        public async Task CompleteAsync()
        {
            await _call.RequestStream.CompleteAsync();
            using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
            await Assert.That(await _call.ResponseStream.MoveNext(timeout.Token)).IsFalse();
        }

        public Task RefillAsync(TenantContext database, ulong changeId) =>
            _call.RequestStream.WriteAsync(new PushFrame { Refill = new Refill { Database = ChangeGroups.Wire(database), ChangeId = changeId } });

        // One group putting {"n":1} under `id`.
        public Task BatchAsync(TenantContext database, ulong changeId, string id)
        {
            LastStamp = Stamp();
            return _call.RequestStream.WriteAsync(new PushFrame
            {
                Batch = new Batch
                {
                    Database = ChangeGroups.Wire(database),
                    LastChangeId = changeId,
                    Groups = ChangeGroups.Encode([new OriginGroup((long)changeId, [Put(_origin, LastStamp, id, """{"n":1}""")])]),
                },
            });
        }

        // The next ack, within the sender's progress timeout.
        public async Task<Ack> AckAsync()
        {
            using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
            return await _call.ResponseStream.MoveNext(timeout.Token) ? _call.ResponseStream.Current.Ack : throw new IOException("the stream ended");
        }

        public void Dispose()
        {
            _call.Dispose();
            _channel.Dispose();
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
