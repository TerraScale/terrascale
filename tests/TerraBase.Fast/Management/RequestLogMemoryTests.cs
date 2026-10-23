using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;

namespace TerraBase.Fast.Management;

public sealed class RequestLogMemoryTests
{
    [Test]
    public async Task Completed_requests_use_process_memory_and_flush_the_latest_ring_on_a_four_MiB_server()
    {
        const long Budget = 4 * 1024 * 1024;
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await RequestLogMemoryFixture.StartAsync(
            settings => settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture), time: clock);
        using var tenant = await api.CreateTenantAsync("documents:read");
        var database = await tenant.ContextAsync(api.Server, "app", token);
        // This test isolates retained observations from the separate document/API-key cache.
        api.Server.Services.GetRequiredService<DocumentCache>().Dispose();
        await RequestLogMemoryFixture.DrainAsync(api.Server, token);
        var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
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
            using (var tenantShare = budget.TryReserve(MemoryLane.Foreground, Budget / 16, database))
            {
                await Assert.That(tenantShare).IsNotNull();
            }
            using (var process = budget.TryReserve(MemoryLane.Foreground, Budget, tenant: null))
            {
                await Assert.That(process).IsNull();
            }

            await RequestLogMemoryFixture.DrainAsync(api.Server, token);
            using var released = budget.TryReserve(MemoryLane.Foreground, Budget, tenant: null);
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
