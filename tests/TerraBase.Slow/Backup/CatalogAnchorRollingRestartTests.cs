using System.Collections.Concurrent;
using System.Diagnostics;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

/// <summary>
/// Batch 8 (2026-10-25): after a roll, a writer held idle for 23.5 s made every catalog anchor time out
/// (<see cref="CatalogGate.AnchorTimeout"/>), and ts-auth saw TerraBase unavailable in all three regions. Here two regions
/// keep writing and anchoring their catalogs while the third restarts twice, as a rolling deploy does: every anchor, the
/// restarted region's included, is written well within its deadline.
/// </summary>
public sealed class CatalogAnchorRollingRestartTests
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GateOpens = TimeSpan.FromMinutes(3);

    [Test]
    public async Task Anchors_stay_within_their_deadline_while_a_region_restarts_under_catalog_writes()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        var members = new FakeMemberStore();
        await using var gru = new RestartingMachine("gru", bucket, members);
        await using var sjc = new RestartingMachine("sjc", bucket, members);
        await using var fra = new RestartingMachine("fra", bucket, members);
        foreach (var machine in (RestartingMachine[])[gru, sjc, fra])
        {
            await machine.StartAsync();
        }

        foreach (var machine in (RestartingMachine[])[gru, sjc, fra])
        {
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(machine.Gate.IsOpen), timeout: TimeSpan.FromSeconds(30));
        }

        var anchors = new ConcurrentQueue<(string Region, TimeSpan Took, bool Held)>();
        var writes = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);

        async Task WriteAsync(RestartingMachine machine)
        {
            while (!stop.IsCancellationRequested)
            {
                _ = await machine.Api.CreateOrganizationAsync("anchor load " + EntityIds.New());
                Interlocked.Increment(ref writes);
            }
        }

        async Task AnchorAsync(RestartingMachine machine, string region, int count)
        {
            for (var i = 0; i < count && !stop.IsCancellationRequested; i++)
            {
                var started = Stopwatch.GetTimestamp();
                var held = await machine.Gate.TryAnchorAsync(token);
                anchors.Enqueue((region, Stopwatch.GetElapsedTime(started), held));
            }
        }

        var load = Task.WhenAll(Task.Run(() => WriteAsync(gru), CancellationToken.None), Task.Run(() => WriteAsync(sjc), CancellationToken.None));
        try
        {
            for (var restart = 0; restart < 2; restart++)
            {
                var peers = Task.WhenAll(AnchorAsync(gru, "gru", 50), AnchorAsync(sjc, "sjc", 50));
                await fra.StopAsync();
                // fra misses catalog changes and the peers' new anchors while it is down.
                var missed = Volatile.Read(ref writes) + 50;
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Volatile.Read(ref writes) >= missed), timeout: TimeSpan.FromSeconds(60));
                await fra.StartAsync();
                // The peers anchored markers fra missed: its gate opens once a revocation snapshot or their streams cover them,
                // which a loaded runner takes longer than a minute for. Only the anchors are bounded here.
                try
                {
                    await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(fra.Gate.IsOpen), timeout: GateOpens);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Assert.Fail($"restart {restart}: fra's catalog gate stayed closed, waiting for: {string.Join("; ", fra.Gate.Waiting)}");
                }
                await Task.WhenAll(peers, AnchorAsync(fra, "fra", 50), WriteCountAsync(fra, 50));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await load;
        }

        var late = anchors.Where(anchor => !anchor.Held || anchor.Took >= Within).Select(anchor => $"{anchor.Region} {anchor.Took.TotalSeconds:F1} s {anchor.Held}").ToList();
        await Assert.That(late).IsEmpty();
        await Assert.That(anchors.Count).IsEqualTo(300);

        async Task WriteCountAsync(RestartingMachine machine, int count)
        {
            for (var i = 0; i < count; i++)
            {
                _ = await machine.Api.CreateOrganizationAsync("restarted load " + EntityIds.New());
            }
        }
    }
}
