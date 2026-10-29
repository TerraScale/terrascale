using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// Damaged replication input (docs/terrabase/multi-region-replication.md sections 7, 9 and 18). The CDC tailer reads rows
/// of <c>turso_cdc</c> that are cut, empty, of an unknown change type or re-encoded with garbage (one fixture per case in
/// <c>Fixtures/CorruptCdc</c>): it refuses the transaction with an <see cref="InvalidDataException"/> or ships it exactly as
/// written, never as something else and never by leaving it out. One database's damaged row does not hold back another
/// database on the same stream. A peer's batch with one bad group applies nothing and is refused for its database alone.
/// A torn WAL tail (what a power loss leaves with <c>synchronous = NORMAL</c>) loses whole transactions after the tear,
/// their CDC rows with them, and nothing before it.
/// </summary>
public sealed class CorruptChangeLogTests
{
    private const string Items = "items";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly string[] Torn = ["torn-1", "torn-2", "torn-3"];

    /// <summary>Every fixture file's name, so a new fixture runs without being listed here.</summary>
    public static IEnumerable<string> Fixtures() =>
        Directory.EnumerateFiles(FixtureDirectory(), "*.json").Select(file => Path.GetFileNameWithoutExtension(file)).Order(StringComparer.Ordinal);

    // One transaction writes document a; its CDC rows are damaged as the fixture says. Refused: every read from before the
    // transaction fails with InvalidDataException (the stream would end and resume from the ack, holding the write).
    // Shipped: once the next transaction (document b) commits, a read returns a's delta byte for byte as it was written
    // before the damage, and b's.
    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task A_damaged_cdc_row_is_refused_or_shipped_exactly_as_written(string fixture)
    {
        var damage = Damage.Load(fixture);
        await using var node = await Node.StartAsync("gru");
        await node.WriteAsync(node.Database, async transaction => _ = await transaction.CreateCollectionAsync(new CollectionInfo(Items, 1), CancellationToken.None));
        var before = await node.HeadAsync(node.Database);
        await node.WriteAsync(node.Database, async transaction => _ = await transaction.PutAsync(Items, "a", Json("""{"n":1,"s":"written"}"""), CancellationToken.None));
        var written = await node.ReadGroupsAsync(node.Database, before);
        await Assert.That(written.Count).IsEqualTo(1);
        await Assert.That(written[0].Deltas.Select(delta => delta.Id)).IsEquivalentTo(["a"]);

        await node.DamageAsync(node.Database, before, "a", damage);
        if (damage.Expect == "refused")
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await node.ReadGroupsAsync(node.Database, before));
            Console.WriteLine($"{fixture}: refused: {error!.Message}");
            await Assert.That(await node.Api.Server.Services.GetRequiredService<IRebuildLedger>()
                .GetAsync(node.Database, CancellationToken.None)).IsNotNull().Because("the source refusal is durable");
            return;
        }

        await node.WriteAsync(node.Database, async transaction => _ = await transaction.PutAsync(Items, "b", Json("""{"n":2}"""), CancellationToken.None));
        var deltas = (await node.ReadGroupsAsync(node.Database, before)).SelectMany(group => group.Deltas).ToList();
        await Assert.That(deltas.Select(delta => delta.Id)).IsEquivalentTo(["a", "b"]);
        await Assert.That(Encoded(deltas.Single(delta => delta.Id == "a"))).IsEqualTo(Encoded(written[0].Deltas[0]));
    }

    // Two databases share gru's stream to sjc. The one gru lists first gets a write whose state row is cut in half while sjc
    // is unlisted ("damaged"; "intact" leaves it alone: the control); the other gets a clean write. Once sjc is listed again,
    // the clean write reaches it; the damaged one is never applied there in any form, and gru records its durable fault
    // and reports the held database in send_lag.
    [Test]
    [Arguments("damaged")]
    [Arguments("intact")]
    public async Task A_damaged_cdc_row_in_one_database_does_not_hold_back_another_database_on_the_same_stream(string firstWrite)
    {
        await using var cluster = await Pair.StartAsync();
        var (gru, sjc) = (cluster.Gru, cluster.Sjc);
        foreach (var database in cluster.Databases)
        {
            await gru.WriteAsync(database, async transaction =>
            {
                _ = await transaction.CreateCollectionAsync(new CollectionInfo(Items, 1), CancellationToken.None);
                _ = await transaction.PutAsync(Items, "warm", Json("""{"n":0}"""), CancellationToken.None);
            });
        }

        // Control: both databases replicate before any damage.
        foreach (var database in cluster.Databases)
        {
            await Assert.That(await sjc.UntilAsync(database, "warm", """{"n":0}""")).IsTrue();
        }

        var first = await gru.Groups.ListDatabasesAsync(CancellationToken.None).FirstAsync(database => cluster.Databases.Contains(database));
        var other = cluster.Databases.Single(database => database != first);
        await gru.UntilAcknowledgedAsync(first, acknowledged => acknowledged > 0);
        await cluster.UnlistAsync(sjc);
        var before = await gru.HeadAsync(first);
        await gru.WriteAsync(first, async transaction => _ = await transaction.PutAsync(Items, "damaged", Json("""{"n":1}"""), CancellationToken.None));
        if (firstWrite == "damaged")
        {
            await gru.DamageAsync(first, before, "damaged", Damage.Load("truncated-state-image"));
        }

        await gru.WriteAsync(other, async transaction => _ = await transaction.PutAsync(Items, "clean", Json("""{"n":2}"""), CancellationToken.None));
        cluster.List(sjc);

        await Assert.That(await sjc.UntilAsync(other, "clean", """{"n":2}""")).IsTrue().Because("a damaged row of another database must not hold back this one");
        if (firstWrite == "damaged")
        {
            await Assert.That(await sjc.ReadAsync(first, "damaged")).IsNull();
            // Maintenance or shipping may detect the damage first; its durable fault and growing send lag show the hold.
            await gru.UntilFaultRecordedAsync(first);
            await Assert.That(await gru.UntilMetricAsync("terrabase_replication_send_lag_seconds", value => value > 0, "sjc")).IsTrue();
            await Assert.That(await gru.Api.Server.Services.GetRequiredService<IRebuildLedger>()
                .GetAsync(first, CancellationToken.None)).IsNotNull();
            var peer = sjc.Api.Server.Services.GetRequiredService<LocalReplica>().Id;
            await Assert.That(await gru.Groups.AckedAsync(first, peer, CancellationToken.None)).IsNull();
            await Assert.That(await gru.Groups.ReadAsync(first, await gru.IncarnationAsync(first), before,
                ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)).IsNull();
            await Assert.That(await gru.Groups.AckAsync(first, await gru.IncarnationAsync(first), peer, before, default, CancellationToken.None)).IsFalse();
            // Replace the real inbound stream while discovery retains the sender's Link and its cached cursors.
            using (var channel = PeerChannel.Create(sjc.Api.Server.BaseAddress, PeerTls.Credential))
            {
                var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
                using var interrupted = client.Push(deadline: DateTime.UtcNow.Add(Deadline));
                await interrupted.RequestStream.WriteAsync(new PushFrame
                {
                    Hello = new Hello { Node = gru.Api.Server.Services.GetRequiredService<LocalReplica>().Id.Value, Region = "gru", Machine = gru.Name },
                });
                using var deadline = new CancellationTokenSource(Deadline);
                await Assert.That(await interrupted.ResponseStream.MoveNext(deadline.Token)).IsTrue();
                await Assert.That(interrupted.ResponseStream.Current.Welcome).IsNotNull();
            }
            await gru.UntilAcknowledgedAsync(first, acknowledged => acknowledged == 0);
            var beforeHealthy = await gru.HeadAsync(other);
            await gru.WriteAsync(other, async transaction => _ = await transaction.PutAsync(Items, "reconnected", Json("""{"n":3}"""), CancellationToken.None));
            var healthyGroup = (await gru.ReadGroupsAsync(other, beforeHealthy))
                .Single(group => group.Deltas.Any(delta => delta.Id == "reconnected"));
            await Assert.That(await sjc.UntilAsync(other, "reconnected", """{"n":3}""")).IsTrue()
                .Because("the refused first cursor cannot prevent a later healthy database from shipping on a new connection");
            // Frontiers and ACK bookkeeping advance the file head without a shippable group to acknowledge.
            await gru.UntilAcknowledgedAsync(other, acknowledged => acknowledged >= healthyGroup.ChangeId);
            await gru.UntilAcknowledgedAsync(first, acknowledged => acknowledged == 0);
            await Assert.That(await sjc.ReadAsync(first, "damaged")).IsNull();
        }
        else
        {
            await Assert.That(await sjc.UntilAsync(first, "damaged", """{"n":1}""")).IsTrue();
            await Assert.That(await gru.UntilMetricAsync("terrabase_replication_unreadable_reads_total", _ => false, absent: true)).IsTrue();
        }
    }

    // The lag gauge decodes stored CDC after ACK bookkeeping. Its swallowed decoder error still faults the copy.
    [Test]
    public async Task Ack_tail_measurement_records_damage_and_refuses_subsequent_work()
    {
        var time = new FakeTimeProvider();
        await using var node = await Node.StartAsync("gru", time);
        await node.WriteAsync(node.Database, async transaction => _ = await transaction.CreateCollectionAsync(new CollectionInfo(Items, 1), CancellationToken.None));
        var before = await node.HeadAsync(node.Database);
        await node.WriteAsync(node.Database, async transaction => _ = await transaction.PutAsync(Items, "a", Json("""{"n":1}"""), CancellationToken.None));
        await node.DamageAsync(node.Database, before, "a", Damage.Load("unknown-change-type-on-state-row"));
        // Expire the one-second gauge cache without firing the five-second maintenance timer.
        time.Advance(TimeSpan.FromSeconds(2));
        var peer = ReplicaId.Of(99, 1, 0);
        var incarnation = await node.IncarnationAsync(node.Database);
        await Assert.That(await node.Groups.AckAsync(node.Database, incarnation, peer, before, default, CancellationToken.None)).IsFalse();
        await Assert.That(await node.Api.Server.Services.GetRequiredService<IRebuildLedger>()
            .GetAsync(node.Database, CancellationToken.None)).IsNotNull();
        await Assert.That(await node.Groups.AckedAsync(node.Database, peer, CancellationToken.None)).IsNull();
        await Assert.That(await node.Groups.AckAsync(node.Database, incarnation, peer, before, default, CancellationToken.None)).IsFalse();
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            await node.WriteAsync(node.Database, async transaction => _ = await transaction.PutAsync(Items, "later", Json("""{"n":2}"""), CancellationToken.None)));

    }

    // Correcting one capture row cannot retire the durable copy fault. PR1 keeps recovery refused until certified refill.
    [Test]
    public async Task Repairing_a_damaged_capture_row_does_not_restore_trust_in_the_faulted_copy()
    {
        await using var cluster = await Pair.StartAsync();
        var (gru, sjc) = (cluster.Gru, cluster.Sjc);
        var first = cluster.Databases[0];
        await gru.WriteAsync(first, async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo(Items, 1), CancellationToken.None);
            _ = await transaction.PutAsync(Items, "warm", Json("""{"n":0}"""), CancellationToken.None);
        });
        await Assert.That(await sjc.UntilAsync(first, "warm", """{"n":0}""")).IsTrue();

        await cluster.UnlistAsync(sjc);
        var before = await gru.HeadAsync(first);
        // An unrelated captured transaction can precede the intended write after the sampled head.
        await gru.WriteAsync(first, async transaction => _ = await transaction.CreateCollectionAsync(new CollectionInfo("unrelated", 1), CancellationToken.None));
        await gru.WriteAsync(first, async transaction => _ = await transaction.PutAsync(Items, "held", Json("""{"n":1}"""), CancellationToken.None));
        await gru.DamageAsync(first, before, "held", Damage.Load("unknown-change-type-on-state-row"));
        // Read past the healthy unrelated group: background probes stop at the first local group.
        try
        {
            _ = await gru.Groups.ReadAsync(first, await gru.IncarnationAsync(first), before,
                ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            // A racing reader may already have recorded refusal; the durable checks below prove either path.
        }
        // Establish the fault while unlisted, so the new sender link has never learned this copy's cursor.
        await gru.UntilFaultRecordedAsync(first);
        cluster.List(sjc);
        await Assert.That(await gru.UntilMetricAsync("terrabase_replication_send_lag_seconds", value => value > 0, "sjc")).IsTrue();
        await gru.UntilAcknowledgedAsync(first, acknowledged => acknowledged == 0);
        var peer = sjc.Api.Server.Services.GetRequiredService<LocalReplica>().Id;
        await Assert.That(await gru.Groups.AckedAsync(first, peer, CancellationToken.None)).IsNull();
        await Assert.That(await gru.Groups.AckAsync(first, await gru.IncarnationAsync(first), peer, before, default, CancellationToken.None)).IsFalse();
        await Assert.That(await sjc.ReadAsync(first, "held")).IsNull();

        await gru.RepairAsync(first, "UPDATE turso_cdc SET change_type = 1 WHERE change_type = 7;");
        // Decoder control: the corrected row is readable, but production sourcing still refuses its incarnation.
        var decoded = await gru.Api.Server.Services.GetRequiredService<TursoChangeGroupSource>()
            .ReadAsync(first, await gru.IncarnationAsync(first), before, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)
            ?? throw new InvalidOperationException("the raw decoder refused its own incarnation");
        await Assert.That(decoded.Groups.SelectMany(group => group.Deltas).Select(delta => delta.Id)).Contains("held");
        await Assert.That(await gru.Groups.ReadAsync(first, await gru.IncarnationAsync(first), before,
                ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)).IsNull();
        await Assert.That(await gru.Api.Server.Services.GetRequiredService<IReplicaStates>().DigestAsync(first, await gru.IncarnationAsync(first), CancellationToken.None)).IsNull();
        await Assert.That(await gru.Api.Server.Services.GetRequiredService<IRebuildLedger>().GetAsync(first, CancellationToken.None)).IsNotNull();
        await Assert.That(await sjc.ReadAsync(first, "held")).IsNull();
    }

    // A batch whose second group holds bytes that are no DocumentState is refused for its database: its first, valid group
    // is not applied either, and the stream goes on. On the same stream, a batch of the other database applies, then the
    // valid group alone; the receiver counts the refused batch.
    [Test]
    public async Task A_batch_with_one_undecodable_group_is_refused_for_its_database_alone_and_applies_none_of_its_groups()
    {
        await using var node = await Node.StartAsync("gru");
        foreach (var database in new[] { node.Database, node.Second })
        {
            await node.WriteAsync(database, async transaction => _ = await transaction.CreateCollectionAsync(new CollectionInfo(Items, 1), CancellationToken.None));
        }

        var origin = ReplicaId.Of(1, 7, 0);
        var at = new EntryVersion(new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16), origin);
        using var body = JsonDocument.Parse("""{"n":1}""");
        var good = new Delta
        {
            Collection = Items,
            Id = "x",
            Stamp = at.Stamp.Value,
            State = ByteString.CopyFrom(DocumentStateEncoding.Encode(DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp))),
        };
        var garbage = new Delta { Collection = Items, Id = "y", Stamp = at.Stamp.Value + 1, State = ByteString.CopyFrom([0xFF, 0x00, 0x13, 0x37, 0x42]) };

        using var channel = PeerChannel.Create(node.Api.Server.BaseAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        using var call = await HelloAsync(client, origin);
        await call.RequestStream.WriteAsync(new PushFrame { Batch = Batch(node.Database, (1, good), (2, garbage)) });
        var refused = await AckAsync(call);
        await Assert.That(refused.Refused).IsTrue();
        await Assert.That(refused.Database).IsEqualTo(ChangeGroups.Wire(node.Database, Sender));
        await Assert.That(await node.ReadAsync(node.Database, "x")).IsNull().Because("no group of a refused batch is applied");

        await call.RequestStream.WriteAsync(new PushFrame { Batch = Batch(node.Second, (1, good)) });
        var other = await AckAsync(call);
        await Assert.That((other.Refused, other.ChangeId, other.Database)).IsEqualTo((false, 1UL, ChangeGroups.Wire(node.Second, Sender)));
        await call.RequestStream.WriteAsync(new PushFrame { Batch = Batch(node.Database, (1, good)) });
        var again = await AckAsync(call);
        await Assert.That((again.Refused, again.ChangeId, again.Database)).IsEqualTo((false, 1UL, ChangeGroups.Wire(node.Database, Sender)));

        await Assert.That(await node.ReadAsync(node.Second, "x")).IsEqualTo("""{"n":1}""");
        await Assert.That(await node.ReadAsync(node.Database, "x")).IsEqualTo("""{"n":1}""");
        await Assert.That(await node.UntilMetricAsync("terrabase_replication_undecodable_batches_total", value => value == 1, "sjc")).IsTrue();
    }

    // Transaction 1 (document kept) commits; the WAL's size is taken; transaction 2 (documents torn-1..3) commits. A copy of
    // the data directory has its database's WAL cut 100 bytes into the first frame after that size (Variant "cut") or that
    // frame and everything after it overwritten with garbage ("scrambled"). A node started on the copy serves kept exactly,
    // none of transaction 2. The decoder retains only transaction 1. The copy's log is all that remains of it, so it serves on
    // from there without a rebuild: new writes and sourcing are accepted.
    [Test]
    [Arguments("cut")]
    [Arguments("scrambled")]
    public async Task A_torn_wal_tail_loses_whole_transactions_after_the_tear_and_their_cdc_rows_with_them(string tear)
    {
        await using var node = await Node.StartAsync("gru");
        await node.WriteAsync(node.Database, async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo(Items, 1), CancellationToken.None);
            _ = await transaction.PutAsync(Items, "kept", Json("""{"n":1}"""), CancellationToken.None);
        });
        var wal = await node.WalAsync(node.Database);
        var kept = (await node.ReadGroupsAsync(node.Database, 0)).SelectMany(group => group.Deltas).Single(delta => delta.Id == "kept");
        var intact = await node.HoldingWriterAsync(node.Database, () => new FileInfo(wal).Length);
        await node.WriteAsync(node.Database, async transaction =>
        {
            foreach (var id in Torn)
            {
                _ = await transaction.PutAsync(Items, id, Json("""{"n":2}"""), CancellationToken.None);
            }
        });
        await Assert.That(await node.ReadAsync(node.Database, "torn-1")).IsEqualTo("""{"n":2}""");

        var copy = Directory.CreateTempSubdirectory("terrabase-torn-wal-").FullName;
        try
        {
            await node.HoldingWriterAsync(node.Database, () => CopyDirectory(node.Api.Server.DataDirectory, copy));
            var torn = Path.Combine(copy, Path.GetRelativePath(node.Api.Server.DataDirectory, wal));
            var length = new FileInfo(torn).Length;
            await Assert.That(length).IsGreaterThan(intact).Because("transaction 2's frames follow the size taken before it");
            await using (var file = new FileStream(torn, FileMode.Open, FileAccess.Write))
            {
                if (tear == "cut")
                {
                    file.SetLength(intact + 100);
                }
                else
                {
                    file.Position = intact + 24;
                    var garbage = new byte[length - file.Position];
                    new Random(7).NextBytes(garbage);
                    await file.WriteAsync(garbage);
                }
            }

            await using var restarted = await Node.StartAsync("gru", copy, node);
            await Assert.That(await restarted.ReadAsync(node.Database, "kept")).IsEqualTo("""{"n":1}""");
            foreach (var id in Torn)
            {
                await Assert.That(await restarted.ReadAsync(node.Database, id)).IsNull().Because($"{id} was in the torn transaction");
            }

            // The copied directory has a new owner. Decode the recovered rows using their original replica;
            // the new owner's source must never present donor writes as its own.
            var donor = node.Api.Server.Services.GetRequiredService<TursoStorageEngine>().ReplicaId;
            await Assert.That(restarted.Api.Server.Services.GetRequiredService<TursoStorageEngine>().ReplicaId.Member).IsNotEqualTo(donor.Member);
            List<ChangeDelta> deltas;
            await using (var recovered = (TursoStorageTransaction)await (await restarted.StoreAsync(node.Database))
                .BeginTransactionAsync(false, CancellationToken.None))
            {
                var (groups, _, _) = await recovered.ReadChangeGroupsAsync(0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, donor, CancellationToken.None);
                deltas = [.. groups.SelectMany(group => group.Deltas)];
            }
            await Assert.That(deltas.Where(delta => delta.Collection == Items).Select(delta => delta.Id)).IsEquivalentTo(["kept"]);
            await Assert.That(Encoded(deltas.Single(delta => delta.Id == "kept"))).IsEqualTo(Encoded(kept));

            await restarted.WriteAsync(node.Database,
                async transaction => _ = await transaction.PutAsync(Items, "after", Json("""{"n":3}"""), CancellationToken.None));
            await Assert.That(await restarted.ReadAsync(node.Database, "after")).IsEqualTo("""{"n":3}""");
            await Assert.That(await restarted.Groups.ReadAsync(node.Database, await restarted.IncarnationAsync(node.Database), 0,
                ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)).IsNotNull();
            await Assert.That(await restarted.Api.Server.Services.GetRequiredService<IRebuildLedger>()
                .GetAsync(node.Database, CancellationToken.None)).IsNull();
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    private static string FixtureDirectory([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(source))!, "Fixtures", "CorruptCdc");

    private static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);

    private static string Encoded(ChangeDelta delta) =>
        $"{delta.Collection}/{delta.Id}@{delta.Stamp.Value}:{Convert.ToHexString(DocumentStateEncoding.Encode(delta.State))}";

    private static async Task<AsyncDuplexStreamingCall<PushFrame, AckFrame>> HelloAsync(Features.Replication.V1.Replication.ReplicationClient client, ReplicaId origin)
    {
        var call = client.Push();
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
        return call;
    }

    private static async Task<Ack> AckAsync(AsyncDuplexStreamingCall<PushFrame, AckFrame> call)
    {
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        return call.ResponseStream.Current.Ack;
    }

    // The made-up origin's copy that every raw batch names, and its acks echo.
    private static readonly Ulid Sender = Ulid.NewUlid();

    private static Batch Batch(TenantContext database, params (ulong ChangeId, Delta Delta)[] groups)
    {
        var list = new GroupList();
        foreach (var (changeId, delta) in groups)
        {
            var group = new Group { ChangeId = changeId };
            group.Deltas.Add(delta);
            list.Groups.Add(group);
        }

        return new Batch
        {
            Database = ChangeGroups.Wire(database, Sender),
            LastChangeId = groups[^1].ChangeId,
            Groups = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(list.ToByteArray())),
        };
    }

    // Copies every file but the lock file, which the running node holds (.NET takes the same lock to read it); the copy's
    // lock file gets the layout record the engine writes there.
    private static bool CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(file => Path.GetFileName(file) != TursoStorageEngine.LockFileName))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        File.WriteAllBytes(Path.Combine(to, TursoStorageEngine.LockFileName), Encoding.ASCII.GetBytes(StorageFormat.Id));
        return true;
    }

    /// <summary>One fixture: which CDC row of the transaction to damage, how, and what the tailer must do.</summary>
    private sealed record Damage(string Row, JsonObject Mutation, string Expect)
    {
        public static Damage Load(string name)
        {
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory(), name + ".json")))!.AsObject();
            var damage = new Damage(json["row"]!.GetValue<string>(), json["mutation"]!.AsObject(), json["expect"]!.GetValue<string>());
            return damage is { Row: "state" or "document" or "commit", Expect: "refused" or "shipped" }
                ? damage
                : throw new InvalidDataException($"fixture {name}: row is state, document or commit; expect is refused or shipped");
        }
    }

    /// <summary>One machine of a test: the real server with its CDC tailer, holding one or two databases.</summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly bool _ownsDirectory;

        private Node(ManagementApi api, string directory, bool ownsDirectory, TenantContext database, TenantContext second)
        {
            (Api, _directory, _ownsDirectory, Database, Second) = (api, directory, ownsDirectory, database, second);
        }

        public ManagementApi Api { get; }

        public TenantContext Database { get; }

        public TenantContext Second { get; }

        public IChangeGroupSource Groups => Api.Server.Services.GetRequiredService<IChangeGroupSource>();

        public static Task<Node> StartAsync(string region, TimeProvider? time = null) => StartAsync(region, null, null, null, [], time: time);

        // A node on a copy of `of`'s data directory, under its name and databases; the copy stays the caller's.
        public static Task<Node> StartAsync(string region, string directory, Node of) => StartAsync(region, directory, of, null, []);

        public static async Task<Node> StartAsync(string region, string? directory, Node? of, FakeMemberStore? members, string[] replicas, string? name = null,
            TenantContext? database = null, TenantContext? second = null, string? user = null, TimeProvider? time = null)
        {
            var owns = directory is null;
            directory ??= Directory.CreateTempSubdirectory("terrabase-corrupt-cdc-").FullName;
            name = of?.Name ?? name ?? Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
            database = of?.Database ?? database ?? new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
            second = of?.Second ?? second ?? database with { DatabaseId = EntityIds.New().ToString() };
            var api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = name;
                settings["data-directory"] = directory;
            }, services =>
            {
                // Preserve each suspect fixture in place while testing its decoder and durable source refusal.
                services.AddSingleton<IReplicaRebuild, BusyRebuild>();
                if (members is not null)
                {
                    services.AddSingleton<IMemberDirectory>(members);
                }
            }, userId: of?.Api.User ?? user, time: time);
            var node = new Node(api, directory, owns, database, second) { Name = name };
            if (of is null)
            {
                await node.RegisterAsync(replicas);
                // A peer row at 0 holds every CDC row of the database from now on, as a peer's first contact does: retention
                // must not take the rows a test reads.
                _ = await node.Groups.AckedAsync(database, ReplicaId.Of(99, 1, 0), CancellationToken.None);
                _ = await node.Groups.AckedAsync(second, ReplicaId.Of(99, 1, 0), CancellationToken.None);
            }

            return node;
        }

        public string Name { get; private init; } = "";

        public async Task<TursoDatabaseStore> StoreAsync(TenantContext database) =>
            (TursoDatabaseStore)(await Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the database is missing"));

        // One queued write of the database's group commit, as the document API makes one.
        public async Task WriteAsync(TenantContext database, Func<IStorageTransaction, Task> work)
        {
            var store = await StoreAsync(database);
            _ = await store.WriteAsync(async (transaction, _) =>
            {
                await work(transaction);
                return true;
            }, static _ => true, CancellationToken.None);
        }

        // The copy in place, which the source's reads and acks name; its own incarnation is never refused.
        public async Task<Ulid> IncarnationAsync(TenantContext database) => (await StoreAsync(database)).Incarnation;

        public async Task<IReadOnlyList<OriginGroup>> ReadGroupsAsync(TenantContext database, long after) =>
            (await Groups.ReadAsync(database, await IncarnationAsync(database), after, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)
                ?? throw new InvalidOperationException("the copy refused its own incarnation")).Groups;

        public async Task<long> HeadAsync(TenantContext database) =>
            await Groups.HeadAsync(database, await IncarnationAsync(database), CancellationToken.None) ?? throw new InvalidOperationException("the copy refused its own incarnation");

        public async Task<long> AckedAsync(TenantContext database, ReplicaId peer) =>
            (await Groups.AckedAsync(database, peer, CancellationToken.None))?.Acked ?? throw new InvalidOperationException("the database is missing");

        public async Task<string> WalAsync(TenantContext database) => (await StoreAsync(database)).FilePath + "-wal";

        // Runs `call` while no write of the database can run.
        public async Task<T> HoldingWriterAsync<T>(TenantContext database, Func<T> call) =>
            await (await StoreAsync(database)).WithWriterAsync(_ => call(), "a test reading the database's files", CancellationToken.None);

        // Damages the intended document's captured transaction after `after`, skipping unrelated commits:
        // its collection row, its _tb_state row, or its commit row. Changes to turso_cdc are not captured.
        public async Task DamageAsync(TenantContext database, long after, string documentId, Damage damage)
        {
            var commit = (await ReadGroupsAsync(database, after)).Single(group =>
                group.Deltas.Any(delta => delta.Collection == Items && delta.Id == documentId)).ChangeId;
            var store = await StoreAsync(database);
            var damaged = await store.WithWriterAsync(session =>
            {
                List<(long Id, long Type, string Table, byte[]? After)> rows = [];
                using (var read = session.Connection.CreateCommand())
                {
                    read.CommandText = """
                        SELECT change_id, change_type, table_name, after FROM turso_cdc
                        WHERE change_id > $after AND change_id <= $commit AND change_id > coalesce((
                            SELECT max(change_id) FROM turso_cdc WHERE change_type = 2 AND change_id < $commit), 0)
                        ORDER BY change_id;
                        """;
                    read.Parameters.AddWithValue("$after", after);
                    read.Parameters.AddWithValue("$commit", commit);
                    using var reader = read.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? "" : reader.GetString(2), reader.IsDBNull(3) ? null : TursoSession.Bytes(reader, 3)));
                    }
                }

                var (id, _, _, image) = damage.Row switch
                {
                    "commit" => rows.Single(row => row.Id == commit && row.Type == 2),
                    "document" => rows.First(row => row.Id < commit && row.Type is 0 or 1 && row.Table == Items
                        && SqliteRecord.Decode(row.After) is [string capturedId, ..] && capturedId == documentId),
                    _ => rows.First(row => row.Id < commit && row.Type is 0 or 1 && row.Table == "_tb_state"
                        && SqliteRecord.Decode(row.After) is [string collection, string capturedId, ..]
                        && collection == Items && capturedId == documentId),
                };
                using var write = session.Connection.CreateCommand();
                write.Parameters.AddWithValue("$id", id);
                switch (damage.Mutation["kind"]!.GetValue<string>())
                {
                    case "delete":
                        write.CommandText = "DELETE FROM turso_cdc WHERE change_id = $id;";
                        break;
                    case "change_type":
                        write.CommandText = "UPDATE turso_cdc SET change_type = $type WHERE change_id = $id;";
                        write.Parameters.AddWithValue("$type", damage.Mutation["value"]!.GetValue<long>());
                        break;
                    case "null":
                        write.CommandText = "UPDATE turso_cdc SET after = NULL WHERE change_id = $id;";
                        break;
                    case "truncate":
                        write.CommandText = "UPDATE turso_cdc SET after = $after WHERE change_id = $id;";
                        write.Parameters.AddWithValue("$after", image![..(image!.Length / 2)]);
                        break;
                    case "append":
                        write.CommandText = "UPDATE turso_cdc SET after = $after WHERE change_id = $id;";
                        write.Parameters.AddWithValue("$after", (byte[])[.. image!, .. Convert.FromHexString(damage.Mutation["hex"]!.GetValue<string>())]);
                        break;
                    case "column":
                        var values = SqliteRecord.Decode(image);
                        values[damage.Mutation["column"]!.GetValue<int>()] = damage.Mutation["text"]!.GetValue<string>();
                        write.CommandText = "UPDATE turso_cdc SET after = $after WHERE change_id = $id;";
                        write.Parameters.AddWithValue("$after", Record(values));
                        break;
                    default:
                        throw new InvalidDataException("a fixture mutation is delete, change_type, null, truncate, append or column");
                }

                return write.ExecuteNonQuery();
            }, "a test damaging a CDC row", CancellationToken.None);
            await Assert.That(damaged).IsEqualTo(1);
        }

        // Runs one repair statement on the change log through the database's writer; it must change exactly one row.
        public async Task RepairAsync(TenantContext database, string sql)
        {
            var store = await StoreAsync(database);
            var repaired = await store.WithWriterAsync(session =>
            {
                using var write = session.Connection.CreateCommand();
                write.CommandText = sql;
                return write.ExecuteNonQuery();
            }, "a test repairing a CDC row", CancellationToken.None);
            await Assert.That(repaired).IsEqualTo(1);
        }

        public async Task UntilFaultRecordedAsync(TenantContext database)
        {
            var incarnation = await IncarnationAsync(database);
            var services = Api.Server.Services;
            await TerraBaseTestServer.EventuallyAsync(async token =>
                (await services.GetRequiredService<IRebuildLedger>().GetAsync(database, token))?.OldIncarnation == incarnation
                    && await services.GetRequiredService<IReplicaCopyFaults>().RefusedAsync(database, incarnation, token), Deadline);
        }

        public Task UntilAcknowledgedAsync(TenantContext database, Func<long, bool> holds) =>
            TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var response = await Api.Server.Client.GetAsync(new Uri("/replication", UriKind.Relative), token);
                var status = JsonElement.Parse(await response.Content.ReadAsStringAsync(token));
                return status.GetProperty("peers").EnumerateArray().SelectMany(peer => peer.GetProperty("databases").EnumerateArray())
                    .Any(copy => copy.GetProperty("databaseId").GetString() == database.DatabaseId
                        && holds(copy.GetProperty("acknowledged").GetInt64()));
            }, Deadline);

        // Whether the document's materialized JSON becomes `expected` within the deadline.
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

        // Whether a sample of the metric (with the label, when given) satisfies `holds` within the deadline, or with `absent`,
        // whether the metric has no sample.
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

        public async Task<string?> ReadAsync(TenantContext database, string id)
        {
            if (await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None) is not { } store)
            {
                return null;
            }

            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            try
            {
                return await transaction.GetAsync(Items, id, CancellationToken.None) is { Deleted: false } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync();
            if (_ownsDirectory)
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        // The organization, project and both databases, placed in `replicas` besides this node's region.
        private async Task RegisterAsync(string[] replicas)
        {
            await CatalogFixture.RegisterAsync(Api, Api.User, Database, "main", replicas);
            await CatalogFixture.RegisterAsync(Api, Api.User, Second, "second", replicas);
        }
    }

    private sealed class BusyRebuild : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Busy());
    }

    /// <summary>gru and sjc in one fake DNS, both holding the same two databases.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        private readonly FakeMemberStore _members = new();
        private readonly List<ClusterMember> _unlisted = [];

        public Node Gru { get; private set; } = null!;

        public Node Sjc { get; private set; } = null!;

        public TenantContext[] Databases => [Gru.Database, Gru.Second];

        public static async Task<Pair> StartAsync()
        {
            var pair = new Pair();
            try
            {
                var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
                var second = database with { DatabaseId = EntityIds.New().ToString() };
                pair.Gru = await Node.StartAsync("gru", null, null, pair._members, ["sjc"], database: database, second: second);
                pair.Sjc = await Node.StartAsync("sjc", null, null, pair._members, ["gru"], database: database, second: second, user: pair.Gru.Api.User);
                return pair;
            }
            catch
            {
                await pair.DisposeAsync();
                throw;
            }
        }

        // Drops the node from the DNS and waits until gru reports no outbound stream to it.
        public async Task UnlistAsync(Node node)
        {
            var listed = (await _members.ListAsync(CancellationToken.None)).Single(member => member.Name == node.Name);
            _unlisted.Add(listed);
            _members.Leave(listed.Name);
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                using var response = await Gru.Api.Server.Client.GetAsync(new Uri("/replication", UriKind.Relative));
                if (JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("peers").GetArrayLength() == 0)
                {
                    return;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("gru kept its stream to an unlisted machine");
        }

        public void List(Node node)
        {
            var member = _unlisted.Single(member => member.Name == node.Name);
            _unlisted.Remove(member);
            _members.Join(member);
        }

        public async ValueTask DisposeAsync()
        {
            if (Sjc is not null)
            {
                await Sjc.DisposeAsync();
            }

            if (Gru is not null)
            {
                await Gru.DisposeAsync();
            }
        }
    }

    // SQLite's record format (https://sqlite.org/fileformat.html#record_format): a varint header size, one serial type per
    // value, then the values. Text is serial type 13 + 2n, a 64-bit integer 6, NULL 0.
    private static byte[] Record(object?[] values)
    {
        List<byte> types = [];
        List<byte> body = [];
        foreach (var value in values)
        {
            switch (value)
            {
                case null:
                    Varint(types, 0);
                    break;
                case long number:
                    Varint(types, 6);
                    var big = new byte[8];
                    BinaryPrimitives.WriteInt64BigEndian(big, number);
                    body.AddRange(big);
                    break;
                case string text:
                    var bytes = Encoding.UTF8.GetBytes(text);
                    Varint(types, 13 + (2L * bytes.Length));
                    body.AddRange(bytes);
                    break;
                case byte[] blob:
                    Varint(types, 12 + (2L * blob.Length));
                    body.AddRange(blob);
                    break;
                default:
                    throw new InvalidDataException($"a record value of type {value.GetType().Name}");
            }
        }

        // The header size counts itself; one byte while the header is under 128 bytes.
        List<byte> record = [];
        Varint(record, types.Count + 1 < 128 ? types.Count + 1 : types.Count + 2);
        return [.. record, .. types, .. body];
    }

    // Big-endian base-128, high bit set on every byte but the last (values below 2^56).
    private static void Varint(List<byte> into, long value)
    {
        Span<byte> groups = stackalloc byte[9];
        var count = 0;
        do
        {
            groups[count++] = (byte)(value & 0x7F);
            value >>= 7;
        }
        while (value != 0);

        for (var index = count - 1; index >= 0; index--)
        {
            into.Add((byte)(groups[index] | (index > 0 ? 0x80 : 0)));
        }
    }
}
