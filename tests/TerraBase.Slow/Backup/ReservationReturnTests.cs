using System.Diagnostics.Metrics;
using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class ReservationReturnTests
{
    [Test]
    public async Task Failed_uploads_release_every_disk_and_memory_reservation()
    {
        await using var host = await BackupShippingHost.StartAsync();
        var meter = host.Api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long disk = -1, memory = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && (instrument.Name == "terrabase.disk.reserved" || instrument.Name == "terrabase.memory.reserved"))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "terrabase.disk.reserved")
            {
                Interlocked.Exchange(ref disk, value);
            }
            else
            {
                Interlocked.Exchange(ref memory, value);
            }
        });
        listener.Start();
        long Disk() { listener.RecordObservableInstruments(); return Interlocked.Read(ref disk); }
        long Memory() { listener.RecordObservableInstruments(); return Interlocked.Read(ref memory); }

        await host.ShipAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        var generation = host.Status().Generation!;
        var segments = host.Segments(generation).Count;
        var baseline = Disk();
        var baselineMemory = Memory();
        await Assert.That(baseline).IsGreaterThanOrEqualTo(0L);

        host.S3.FailPut = key => key.StartsWith(host.File, StringComparison.Ordinal);
        var faker = new Faker();
        for (var write = 0; write < 3; write++)
        {
            await host.PutAsync("people", $"failed-{write}", faker.Lorem.Sentence(), CancellationToken.None);
            host.Time.Advance(TimeSpan.FromSeconds(2));
            await host.StartPassAsync(CancellationToken.None);
        }

        await Assert.That(host.Status().LastError).IsNotNull();
        await Assert.That(Disk()).IsGreaterThan(baseline);

        for (var pass = 0; pass < 12; pass++)
        {
            host.Time.Advance(TimeSpan.FromMinutes(5));
            await host.StartPassAsync(CancellationToken.None);
        }

        host.S3.FailPut = _ => false;
        host.Time.Advance(TimeSpan.FromMinutes(5));
        await host.StartPassAsync(CancellationToken.None);
        for (var pass = 0; pass < 4 && host.Status().PendingBytes > 0; pass++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(1));
            await host.StartPassAsync(CancellationToken.None);
        }

        await Assert.That(host.Status().PendingBytes).IsEqualTo(0);
        await Assert.That(Disk()).IsLessThanOrEqualTo(baseline + DiskBudget.WorkspaceBytes(0));
        await Assert.That(Memory()).IsLessThanOrEqualTo(baselineMemory + 3 * 4096L);
        await Assert.That(Memory()).IsGreaterThanOrEqualTo(0L);
        await Assert.That(host.Segments(generation).Count).IsGreaterThan(segments);
        using var read = await host.Api.Client.GetAsync(
            ManagementApi.Uri(ManagementApi.DocumentPath(host.Database, "people", "failed-2")), CancellationToken.None);
        await Assert.That(read.StatusCode).IsNotEqualTo(HttpStatusCode.Ambiguous);
    }
}
