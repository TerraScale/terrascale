using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

/// <summary>
/// Rolling deploys restart one region at a time while the others write. Twenty graceful restarts of one region, under catalog
/// writes from a peer and its own, every other one right after a healthy rewrite of its catalog: each restart comes back on
/// the same catalog file (its log replayed, never a freshly created file under a new identity), holding every change it
/// acknowledged, and its catalog gate opens again.
/// </summary>
public sealed class CatalogGateRollingRestartTests
{
    private const int Restarts = 20;
    // A loaded runner verifies a restarted region slower; a catalog closed as restored never reopens, however long it waits.
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Twenty_rolling_restarts_under_write_load_keep_the_catalog_and_reopen_the_gate()
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

        // A peer keeps writing the catalog, which replicates into gru while it serves.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var load = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = await sjc.Api.CreateOrganizationAsync("peer load " + EntityIds.New());
            }
        }, CancellationToken.None);

        try
        {
            for (var restart = 0; restart < Restarts; restart++)
            {
                var acknowledged = new List<string>();
                for (var write = 0; write < 5; write++)
                {
                    acknowledged.Add(await gru.Api.CreateOrganizationAsync($"gru {restart}.{write}"));
                }

                var engine = gru.Api.Server.Services.GetRequiredService<IStorageEngine>();
                if (restart % 2 == 0)
                {
                    var measured = (TursoDatabaseStore)await gru.Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(token);
                    measured.RewriteStepMeasured = (step, seconds) => Console.WriteLine($"REWRITE {step}: {seconds * 1000:F3} ms");
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            _ = await gru.Api.Server.Services.GetRequiredService<IHealthyRewrite>().RewriteAsync(null, 0, token);
                            break;
                        }
                        catch (RewriteRefusedException error) when (error.Reason == RewriteRefusal.Budget)
                        {
                            Console.WriteLine($"Rewrite attempt {attempt}/5 refused: {error.Message}");
                            if (attempt == 5)
                            {
                                throw;
                            }
                        }
                    }
                }

                var before = (await engine.OpenSystemDatabaseAsync(token)).Incarnation;
                await gru.StopAsync();
                await gru.StartAsync();

                // The same file came back: a lost log under a file holding no schema yet would be bootstrapped anew.
                var catalog = await gru.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
                await Assert.That(catalog.Incarnation).IsEqualTo(before);
                try
                {
                    await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gru.Gate.IsOpen), timeout: Within);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Assert.Fail($"restart {restart}: the catalog gate stayed closed, waiting for: {string.Join("; ", gru.Gate.Waiting)}");
                }

                await using var read = await catalog.BeginTransactionAsync(write: false, token);
                foreach (var organization in acknowledged)
                {
                    await Assert.That(await read.GetAsync("organizations", organization, token)).IsNotNull();
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await load;
        }
    }
}
