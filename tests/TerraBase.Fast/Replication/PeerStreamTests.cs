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
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.Groups.HeadAsync(cluster.Database, CancellationToken.None))).IsTrue();
        await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", joined, Json("""{"n":3}"""), CancellationToken.None));
        await Assert.That(await gru.UntilAsync(joined, """{"n":3}""")).IsTrue();
        await sjc.StopAsync();
        var beforeHeld = await gru.Groups.HeadAsync(cluster.Database, CancellationToken.None);
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", held, Json("""{"n":2}"""), CancellationToken.None));

        await gru.TrimAsync();
        await Assert.That(await gru.Groups.AckedAsync(cluster.Database, sjcNode, CancellationToken.None)).IsLessThanOrEqualTo(beforeHeld);
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
        var streamAck = await gru.Groups.HeadAsync(cluster.Database, CancellationToken.None);
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
            var cut = !await gru.Groups.RetainsAsync(cluster.Database, streamAck, CancellationToken.None);
            var dbAck = await gru.Groups.AckedAsync(cluster.Database, sjcNode, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            string? back = null;
            while (back is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                back = await sjc.ReadAsync(first);
            }

            rounds.Add($"round {round}: stream ack {streamAck}, _tb_peers.acked {dbAck}, retains-false {cut}, lost document refilled {back is not null}");
            streamAck = await gru.Groups.AckedAsync(cluster.Database, sjcNode, CancellationToken.None);
        }

        await Assert.That(rounds.Count(round => round.EndsWith("refilled True", StringComparison.Ordinal))).IsEqualTo(0).Because(string.Join("; ", rounds));
    }

    // Round-2 review, probe A: retention remembers each raise as a range without local groups, and that range must end at
    // the quiet point (the commit before the next local group), never past it. A live stream whose in-memory ack is still
    // the pre-raise ack reads from the end of the remembered range, and would otherwise skip gru's local group.
    [Test]
    public async Task A_read_from_below_a_raise_still_returns_the_local_group_after_it()
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
        var streamAck = await gru.Groups.HeadAsync(cluster.Database, CancellationToken.None);
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, streamAck)).IsTrue();
        gru.CdcSource.Untrack();
        await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", joined, Json("""{"n":3}"""), CancellationToken.None));
        await Assert.That(await gru.UntilAsync(joined, """{"n":3}""")).IsTrue();
        await sjc.StopAsync();
        await Assert.That(await gru.Groups.AckedAsync(cluster.Database, sjcNode, CancellationToken.None)).IsEqualTo(streamAck);
        // AckedAsync tracked the database again.
        gru.CdcSource.Untrack();
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", held, Json("""{"n":2}"""), CancellationToken.None));

        await gru.TrimAsync();
        await Assert.That(await gru.Groups.AckedAsync(cluster.Database, sjcNode, CancellationToken.None)).IsGreaterThan(streamAck);
        await Assert.That(await gru.Groups.RetainsAsync(cluster.Database, streamAck, CancellationToken.None)).IsTrue();
        var groups = await gru.Groups.ReadAsync(cluster.Database, streamAck, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None);
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
        var streamAck = await gru.Groups.HeadAsync(cluster.Database, CancellationToken.None);
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, streamAck)).IsTrue();
        await sjc.WriteAsync(async transaction => await transaction.PutAsync("items", joined, Json("""{"n":3}"""), CancellationToken.None));
        await Assert.That(await gru.UntilAsync(joined, """{"n":3}""")).IsTrue();
        await sjc.StopAsync();
        await gru.WriteAsync(async transaction => await transaction.PutAsync("items", held, Json("""{"n":2}"""), CancellationToken.None));
        await gru.TrimAsync();
        var acked = await gru.Groups.AckedAsync(cluster.Database, sjcNode, CancellationToken.None);

        clock.Offset = TimeSpan.FromHours(25);
        await gru.TrimAsync();
        await Assert.That(await gru.Groups.RetainsAsync(cluster.Database, acked, CancellationToken.None)).IsFalse();
        // A live stream's in-memory ack stays below the raise: the remembered range from it ends where the cut begins.
        await Assert.That(await gru.Groups.RetainsAsync(cluster.Database, streamAck, CancellationToken.None)).IsFalse();
        _ = await gru.Groups.ReadAsync(cluster.Database, acked, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None);
        await Assert.That(await gru.Groups.RetainsAsync(cluster.Database, acked, CancellationToken.None)).IsFalse().Because("a read past a cut proves nothing quiet");
        await Assert.That(await gru.Groups.RetainsAsync(cluster.Database, streamAck, CancellationToken.None)).IsFalse();
        // Between a cut and the pass that prunes it, the raise's range (streamAck, acked] is still remembered: its end is
        // checked too, so RetainsAsync still sees the cut past it.
        gru.CdcSource.Remember(await gru.IncarnationAsync(), streamAck, acked);
        await Assert.That(await gru.Groups.RetainsAsync(cluster.Database, streamAck, CancellationToken.None)).IsFalse().Because("the end of a remembered range is checked");

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
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.Groups.HeadAsync(cluster.Database, CancellationToken.None))).IsTrue();
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
                await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.Groups.HeadAsync(cluster.Database, CancellationToken.None))).IsTrue();
            }

            clock.Offset += TimeSpan.FromSeconds(30);
            await gru.TrimAsync();
            most = Math.Max(most, gru.CdcSource.QuietRanges(incarnation));
        }

        await Assert.That(most).IsLessThanOrEqualTo(8).Because($"at most {most} ranges over 60 passes");
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
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjcNode, await gru.Groups.HeadAsync(cluster.Database, CancellationToken.None))).IsTrue();
        var stale = await gru.Groups.AckedAsync(cluster.Database, sjcNode, CancellationToken.None);
        await Assert.That(stale).IsGreaterThan(0);

        var engine = gru.Api.Server.Services.GetRequiredService<IStorageEngine>();
        var copy = cluster.Database with { DatabaseId = EntityIds.New().ToString() };
        await Assert.That(await engine.CopyDatabaseAsync(cluster.Database, copy, CancellationToken.None)).IsTrue();
        var store = (await engine.OpenDatabaseAsync(copy, CancellationToken.None))!;
        for (var write = 0; write < 500 && await gru.Groups.HeadAsync(copy, CancellationToken.None) < stale; write++)
        {
            var id = EntityIds.New().ToString();
            _ = await store.WriteAsync(async (transaction, token) =>
            {
                _ = await transaction.PutAsync("items", id, Json("""{"n":2}"""), CancellationToken.None);
                return true;
            }, static _ => true, CancellationToken.None);
        }

        await Assert.That(await gru.Groups.HeadAsync(copy, CancellationToken.None)).IsGreaterThanOrEqualTo(stale);
        await Assert.That(await gru.Groups.AckedAsync(copy, sjcNode, CancellationToken.None)).IsEqualTo(0);
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

    private sealed class Machine(Cluster cluster, string region, string[] replicas) : IAsyncDisposable
    {
        private readonly string _name = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-peer-streams-").FullName;
        private ManagementApi? _api;

        public string Region { get; } = region;

        public FakeChangeGroupSource Source { get; } = new();

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{Region} is stopped");

        public ReplicaId Node => Api.Server.Services.GetRequiredService<LocalReplica>().Id;

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

        public TerraBase.Engine.Sqlite.TursoChangeGroupSource CdcSource => Api.Server.Services.GetRequiredService<TerraBase.Engine.Sqlite.TursoChangeGroupSource>();

        // The database file's incarnation, which keys the CDC source's remembered quiet ranges.
        public async Task<Ulid> IncarnationAsync() =>
            ((TerraBase.Engine.Sqlite.SqliteDatabaseStore)(await Api.Server.Services.GetRequiredService<TerraBase.Engine.Sqlite.SqliteStorageEngine>()
                .OpenDatabaseAsync(cluster.Database, CancellationToken.None))!).Incarnation;

        // One retention pass over the database, as the CDC source's 5 s loop runs it.
        public async Task TrimAsync()
        {
            var engine = Api.Server.Services.GetRequiredService<TerraBase.Engine.Sqlite.SqliteStorageEngine>();
            var store = (TerraBase.Engine.Sqlite.SqliteDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
            await Api.Server.Services.GetRequiredService<TerraBase.Engine.Sqlite.TursoChangeGroupSource>().TrimAsync(store, CancellationToken.None);
        }

        // The rows of the database's turso_cdc.
        public async Task<long> CdcRowsAsync()
        {
            var engine = Api.Server.Services.GetRequiredService<TerraBase.Engine.Sqlite.SqliteStorageEngine>();
            var store = (TerraBase.Engine.Sqlite.SqliteDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None))!;
            return await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM turso_cdc;";
                return (long)command.ExecuteScalar()!;
            }, "a test counting CDC rows", CancellationToken.None);
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

        public async Task<string?> ReadAsync(string id)
        {
            if (await Engine.OpenDatabaseAsync(cluster.Database, CancellationToken.None) is not { } store)
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
