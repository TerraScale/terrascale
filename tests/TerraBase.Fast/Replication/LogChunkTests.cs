using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Backup;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// Log shipping through the object store (LogChunks): a receiver applies a chunk reference as the batch it holds and refuses
/// one it cannot read as unread. Real machines; the bucket is an in-memory store each machine is given.
/// </summary>
public sealed class LogChunkTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_chunk_reference_applies_as_its_batch_and_one_that_cannot_be_read_is_refused_as_unread(bool bucketed)
    {
        var bucket = new MemoryObjectStore();
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: false, clock: null, (region, services) =>
        {
            if (bucketed)
            {
                services.AddSingleton<IObjectStore>(bucket.For(region));
            }
        }, "gru");
        var gru = cluster["gru"];
        await gru.WriteAsync(async transaction => _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None));
        using var channel = PeerChannel.Create(gru.Api.Server.BaseAddress, PeerTls.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
        var origin = ReplicaId.Of(1, 7, 0);
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome.Chunks).IsEqualTo(bucketed).Because("a machine reads chunks when it has a bucket");

        var (id, json) = (EntityIds.New().ToString(), """{"n":1}""");
        var at = new EntryVersion(new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16), origin);
        using var body = JsonDocument.Parse(json);
        var envelope = ChangeGroups.Encode([new OriginGroup(1, [new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp))])]);
        var key = LogChunks.Prefix + "sjc-machine/" + Ulid.NewUlid();
        var (sealedBytes, seal) = LogChunks.Seal(key, envelope.Span);
        var chunk = new LogChunk
        {
            Key = key,
            Seal = ByteString.CopyFrom(seal),
            Length = (uint)sealedBytes.Length,
            Decoded = (uint)ApplicationEnvelope.DecodedLength(envelope.Span),
        };
        var incarnation = Ulid.NewUlid();

        // Not in the bucket yet, then under another key: refused as unread, nothing applied.
        foreach (var unreadable in new[] { chunk, chunk.Clone() })
        {
            if (unreadable != chunk)
            {
                bucket.Objects[key] = sealedBytes;
                unreadable.Seal = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
            }

            var refused = await SendAsync(call, cluster, incarnation, unreadable);
            await Assert.That(refused is { Refused: true, ChunkUnread: true }).IsTrue().Because(refused.ToString());
            await Assert.That(await gru.ReadAsync(id)).IsNull();
        }

        // The chunk itself: applied as its inline batch would be (a machine without a bucket refuses it as unread).
        var acked = await SendAsync(call, cluster, incarnation, chunk);
        await Assert.That(acked.Refused).IsEqualTo(!bucketed).Because(acked.ToString());
        await Assert.That(await gru.ReadAsync(id)).IsEqualTo(bucketed ? json : null);
        await Assert.That(bucket.Readers(key)).IsEquivalentTo(bucketed ? ["gru"] : Array.Empty<string>());
    }

    private static async Task<Ack> SendAsync(AsyncDuplexStreamingCall<PushFrame, AckFrame> call, PeerStreamTests.Cluster cluster, Ulid incarnation, LogChunk chunk)
    {
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = ChangeGroups.Wire(cluster.Database, incarnation), LastChangeId = 1, Chunk = chunk } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        return call.ResponseStream.Current.Ack;
    }
}
