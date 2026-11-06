using System.Diagnostics;
using System.Text.Json;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Testing;

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
            _ = await store.UpdateAsync("gru", JsonSerializer.SerializeToUtf8Bytes(legacy, TerraBaseJsonContext.Default.RegionalOwner), current.Revision, cancellationToken: token);
            await store.DeleteAsync("protocol.gru", cancellationToken: token);
            await store.DeleteAsync("prepared.gru.gru-1", cancellationToken: token);
            var stoppedAt = drill.SourceNats.Drop((subject, payload) => cut == "owner-first"
                ? subject == "$KV." + RegionalHandoff.Bucket + ".protocol.gru"
                : subject == "$KV." + RegionalHandoff.Bucket + ".gru"
                    && JsonSerializer.Deserialize(payload.Span, TerraBaseJsonContext.Default.RegionalOwner) is { Activated: true, Version: 2 });
            await drill.Source.StartAsync(token);
            await stoppedAt.WaitAsync(TimeSpan.FromSeconds(15), token);
            if (cut == "legacy-marker-first")
            {
                // Reconstruct the exact old crash state: durable self-certificate
                // and protocol marker, but Version:0/Previous:null owner unchanged.
                _ = await store.PutAsync("protocol.gru", "2"u8.ToArray(), cancellationToken: token);
                await Assert.That(await drill.OwnerAsync(token)).IsEqualTo(legacy);
                var certificate = await store.GetEntryAsync<byte[]>("prepared.gru.gru-1", cancellationToken: token);
                await Assert.That(JsonSerializer.Deserialize(certificate.Value!, TerraBaseJsonContext.Default.RegionalPrepared))
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

    [Test]
    [Timeout(120_000)]
    public async Task V2_rolls_back_to_real_b443_on_retained_files_and_reupgrades_without_rewinding_acknowledged_writes(CancellationToken token)
    {
        await using var drill = await RecoveryDrill.StartAsync(token);
        await drill.Source.KillAsync();
        await drill.Target.KillAsync();
        var store = await new NatsKVContext(new NatsJSContext(drill.Connection)).GetStoreAsync(RegionalHandoff.Bucket, token);
        var current = await store.GetEntryAsync<byte[]>("gru", cancellationToken: token);
        _ = await store.UpdateAsync("gru", JsonSerializer.SerializeToUtf8Bytes(new RegionalOwner("gru", "gru-1", 7), TerraBaseJsonContext.Default.RegionalOwner), current.Revision, cancellationToken: token);
        await store.DeleteAsync("protocol.gru", cancellationToken: token);
        await store.DeleteAsync("prepared.gru.gru-1", cancellationToken: token);
        await drill.Source.StartAsync(token);
        await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        await drill.ExecuteAsync(drill.SourceHttp, "INSERT INTO acked VALUES ('v2-acked')", token);
        var retained = new Dictionary<string, NatsKVEntry<byte[]>>();
        foreach (var key in new[] { "gru", "protocol.gru", "prepared.gru.gru-1" })
        {
            retained[key] = await store.GetEntryAsync<byte[]>(key, cancellationToken: token);
        }
        await drill.Source.KillAsync();
        await Task.Delay(RegionalWriterLease.Ttl, token);
        // The actual legacy Program opens the same directory; no snapshot restore,
        // owner CAS, metadata rewind or prepared/protocol deletion is performed.
        await drill.Source.StartLegacyAsync(token);
        await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2", "v2-acked"], token);
        await drill.ExecuteAsync(drill.SourceHttp, "INSERT INTO acked VALUES ('rollback-acked')", token);
        foreach (var (key, before) in retained)
        {
            var after = await store.GetEntryAsync<byte[]>(key, cancellationToken: token);
            await Assert.That(after.Revision).IsEqualTo(before.Revision);
            await Assert.That(after.Value).IsEquivalentTo(before.Value!);
        }
        await drill.Source.KillAsync();
        await drill.Source.StartAsync(token);
        await RecoveryDrill.WaitWriterAsync(drill.SourceHttp, RegionalHandoff.RecoveryBound, token);
        await drill.ExecuteAsync(drill.SourceHttp, "INSERT INTO acked VALUES ('retry-acked')", token);
        await drill.AssertRowsAsync(drill.SourceHttp, ["ack-0", "ack-1", "ack-2", "v2-acked", "rollback-acked", "retry-acked"], token);
        await Assert.That(await drill.OwnerAsync(token) is { Node: "gru-1", Epoch: 7, Version: 2, Activated: true }).IsTrue();
        Console.WriteLine("DRILL v2 -> actual b443 -> v2: same retained files; all six acknowledged IDs exact; owner/epoch pinned; protocol/prepared retained through rollback");
    }
}
