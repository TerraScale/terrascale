using System.Diagnostics.Metrics;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class PendingDiskClaimTests
{
    [Test]
    public async Task A_failed_key_refresh_retries_pending_frames_under_their_existing_disk_claim(CancellationToken cancellationToken)
    {
        await using var host = await BackupShippingHost.StartAsync();
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var generation = host.Status().Generation!;
        var prior = host.Segments(generation).Count;
        var meter = host.Api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long reserved = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "terrabase.disk.reserved")
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Exchange(ref reserved, value));
        listener.Start();
        var keyPrefix = $"v1/keys/{host.Database.Organization}/{host.Database.Project}/";
        var attempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var retryStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? retry = null;
        try
        {
            host.S3.BeforeList = (prefix, _) =>
            {
                if (prefix == keyPrefix && Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidDataException("key refresh failed before sealing");
                }
                return Task.CompletedTask;
            };
            await host.PutAsync("people", "retained", new Faker().Lorem.Sentence(), cancellationToken);
            await host.ShipAsync(TimeSpan.FromSeconds(61), cancellationToken);
            await Assert.That(attempts).IsEqualTo(1);
            await Assert.That(host.Status().LastError).IsNotNull();
            await Assert.That(host.Status().PendingBytes).IsGreaterThan(0);
            await Assert.That(host.Segments(generation).Count).IsEqualTo(prior);
            listener.RecordObservableInstruments();
            var retained = reserved;
            await Assert.That(retained).IsGreaterThanOrEqualTo(DiskBudget.WorkspaceBytes(0));
            var bytes = new DriveInfo(host.Api.Server.DataDirectory).AvailableFreeSpace - DiskBudget.Headroom - retained - (8L << 20);
            using var occupied = host.Api.Server.Services.GetRequiredService<IDiskBudget>().Reserve(bytes);
            host.S3.BeforePut = async (key, token) =>
            {
                if (key.StartsWith(host.File + "/wal/", StringComparison.Ordinal))
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            };
            host.Time.Advance(TimeSpan.FromSeconds(2));
            retry = host.StartPassAsync(retryStop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            listener.RecordObservableInstruments();
            await Assert.That(reserved).IsEqualTo(bytes + retained);
            release.TrySetResult();
            await retry.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await Assert.That(host.Segments(generation).Count).IsEqualTo(prior + 1);
            await Assert.That(host.Status().PendingBytes).IsEqualTo(0);
            listener.RecordObservableInstruments();
            await Assert.That(reserved).IsEqualTo(bytes);
        }
        finally
        {
            host.S3.BeforeList = null;
            host.S3.BeforePut = null;
            release.TrySetResult();
            await retryStop.CancelAsync();
            if (retry is not null)
            {
                await retry.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
}
