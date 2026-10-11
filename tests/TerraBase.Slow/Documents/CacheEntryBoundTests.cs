using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

public sealed class CacheEntryBoundTests
{
    [Test]
    public async Task Many_small_query_pages_evict_by_entry_count_before_the_byte_budget()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await ManagementApi.StartAsync(settings =>
            settings["memory-budget"] = (64L * 1024 * 1024).ToString(CultureInfo.InvariantCulture), managementCredential: true);
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        await management.SetPlanAsync(tenant.User, tenant.OrganizationId, "enterprise", deadline.Token);
        using (var database = await tenant.Data.PutAsync(tenant.Database("bounded"), null, deadline.Token))
        {
            database.EnsureSuccessStatusCode();
        }
        using (var collection = await tenant.Data.PostAsJsonAsync(tenant.Database("bounded", "/collections"),
            new { name = "items" }, deadline.Token))
        {
            collection.EnsureSuccessStatusCode();
        }

        var meter = management.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long misses = 0;
        var entries = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name is "terrabase.document_cache.lookups" or "terrabase.document_cache.entries"
                && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var matching = 0;
            foreach (var tag in tags)
            {
                if ((tag.Key == "cache" && Equals(tag.Value, "page"))
                    || (tag.Key == "result" && Equals(tag.Value, "miss")))
                {
                    matching++;
                }
            }
            if (matching == 2)
            {
                Interlocked.Add(ref misses, value);
            }
        });
        listener.SetMeasurementEventCallback<int>((_, value, _, _) => Interlocked.Exchange(ref entries, value));
        listener.Start();

        // Empty pages are tiny: 4 097 distinct queries fit in the byte budget but exceed 4 096 entries.
        for (var query = 0; query <= 4096; query++)
        {
            await ReadAsync(query);
        }
        listener.RecordObservableInstruments();
        await Assert.That(Volatile.Read(ref entries)).IsEqualTo(4096);
        await Assert.That(Volatile.Read(ref misses)).IsEqualTo(4097);
        await ReadAsync(4096);
        await Assert.That(Volatile.Read(ref misses)).IsEqualTo(4097);
        await ReadAsync(0);
        await Assert.That(Volatile.Read(ref misses)).IsEqualTo(4098);

        async Task ReadAsync(int query)
        {
            using var response = await tenant.Data.PostAsJsonAsync(tenant.Database("bounded", "/collections/items/query"),
                new { filter = new { eq = new object[] { "/n", query } }, limit = 1 }, deadline.Token);
            response.EnsureSuccessStatusCode();
        }
    }
}
