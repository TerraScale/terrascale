using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Storage;
using TerraBase.Features.Management;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed partial class HistoryErasureBackupTests
{
    [Test]
    [Timeout(300_000)]
    public async Task A_burst_of_erasures_rewrites_large_auth_and_system_files_with_every_writer_hold_under_one_second(CancellationToken cancellationToken)
    {
        await using var host = new Host();
        RecordingRewrite recording = null!;
        await using var api = await host.StartAsync(services =>
        {
            // The harness freezes this clock by default; writer holds must measure real elapsed time.
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, TimeProvider.System);
            services.AddSingleton<IHealthyRewrite>(provider => recording = new RecordingRewrite(provider.GetRequiredService<TursoStorageEngine>(), host.Clock));
        });
        await Host.SeedAsync(api);
        var database = await api.CreateManagedDatabaseAsync();
        await api.SetPlanAsync(api.User, database.Organization, "enterprise", cancellationToken);
        using var customer = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(customer, database, "items", cancellationToken);
        var payload = Encoding.UTF8.GetBytes("{\"body\":\"" + new Faker().Random.AlphaNumeric(100 * 1024) + "\"}");
        for (var index = 0; index < 64; index++)
        {
            await Host.WriteAsync(api, async transaction => _ = await transaction.PutAsync(Users, Id(), payload, cancellationToken));
            await Host.WriteSystemAsync(api, transaction => transaction.PutAsync("batching_payload", Id(), payload, cancellationToken));
        }

        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var auth = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(Auth, cancellationToken))!;
        var system = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(cancellationToken);
        foreach (var store in new[] { auth, system })
        {
            var bytes = Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, Path.GetFileName(store.FilePath) + "*")
                .Sum(path => new FileInfo(path).Length);
            Console.WriteLine($"Erasure rewrite source {store.IsSystem}: {bytes} bytes");
            await Assert.That(bytes).IsGreaterThan(10L * 1024 * 1024);
        }

        var requested = host.Clock.GetUtcNow();
        var erasures = Enumerable.Range(0, 12).Select(_ => (Fence: Id(), Erasure: Id())).ToArray();
        foreach (var (fence, erasure) in erasures)
        {
            await Host.PutAsync(api, Jobs, fence, """{"scope":"account"}""");
            await Host.PutAsync(api, HistoryErasure.MarkCollection, erasure, $$"""{"kind":"request","erasureId":"{{erasure}}","fenceId":"{{fence}}"}""");
            await Host.RequestAsync(api, erasure, requested);
        }
        await Host.PassAsync(api);
        foreach (var (_, erasure) in erasures)
        {
            var settled = await Host.RequestAsync(api, erasure, requested);
            await Assert.That(settled.SettlementToken).IsNotNull();
            await Assert.That((await Host.RequestAsync(api, erasure, requested, settled.SettlementToken)).Status).IsEqualTo(HistoryErasure.Rewriting);
        }

        using var holds = new WriterHoldTests.WriterHolds(api.Server);
        using var authRelease = new ManualResetEventSlim();
        using var systemRelease = new ManualResetEventSlim();
        var (authPinned, systemPinned, authWrites, systemWrites, committed) = (0, 0, 0, 0, 0);
        // Prove a public write commits with each real read snapshot pinned, before permitting its bulk copy to proceed.
        auth.RewriteSnapshotPinned = token => { Volatile.Write(ref authPinned, 1); authRelease.Wait(token); };
        system.RewriteSnapshotPinned = token => { Volatile.Write(ref systemPinned, 1); systemRelease.Wait(token); };
        for (var attempt = 0; attempt < 4; attempt++)
        {
            authRelease.Reset();
            systemRelease.Reset();
            Volatile.Write(ref authPinned, 0);
            Volatile.Write(ref systemPinned, 0);
            var pass = Host.PassAsync(api);
            try
            {
                while (!pass.IsCompleted && committed < 2048)
                {
                    using var response = await customer.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "customer-" + committed)),
                        new { committed }, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    committed++;
                    if (Volatile.Read(ref authPinned) == 1)
                    {
                        if (auth.Rewriting)
                        {
                            authWrites++;
                        }
                        authRelease.Set();
                    }
                    if (Volatile.Read(ref systemPinned) == 1)
                    {
                        if (system.Rewriting)
                        {
                            systemWrites++;
                        }
                        systemRelease.Set();
                    }
                }
            }
            finally
            {
                authRelease.Set();
                systemRelease.Set();
                await pass;
            }

            if ((await Host.RequestAsync(api, erasures[^1].Erasure, requested)).Status == HistoryErasure.Residue)
            {
                break;
            }
            host.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        var first = await Host.OwnAsync(api, erasures[0].Erasure);
        await Assert.That(first.Auth).IsNotNull();
        await Assert.That(first.System).IsNotNull();
        foreach (var (_, erasure) in erasures)
        {
            await Assert.That((await Host.RequestAsync(api, erasure, requested)).Status).IsEqualTo(HistoryErasure.Residue);
            var receipt = await Host.OwnAsync(api, erasure);
            await Assert.That(receipt.Auth).IsEqualTo(first.Auth);
            await Assert.That(receipt.System).IsEqualTo(first.System);
        }
        await Assert.That(recording.Starts.Count - recording.BudgetOrPressureRefusals).IsEqualTo(2);
        await Assert.That(authWrites).IsGreaterThan(0);
        await Assert.That(systemWrites).IsGreaterThan(0);
        await Assert.That(committed).IsLessThan(2048);
        using var last = await customer.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "customer-" + (committed - 1))), cancellationToken);
        last.EnsureSuccessStatusCode();
        await Assert.That(holds.Measured("a healthy rewrite").Count).IsGreaterThanOrEqualTo(2);
        foreach (var holder in holds.Holders)
        {
            var longest = holds.Measured(holder).Max();
            Console.WriteLine($"Erasure batch holder {holder}: max {longest:F6} s; customer commits {committed} (auth {authWrites}, SYSTEM {systemWrites})");
            await Assert.That(longest).IsLessThan(1).Because(holder);
        }
    }
}
