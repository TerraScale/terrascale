using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class QueryShapeMemoryTests
{
    private const long ScratchBytes = 64 * 1024;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Tenant_queries_release_normalization_scratch_before_storage_and_retain_shape_through_record(bool sql)
    {
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: services =>
        {
            hook.Install(services);
            InstallProbes(services);
        });
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using (var seeded = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "one")),
            new { n = 1, email = new Faker().Internet.Email() }))
        {
            seeded.EnsureSuccessStatusCode();
        }

        var memory = api.Server.Services.GetRequiredService<MemoryProbe>();
        var tracker = api.Server.Services.GetRequiredService<TrackerProbe>();
        using var gauge = new MemoryGauge(api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName));
        using var gate = new UsageCommitHook.Gate(fail: false);
        hook.BeforeBegin = (opened, cancellationToken) => opened.DatabaseId == database.Id
            ? gate.WaitAsync(cancellationToken)
            : ValueTask.CompletedTask;
        var path = sql ? database.Path + "/sql" : $"{database.Data}/collections/people/query";
        object body = sql ? new { sql = "SELECT * FROM people WHERE n = 1" } : new { filter = new { eq = new object[] { "/n", 1 } } };
        var client = sql ? api.Client : data;
        var pending = client.PostAsJsonAsync(ManagementApi.Uri(path), body);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(memory.Active.Any(reservation => reservation.Bytes == ScratchBytes)).IsFalse();
            await Assert.That(memory.Reservations.Count(reservation => reservation.Bytes == ScratchBytes)).IsEqualTo(1);
            // Admission while scratch was still held proves there was no unaccounted handoff to storage.
            await Assert.That(memory.Active.Any(reservation => reservation.AcquiredWithScratch && reservation.Bytes < ScratchBytes)).IsTrue();
        }
        finally
        {
            gate.Dispose();
            hook.BeforeBegin = null;
        }

        using (var response = await pending.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            response.EnsureSuccessStatusCode();
        }
        await AssertReservationsAsync(memory, tracker, gauge, expectedRecords: 1);

        if (!sql)
        {
            // The repeated JSON request uses the real document cache and still records one admitted observation.
            using var cached = await client.PostAsJsonAsync(ManagementApi.Uri(path), body);
            cached.EnsureSuccessStatusCode();
            await AssertReservationsAsync(memory, tracker, gauge, expectedRecords: 2);
            await Assert.That(tracker.Records.Last().RowsScanned).IsEqualTo(0);
        }

        var stats = await api.Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(database.Path[3..] + "/query-shapes"));
        await Assert.That(stats.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(stats.GetProperty("items")[0].GetProperty("queries").GetInt64()).IsEqualTo(sql ? 1L : 2L);
    }

    private static void InstallProbes(IServiceCollection services)
    {
        var registration = services.Single(service => service.ServiceType == typeof(IMemoryBudget));
        services.Remove(registration);
        services.AddSingleton(provider => new MemoryProbe((IMemoryBudget)registration.ImplementationFactory!(provider)));
        services.AddSingleton<IMemoryBudget>(provider => provider.GetRequiredService<MemoryProbe>());
        services.AddSingleton(provider => new TrackerProbe(provider.GetRequiredService<QueryShapeRecorder>(), provider.GetRequiredService<MemoryProbe>()));
        services.Replace(ServiceDescriptor.Singleton<IQueryShapeTracker>(provider => provider.GetRequiredService<TrackerProbe>()));
    }

    private static async Task AssertReservationsAsync(MemoryProbe memory, TrackerProbe tracker, MemoryGauge gauge, int expectedRecords)
    {
        var records = tracker.Records.ToArray();
        await Assert.That(records.Length).IsEqualTo(expectedRecords);
        foreach (var record in records)
        {
            await Assert.That(record.Shape.Length).IsGreaterThan(0);
            await Assert.That(record.Before.Any(reservation => reservation.Bytes == ScratchBytes)).IsFalse();
            await Assert.That(record.After.Any(reservation => reservation.Bytes == ScratchBytes)).IsFalse();
            var retained = record.Before.Where(reservation => reservation.Bytes == 32L + 2L * record.Shape.Length).ToArray();
            await Assert.That(retained.Length).IsEqualTo(1);
            await Assert.That(retained[0].Lane).IsEqualTo(MemoryLane.Foreground);
            await Assert.That(retained[0].AcquiredWithScratch).IsTrue();
            await Assert.That(record.After.Contains(retained[0])).IsTrue();
            await Assert.That(retained[0].Active).IsFalse();
        }

        var scratch = memory.Reservations.Where(reservation => reservation.Bytes == ScratchBytes).ToArray();
        await Assert.That(scratch.Length).IsEqualTo(expectedRecords);
        await Assert.That(scratch.All(reservation => reservation.Lane == MemoryLane.Foreground && !reservation.Active)).IsTrue();
        // Cache and recorder entries legitimately remain admitted; only the request's telemetry buffers must be gone.
        await Assert.That(gauge.Read()).IsEqualTo(memory.Active.Sum(reservation => reservation.Bytes));
    }

    private sealed class MemoryProbe(IMemoryBudget inner) : IMemoryBudget
    {
        public ConcurrentQueue<ObservedReservation> Reservations { get; } = new();

        public ObservedReservation[] Active => [.. Reservations.Where(reservation => reservation.Active)];

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            if (inner.TryReserve(lane, bytes, tenant) is not { } admitted)
            {
                return null;
            }

            var reservation = new ObservedReservation(admitted, lane, Active.Any(active => active.Bytes == ScratchBytes));
            Reservations.Enqueue(reservation);
            return reservation;
        }
    }

    private sealed class ObservedReservation(IMemoryReservation inner, MemoryLane lane, bool acquiredWithScratch) : IMemoryReservation
    {
        private int _disposed;

        public long Bytes => inner.Bytes;
        public MemoryLane Lane => lane;
        public bool AcquiredWithScratch => acquiredWithScratch;
        public bool Active => Volatile.Read(ref _disposed) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
            }
        }
    }

    private sealed class TrackerProbe(IQueryShapeTracker inner, MemoryProbe memory) : IQueryShapeTracker
    {
        public ConcurrentQueue<Observation> Records { get; } = new();

        public void Record(TenantContext database, string collection, string shape, TimeSpan latency, long rowsScanned)
        {
            var before = memory.Active;
            inner.Record(database, collection, shape, latency, rowsScanned);
            Records.Enqueue(new Observation(database, shape, rowsScanned, before, memory.Active));
        }

        public ValueTask<QueryShapeReadResult> ReadAsync(TenantContext database, CancellationToken cancellationToken) =>
            inner.ReadAsync(database, cancellationToken);
    }

    private sealed record Observation(TenantContext Database, string Shape, long RowsScanned, ObservedReservation[] Before, ObservedReservation[] After);

    private sealed class MemoryGauge : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _bytes;

        public MemoryGauge(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "terrabase.memory.reserved" && ReferenceEquals(instrument.Meter, meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, bytes, _, _) => _bytes = bytes);
            _listener.Start();
        }

        public long Read()
        {
            _listener.RecordObservableInstruments();
            return _bytes;
        }

        public void Dispose() => _listener.Dispose();
    }
}
