using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class RequestLogMemoryTests
{
    [Test]
    [Arguments(5, false)]
    [Arguments(5, true)]
    [Arguments(1, true)]
    public async Task Database_rings_reclaim_process_capacity_with_bounded_reported_loss(int databaseCount, bool foregroundPressure)
    {
        const long Budget = 4 * 1024 * 1024;
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await RequestLogMemoryFixture.StartAsync(
            settings => settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture), time: clock);
        using var first = await api.CreateTenantAsync("documents:read", "documents:write");
        using var second = await api.CreateTenantAsync("documents:read", "documents:write");
        using var third = await api.CreateTenantAsync("documents:read", "documents:write");
        using var fourth = await api.CreateTenantAsync("documents:read", "documents:write");
        using var fifth = await api.CreateTenantAsync("documents:read", "documents:write");
        var tenants = new[] { first, second, third, fourth, fifth }.Take(databaseCount).ToArray();
        var pending = databaseCount * 100L;
        var databases = new List<TenantContext>();
        foreach (var tenant in tenants) { databases.Add(await tenant.ContextAsync(api.Server, "app", token)); }
        api.Server.Services.GetRequiredService<DocumentCache>().Dispose();
        await RequestLogMemoryFixture.DrainAsync(api.Server, token);
        var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
        var logs = api.Server.Services.GetRequiredService<IRequestLogs>();
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        var dropped = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Name == "terrabase.request_logs.dropped" && ReferenceEquals(instrument.Meter, meter))
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, count, _, _) => Interlocked.Add(ref dropped, count));
        listener.Start();

        // Seed a persisted row too: flush admission must leave room for actual engine result admission.
        await RequestsAsync(1);
        await RequestLogMemoryFixture.DrainAsync(api.Server, token);
        var started = clock.GetUtcNow();
        await RequestsAsync(100);
        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(token);
        await Assert.That(Interlocked.Read(ref dropped)).IsEqualTo(0);
        using (var spare = MemoryAccounting.ReserveAvailable(api.Server, MemoryLane.Foreground, Budget - pending * 4096))
        {
            await Assert.That(spare).IsNotNull();
            using var extra = budget.TryReserve(MemoryLane.Foreground, 1, tenant: null);
            await Assert.That(extra).IsNull().Because("every completed HTTP observation must retain a process lease");
        }

        if (foregroundPressure)
        {
            using var foreground = budget.TryReserve(MemoryLane.Foreground, Budget / 2, tenant: null);
            await Assert.That(foreground).IsNotNull();
            await logs.FlushAsync(token);
            // Even discarding every log could not admit this flush while real foreground work holds half the budget.
            await Assert.That(Interlocked.Read(ref dropped)).IsEqualTo(Math.Min(100, pending / 2));
        }

        await RequestLogMemoryFixture.DrainAsync(api.Server, token);
        using (var released = MemoryAccounting.ReserveAvailable(api.Server, MemoryLane.Foreground, Budget))
        {
            await Assert.That(released).IsNotNull();
        }
        var losses = Interlocked.Read(ref dropped);
        await Assert.That(losses).IsGreaterThan(0);
        await Assert.That(losses).IsLessThanOrEqualTo(100);
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var transaction = await store.BeginTransactionAsync(write: false, token);
        var recent = 0;
        foreach (var database in databases)
        {
            var saved = await transaction.ListAsync($"databases/{database.DatabaseId}/request_logs", null, 101, 512 * 1024, token);
            await Assert.That(saved.HasMore).IsFalse();
            await Assert.That(saved.Documents.Count).IsLessThanOrEqualTo(100);
            recent += saved.Documents.Count(document =>
            {
                using var json = JsonDocument.Parse(document.Json);
                return json.RootElement.GetProperty("request").GetProperty("time").GetDateTimeOffset() > started;
            });
        }
        await Assert.That((long)recent).IsEqualTo(pending - losses);

        async Task RequestsAsync(int count)
        {
            for (var request = 0; request < count; request++)
            {
                // Each organization gets 50 requests/s, and the whole test stays below the ten-second flush tick.
                clock.Advance(TimeSpan.FromMilliseconds(20));
                foreach (var tenant in tenants)
                {
                    using var response = await tenant.Data.GetAsync(tenant.Database("app", "/collections"), token);
                    response.EnsureSuccessStatusCode();
                    await RequestLogMemoryFixture.WaitForRequestsAsync(api.Server, token);
                }
            }
        }
    }

    [Test]
    public async Task Completed_requests_use_process_memory_and_flush_the_latest_ring_on_a_four_MiB_server()
    {
        const long Budget = 4 * 1024 * 1024;
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await RequestLogMemoryFixture.StartAsync(
            settings => settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture), time: clock);
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        var database = await tenant.ContextAsync(api.Server, "app", token);
        // This test isolates retained observations from the separate document/API-key cache.
        api.Server.Services.GetRequiredService<DocumentCache>().Dispose();
        await RequestLogMemoryFixture.DrainAsync(api.Server, token);
        var started = clock.GetUtcNow();
        for (var batch = 0; batch < 2; batch++)
        {
            for (var request = 0; request < 60; request++)
            {
                // Replenish Hobby rate tokens without reaching the ten-second background flush tick.
                clock.Advance(TimeSpan.FromMilliseconds(20));
                using var response = await tenant.Data.GetAsync(tenant.Database("app", "/collections"), token);
                response.EnsureSuccessStatusCode();
                await RequestLogMemoryFixture.WaitForRequestsAsync(api.Server, token);
            }

            // Leave only request-log leases pending, so a missing process charge cannot pass this assertion.
            await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(token);
            using (var tenantShare = MemoryAccounting.ReserveAvailable(api.Server, MemoryLane.Foreground, Budget / 16, database))
            {
                await Assert.That(tenantShare).IsNotNull();
            }
            using (var process = MemoryAccounting.ReserveAvailable(api.Server, MemoryLane.Foreground, Budget))
            {
                await Assert.That(process).IsNull();
            }

            await RequestLogMemoryFixture.DrainAsync(api.Server, token);
            using var released = MemoryAccounting.ReserveAvailable(api.Server, MemoryLane.Foreground, Budget);
            await Assert.That(released).IsNotNull();
        }

        // The customer read API independently needs its tenant response allowance; inspect persisted state here.
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var transaction = await store.BeginTransactionAsync(write: false, token);
        var saved = await transaction.ListAsync($"databases/{database.DatabaseId}/request_logs", null, 101, 512 * 1024, token);
        await Assert.That(saved.HasMore).IsFalse();
        await Assert.That(saved.Documents.Count).IsEqualTo(100);
        var times = saved.Documents.Select(document =>
        {
            using var json = JsonDocument.Parse(document.Json);
            return json.RootElement.GetProperty("request").GetProperty("time").GetDateTimeOffset();
        }).ToArray();
        await Assert.That(times.Min()).IsEqualTo(started.AddMilliseconds(21 * 20));
        await Assert.That(times.Max()).IsEqualTo(started.AddMilliseconds(120 * 20));
    }
}
