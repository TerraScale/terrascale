using System.Security.Cryptography;
using System.Text;
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
/// Log shipping through the object store (LogChunks). A receiver applies a chunk reference as the batch it holds and refuses
/// one it cannot read as unread; a catch-up larger than the break-even size that two regions need reaches them as sealed
/// chunks put once and read by both; a peer that does not read chunks (an older build) gets every batch inline, and a single
/// reader gets no chunk. Real machines; the bucket is an in-memory store each machine is given.
/// </summary>
public sealed class LogChunkTests
{
    // Documents of 3 KiB of base64: 480 ship about 1.1 MB, five times the gru break-even of two readers.
    private const int Documents = 480;

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

    [Test]
    public async Task A_catch_up_ships_through_sealed_chunks_each_put_once_and_read_by_every_region()
    {
        var bucket = new MemoryObjectStore();
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: true, clock: null,
            (region, services) => services.AddSingleton<IObjectStore>(bucket.For(region)), "gru", "sjc", "fra");
        var (gru, sjc, fra) = (cluster["gru"], cluster["sjc"], cluster["fra"]);
        var written = await CatchUpAsync(gru, [sjc, fra], sequential: true);

        foreach (var (id, json) in written)
        {
            await Assert.That(await sjc.UntilAsync(id, json) && await fra.UntilAsync(id, json)).IsTrue().Because(id);
        }

        // Each chunk went to the bucket once, sealed, and both regions read it: sjc while it caught up, fra after (and while
        // it refused its batches).
        var chunks = bucket.Objects.Keys.Where(key => key.StartsWith(LogChunks.Prefix, StringComparison.Ordinal)).ToList();
        await Assert.That(chunks).IsNotEmpty();
        foreach (var key in chunks)
        {
            await Assert.That(bucket.Objects[key].AsSpan(0, 4).SequenceEqual("TBL1"u8)).IsTrue();
            await Assert.That(bucket.Puts[key]).IsEqualTo(1);
            await Assert.That(bucket.Readers(key)).Contains("sjc").And.Contains("fra").Because($"sjc and fra both read {key}");
        }
    }

    [Test]
    public async Task A_peer_that_does_not_read_chunks_gets_every_batch_inline_and_one_reader_alone_gets_no_chunk()
    {
        var bucket = new MemoryObjectStore();
        // fra has no bucket: its Welcome says it reads no chunks, as an older build's does.
        await using var cluster = await PeerStreamTests.Cluster.StartAsync(cdc: true, clock: null,
            (region, services) =>
            {
                if (region != "fra")
                {
                    services.AddSingleton<IObjectStore>(bucket.For(region));
                }
            }, "gru", "sjc", "fra");
        var (gru, sjc, fra) = (cluster["gru"], cluster["sjc"], cluster["fra"]);
        var written = await CatchUpAsync(gru, [sjc, fra], sequential: false);

        foreach (var (id, json) in written)
        {
            await Assert.That(await sjc.UntilAsync(id, json) && await fra.UntilAsync(id, json)).IsTrue().Because(id);
        }

        await Assert.That(bucket.Puts.Keys.Where(key => key.StartsWith(LogChunks.Prefix, StringComparison.Ordinal))).IsEmpty()
            .Because("sjc alone reads chunks, and for one reader a send costs less than a chunk");
    }

    // With `peers` refusing the database's batches after they acked a first write, gru commits the documents; then the peers
    // apply again and catch up from their acks through the retained log, one after the other when `sequential`.
    private static async Task<Dictionary<string, string>> CatchUpAsync(PeerStreamTests.Machine gru, PeerStreamTests.Machine[] peers, bool sequential)
    {
        var database = gru.Cluster.Database;
        var first = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", first, Encoding.UTF8.GetBytes("""{"n":0}"""), CancellationToken.None);
        });
        foreach (var peer in peers)
        {
            await Assert.That(await peer.UntilAsync(first, """{"n":0}""")).IsTrue();
            await Assert.That(await gru.UntilAckedAsync(database, peer.Node, 1)).IsTrue();
            peer.BeforeApply = (applied, _, _) => applied == database ? throw new IOException("the test holds this database's batches") : Task.CompletedTask;
        }

        var written = new Dictionary<string, string>();
        for (var index = 0; index < Documents; index++)
        {
            var id = EntityIds.New().ToString();
            var json = $$"""{"p":"{{Convert.ToBase64String(RandomNumberGenerator.GetBytes(3 * 768))}}"}""";
            written[id] = json;
            await gru.WriteAsync(async transaction => _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes(json), CancellationToken.None));
        }

        var last = written.Last();
        foreach (var peer in peers)
        {
            peer.BeforeApply = null;
            if (sequential)
            {
                await Assert.That(await peer.UntilAsync(last.Key, last.Value)).IsTrue();
            }
        }

        return written;
    }
}
