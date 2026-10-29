using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

/// <summary>
/// Shutdown ships what generations hold and starts none, a failing daily snapshot backs off without holding segments back,
/// and a drill cancelled with its generation runs for the next one.
/// </summary>
public sealed class ShipperShutdownAndRetryTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Shutdown_bounds_a_blocked_writer_without_advancing_the_fixture_clock(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        await host.PutAsync("people", "seed", Faker.Lorem.Sentence(8), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var now = host.Time.GetUtcNow();
        var engine = host.Api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = ((TursoDatabaseStore)store).WithWritersHeldAsync(async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return 0;
        }, cancellationToken, "the shutdown regression");
        try
        {
            await entered.Task.WaitAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await ((IHostedService)host.Api.Server.Services.GetRequiredService<IContinuousBackup>()).StopAsync(deadline.Token);

            await Assert.That(deadline.IsCancellationRequested).IsFalse();
            await Assert.That(host.Time.GetUtcNow()).IsEqualTo(now);
        }
        finally
        {
            release.TrySetResult();
            await holding;
        }
    }

    [Test]
    public async Task Shutdown_ships_the_last_writes_while_it_cancels_a_daily_snapshot_upload(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        await host.PutAsync("people", "seed", Faker.Lorem.Sentence(8), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var generation = host.Status().Generation!;
        var daily = new HeldCall($"{host.File}/snapshots/");
        host.S3.BeforePut = daily.BeforeAsync;
        host.Time.Advance(TimeSpan.FromDays(1));
        var pass = host.StartPassAsync(cancellationToken);
        try
        {
            await daily.Entered.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
            await host.PutAsync("people", "last", Faker.Lorem.Sentence(8), cancellationToken);

            // The upload is still in flight when the service stops: stopping cancels it, and must still ship the last write.
            await ((IHostedService)host.Api.Server.Services.GetRequiredService<IContinuousBackup>()).StopAsync(cancellationToken);
        }
        finally
        {
            host.S3.BeforePut = null;
            daily.Release();
            await pass.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await Assert.That(host.Segments(generation).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Shutdown_starts_no_generation(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        await host.PutAsync("people", "seed", Faker.Lorem.Sentence(8), cancellationToken);
        var snapshots = $"{host.File}/snapshots/";
        // The database's first snapshot upload fails, so it has no generation; the retry is due when the service stops.
        host.S3.FailPut = key => key.StartsWith(snapshots, StringComparison.Ordinal);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await Assert.That(host.Status().Generation).IsNull();
        host.S3.FailPut = _ => false;
        host.Time.Advance(TimeSpan.FromSeconds(2));

        await ((IHostedService)host.Api.Server.Services.GetRequiredService<IContinuousBackup>()).StopAsync(cancellationToken);

        await Assert.That(host.S3.Objects.Keys.Count(key => key.StartsWith(snapshots, StringComparison.Ordinal))).IsEqualTo(0);
    }

    [Test]
    public async Task A_failing_daily_snapshot_backs_off_while_every_pass_ships_its_segment(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        await host.PutAsync("people", "seed", Faker.Lorem.Sentence(8), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var generation = host.Status().Generation!;
        var snapshots = $"{host.File}/snapshots/";
        var attempts = 0;
        host.S3.FailPut = key => key.StartsWith(snapshots, StringComparison.Ordinal) && Interlocked.Increment(ref attempts) > 0;

        // The daily snapshot falls due and its uploads fail for ten seconds of passes, one second apart, each with a write.
        await host.ShipAsync(TimeSpan.FromDays(1), cancellationToken);
        for (var pass = 0; pass < 10; pass++)
        {
            await host.PutAsync("people", $"doc-{pass}", Faker.Lorem.Sentence(8), cancellationToken);
            await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var failing = host.Status();
        await Assert.That(Volatile.Read(ref attempts)).IsLessThanOrEqualTo(4);
        await Assert.That(host.Segments(generation).Count).IsEqualTo(10);
        await Assert.That(failing.LastError).IsNotNull();
        await Assert.That(failing.LagSeconds).IsEqualTo(0);

        // Once uploads succeed again the next due attempt stores it, and the error clears.
        host.S3.FailPut = _ => false;
        for (var pass = 0; pass < 20 && host.S3.Objects.Keys.Count(key => key.StartsWith(snapshots, StringComparison.Ordinal)) < 2; pass++)
        {
            await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }

        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        await Assert.That(host.S3.Objects.Keys.Count(key => key.StartsWith(snapshots, StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(host.Status().LastError).IsNull();
    }

    [Test]
    public async Task A_drill_cancelled_when_its_generation_ends_runs_for_the_next_generation(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        await host.PutAsync("people", "seed", Faker.Lorem.Sentence(8), cancellationToken);
        // The first generation's drill holds at its download of the snapshot.
        var drill = new HeldCall($"{host.File}/snapshots/");
        host.S3.BeforeGet = drill.BeforeAsync;
        host.Time.Advance(TimeSpan.FromSeconds(10));
        var pass = host.StartPassAsync(cancellationToken);
        try
        {
            await drill.Entered.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
            var first = host.Status().Generation!;

            // Something else empties the capture table: a hole ends the generation, which cancels its drill.
            await host.PutAsync("people", "outside", Faker.Lorem.Sentence(8), cancellationToken);
            var tenant = new TenantContext(host.Database.Organization, PlanTier.Enterprise, host.Database.Project, host.Database.Id);
            var store = (TursoDatabaseStore)(await host.Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, cancellationToken))!;
            await store.WithWriterAsync(writer =>
            {
                using var delete = writer.Connection.CreateCommand();
                delete.CommandText = "DELETE FROM turso_cdc;";
                return delete.ExecuteNonQuery();
            }, "a test", cancellationToken);
            await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
            await Assert.That(host.Status().Generation).IsNotNull().And.IsNotEqualTo(first);
        }
        finally
        {
            host.S3.BeforeGet = null;
            drill.Release();
            await pass.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // The next generation's snapshot is drilled, not left for a day.
        await Assert.That(host.Status().LastDrill).IsNotNull();
        await Assert.That(host.Status().LastDrill!.Passed).IsTrue();
    }

    // Holds the first call under a prefix until released or cancelled.
    private sealed class HeldCall(string prefix)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task BeforeAsync(string key, CancellationToken cancellationToken)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && Interlocked.Increment(ref _calls) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
