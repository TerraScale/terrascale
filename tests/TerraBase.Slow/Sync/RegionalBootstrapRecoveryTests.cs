using System.Diagnostics;
using System.Text.Json;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;

namespace TerraBase.Slow.Sync;

public sealed partial class RegionalWriterCrashTests
{
    [Test]
    [Timeout(120_000)]
    public async Task Bootstrap_crashes_between_protocol_and_owner_publications_resume_the_pinned_owner_within_the_bound(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        var store = await new NatsKVContext(new NatsJSContext(drill.Connection)).GetStoreAsync(RegionalHandoff.Bucket, token);
        foreach (var cut in new[] { "legacy-marker-first", "owner-first" })
        {
            await drill.Source.KillAsync();
            await drill.Target.KillAsync();
            var current = await store.GetEntryAsync<byte[]>("gru", cancellationToken: token);
            var legacy = new RegionalOwner("gru", "gru-1", 7);
            _ = await store.UpdateAsync("gru", JsonSerializer.SerializeToUtf8Bytes(legacy, ReplicasJsonContext.Default.RegionalOwner), current.Revision, cancellationToken: token);
            await store.DeleteAsync("protocol.gru", cancellationToken: token);
            await store.DeleteAsync("prepared.gru.gru-1", cancellationToken: token);
            var stoppedAt = drill.SourceNats.Drop((subject, payload) => cut == "owner-first"
                ? subject == "$KV." + RegionalHandoff.Bucket + ".protocol.gru"
                : subject == "$KV." + RegionalHandoff.Bucket + ".gru"
                    && JsonSerializer.Deserialize(payload.Span, ReplicasJsonContext.Default.RegionalOwner) is { Activated: true, Version: 2 });
            await drill.Source.StartAsync(token);
            await stoppedAt.WaitAsync(TimeSpan.FromSeconds(15), token);
            if (cut == "legacy-marker-first")
            {
                _ = await store.PutAsync("protocol.gru", "2"u8.ToArray(), cancellationToken: token);
                await Assert.That(await drill.OwnerAsync(token)).IsEqualTo(legacy);
                var certificate = await store.GetEntryAsync<byte[]>("prepared.gru.gru-1", cancellationToken: token);
                await Assert.That(JsonSerializer.Deserialize(certificate.Value!, ReplicasJsonContext.Default.RegionalPrepared))
                    .IsEqualTo(new RegionalPrepared("gru-1", "gru-1", 6, null));
            }
            else
            {
                await Assert.That(await drill.OwnerAsync(token) is { Node: "gru-1", Epoch: 7, Activated: true, Version: 2 }).IsTrue();
            }
            await Assert.That((await RecoveryDrill.StatusAsync(drill.SourceHttp, token)).Writer).IsFalse();
            await drill.Source.KillAsync();
            drill.SourceNats.Allow();
            var recovering = Stopwatch.StartNew();
            await drill.Source.StartAsync(token);
            await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
            await Assert.That(recovering.Elapsed < RegionalHandoff.RecoveryBound).IsTrue();
            await Assert.That(await drill.OwnerAsync(token) is { Node: "gru-1", Epoch: 7, Activated: true, Version: 2 }).IsTrue();
            await drill.AssertRowsAsync(drill.SourceHttp, cut == "legacy-marker-first"
                ? ["ack-0", "ack-1", "ack-2"] : ["ack-0", "ack-1", "ack-2", "legacy-marker-first"], token);
            await drill.ExecuteAsync(drill.SourceHttp, $"INSERT INTO acked VALUES ('{cut}')", token);
            _ = await store.GetEntryAsync<byte[]>("protocol.gru", cancellationToken: token);
            Console.WriteLine($"DRILL bootstrap crash {cut}: recovered_ms={recovering.Elapsed.TotalMilliseconds:F3}; bound_ms={RegionalHandoff.RecoveryBound.TotalMilliseconds:F0}; pinned Previous:null owner; acknowledged rows retained");
        }
    }
}
