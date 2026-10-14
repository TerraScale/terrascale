using System.Text.Json;
using Grpc.Core;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Testing;
using ReplicaCluster = TerraBase.Fast.Replication.PeerStreamTests.Cluster;

namespace TerraBase.Fast.Replication;

public sealed class PeerStreamBlockedFrontierTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Blocked_frontiers_answer_without_certifying_progress_or_clearing_a_failed_or_pending_refill(bool refillInFlight)
    {
        // Start the source first so the receiver's cached membership already lists it.
        await using var cluster = await ReplicaCluster.StartAsync(refillInFlight ? ["sjc", "gru"] : ["gru"]);
        var receiver = cluster["gru"];
        var other = await receiver.CreateOtherDatabaseAsync();
        var origin = ReplicaId.Of(1, 7, 0);
        var incarnation = refillInFlight ? (await cluster["sjc"].StoreAsync()).Incarnation : Ulid.NewUlid();
        var blocked = ChangeGroups.Wire(cluster.Database, incarnation);
        var healthy = ChangeGroups.Wire(other, Ulid.NewUlid());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (refillInFlight)
        {
            cluster["sjc"].BeforeDigest = async (database, token) =>
            {
                if (database == cluster.Database)
                {
                    _ = entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            };
        }

        using var channel = PeerChannel.Create(receiver.Api.Server.BaseAddress);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
        try
        {
            await call.RequestStream.WriteAsync(new PushFrame
            {
                Hello = new Hello { Node = origin.Value, Region = "sjc", Machine = refillInFlight ? cluster["sjc"].Name : "absent-refill-source" },
            });
            await Assert.That((await ReadAsync(call.ResponseStream)).Welcome).IsNotNull();

            var refused = new Ack { Database = blocked, Refused = true };
            await call.RequestStream.WriteAsync(new PushFrame { Refill = new Refill { Database = blocked, ChangeId = 5 } });
            if (refillInFlight)
            {
                await entered.Task.WaitAsync(ReplicationProtocol.ProgressTimeout);
            }
            else
            {
                await Assert.That((await ReadAsync(call.ResponseStream)).Ack).IsEqualTo(refused);
            }

            var id = EntityIds.New().ToString();
            var stamp = new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16);
            var version = new EntryVersion(stamp, origin);
            using var json = JsonDocument.Parse("""{"n":1}""");
            var delta = new ChangeDelta("items", id, stamp, DocumentState.Bottom.Put(version, json.RootElement).DeltaAt(stamp));
            var batch = new Batch { Database = blocked, LastChangeId = 6, Groups = ChangeGroups.Encode([new OriginGroup(6, [delta])]) };
            await call.RequestStream.WriteAsync(new PushFrame { Batch = batch });
            await Assert.That((await ReadAsync(call.ResponseStream)).Ack).IsEqualTo(refused);

            // More than the sender's 32 pending response slots: heartbeats alone cannot release frontier reservations.
            for (var frame = 0; frame < 33; frame++)
            {
                await call.RequestStream.WriteAsync(new PushFrame { Frontier = new Frontier { Database = blocked, Hlc = stamp.Value + (ulong)frame } });
                await call.RequestStream.WriteAsync(new PushFrame { Heartbeat = new Heartbeat() });
                await Assert.That(await ReadAsync(call.ResponseStream)).IsEqualTo(new AckFrame
                {
                    Stable = new StableReport { Database = blocked, Stable = 0 },
                });
                await Assert.That((await ReadAsync(call.ResponseStream)).KindCase).IsEqualTo(AckFrame.KindOneofCase.None);
            }

            var healthyBatch = batch.Clone();
            healthyBatch.Database = healthy;
            await call.RequestStream.WriteAsync(new PushFrame { Batch = healthyBatch });
            var acknowledged = (await ReadAsync(call.ResponseStream)).Ack;
            await Assert.That(acknowledged.Database).IsEqualTo(healthy);
            await Assert.That(acknowledged is { ChangeId: 6, Refused: false }).IsTrue();
            await Assert.That(await receiver.ReadAsync(other, id)).IsEqualTo("""{"n":1}""");

            await call.RequestStream.WriteAsync(new PushFrame { Batch = batch });
            await Assert.That((await ReadAsync(call.ResponseStream)).Ack).IsEqualTo(refused);
            await Assert.That(await receiver.ReadAsync(id)).IsNull();
        }
        finally
        {
            _ = release.TrySetResult();
        }
    }

    private static async Task<AckFrame> ReadAsync(IAsyncStreamReader<AckFrame> responses)
    {
        using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
        await Assert.That(await responses.MoveNext(timeout.Token)).IsTrue();
        return responses.Current;
    }
}
