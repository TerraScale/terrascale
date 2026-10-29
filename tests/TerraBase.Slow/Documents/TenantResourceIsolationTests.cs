using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Slow.Management;
using TerraBase.Slow.Storage;
using TerraBase.Features.Memory;
using TerraBase.Fast.Platform;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

public sealed class TenantResourceIsolationTests
{
    private const long Budget = 4 * 1024 * 1024;

    [Test]
    public async Task Dashboard_catalog_results_use_process_memory_and_release_after_rejection()
    {
        await using var management = await RequestLogMemoryFixture.StartAsync(settings =>
            settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));
        using var tenant = await management.CreateTenantAsync("documents:read");
        using var owner = management.As(tenant.User);
        var route = new Uri("/v1/management/organizations", UriKind.Relative);
        using (var initial = await owner.GetAsync(route))
        {
            initial.EnsureSuccessStatusCode();
        }

        await RequestLogMemoryFixture.DrainAsync(management.Server);
        using (var held = MemoryAccounting.ReserveAvailable(management.Server, MemoryLane.Bulk, Budget - 16 * 1024))
        {
            await Assert.That(held).IsNotNull();

            using var refused = await owner.GetAsync(route);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo("tenant_memory");
        }

        using var recovered = await owner.GetAsync(route);
        recovered.EnsureSuccessStatusCode();
        await RequestLogMemoryFixture.DrainAsync(management.Server);
        using var all = MemoryAccounting.ReserveAvailable(management.Server, MemoryLane.Foreground, Budget);
        await Assert.That(all).IsNotNull();
    }

    [Test]
    [Arguments(PlanTier.Hobby)]
    [Arguments(PlanTier.Pro)]
    [Arguments(PlanTier.Enterprise)]
    public async Task Organization_memory_is_shared_across_projects_and_released_once(PlanTier plan)
    {
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
            settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));
        var budget = server.Services.GetRequiredService<IMemoryBudget>();
        var faker = new Faker();
        var tenant = new TenantContext(faker.Random.Guid().ToString(), plan, faker.Random.Guid().ToString());
        var share = Math.Min((long)(Budget * QosPolicy.For(plan).MemoryShare), MemoryAccounting.Available(server, Budget));
        using var first = budget.TryReserve(MemoryLane.Foreground, share - 1, tenant);
        using var second = budget.TryReserve(MemoryLane.Bulk, 1, tenant with { ProjectId = faker.Random.Guid().ToString() });
        using var refused = budget.TryReserve(MemoryLane.Bulk, 1, tenant);
        using var oversized = budget.TryReserve(MemoryLane.Foreground, long.MaxValue, tenant);
        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNotNull();
        await Assert.That(refused).IsNull();
        await Assert.That(oversized).IsNull();

        var metrics = await server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        var reserved = metrics.Split('\n').Where(line => line.StartsWith("terrabase_memory_reserved", StringComparison.Ordinal))
            .Select(line => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture));
        await Assert.That(reserved).Contains(share + MemoryAccounting.Retained(server));
        first!.Dispose();
        first.Dispose();
        second!.Dispose();
        using var recovered = budget.TryReserve(MemoryLane.Foreground, share, tenant);
        await Assert.That(recovered).IsNotNull();
        recovered!.Dispose();
        using var all = MemoryAccounting.ReserveAvailable(server, MemoryLane.Foreground, Budget);
        await Assert.That(all).IsNotNull();
        using var globalRefusal = budget.TryReserve(MemoryLane.Foreground, 1, tenant);
        await Assert.That(globalRefusal).IsNull();
    }

    [Test]
    public async Task Released_organizations_do_not_fill_the_bounded_memory_admission_table()
    {
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
            settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));
        var budget = server.Services.GetRequiredService<IMemoryBudget>();
        List<IMemoryReservation> reservations = [];
        try
        {
            for (var i = 0; i < 4096; i++)
            {
                var tenant = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby);
                var reservation = budget.TryReserve(MemoryLane.Foreground, 1, tenant);
                await Assert.That(reservation).IsNotNull();
                reservations.Add(reservation!);
            }
            var extra = new TenantContext(EntityIds.New().ToString(), PlanTier.Pro);
            using var refused = budget.TryReserve(MemoryLane.Foreground, 1, extra);
            await Assert.That(refused).IsNull();

            using var zero = budget.TryReserve(MemoryLane.Foreground, 0, extra);
            await Assert.That(zero).IsNotNull();
            reservations[0].Dispose();
            using var recovered = budget.TryReserve(MemoryLane.Foreground, 1, extra);
            await Assert.That(recovered).IsNotNull();
            using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            foreach (var reservation in reservations)
            {
                reservation.Dispose();
            }
        }
        using var all = MemoryAccounting.ReserveAvailable(server, MemoryLane.Foreground, Budget);
        await Assert.That(all).IsNotNull();
    }

    [Test]
    public async Task Request_memory_rejection_is_scoped_to_the_organization_and_releases_after_retry()
    {
        await using var management = await RequestLogMemoryFixture.StartAsync(settings =>
            settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));
        using var hobby = await management.CreateTenantAsync("documents:read", "documents:write");
        using var other = await management.CreateTenantAsync("documents:read", "documents:write");
        var database = await hobby.ContextAsync(management.Server, "shop");
        await other.ContextAsync(management.Server, "shop");
        await RequestLogMemoryFixture.DrainAsync(management.Server);
        using (var held = MemoryAccounting.ReserveAvailable(management.Server, MemoryLane.Bulk, Budget / 16, database))
        {
            await Assert.That(held).IsNotNull();
            using var refused = await hobby.Data.GetAsync(hobby.Database("shop", "/collections"));
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo("tenant_memory");
            using var unaffected = await other.Data.GetAsync(other.Database("shop", "/collections"));
            unaffected.EnsureSuccessStatusCode();
        }
        using var recovered = await hobby.Data.GetAsync(hobby.Database("shop", "/collections"));
        recovered.EnsureSuccessStatusCode();
        await RequestLogMemoryFixture.DrainAsync(management.Server);
        using var reusable = MemoryAccounting.ReserveAvailable(management.Server, MemoryLane.Foreground, Budget / 16, database);
        await Assert.That(reusable).IsNotNull();
    }

    [Test]
    public async Task A_downgrade_releases_old_cache_before_request_admission()
    {
        await using var management = await RequestLogMemoryFixture.StartAsync(settings =>
            settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        using var service = management.Service(tenant.User);
        var planUri = new Uri($"/v1/management/organizations/{tenant.OrganizationId}/plan", UriKind.Relative);
        using (var changed = await service.PutAsJsonAsync(planUri, new { plan = "enterprise" }))
        {
            changed.EnsureSuccessStatusCode();
        }
        var database = await tenant.ContextAsync(management.Server, "shop");
        await StorageFixture.CreateCollectionAsync(tenant, "shop", "items");
        var padding = new Faker().Random.String2(40 * 1024, "abcdef");
        for (var i = 0; i < 8; i++)
        {
            using var body = new StringContent($"{{\"name\":\"large\",\"padding\":\"{padding}\"}}", Encoding.UTF8, "application/json");
            using var written = await tenant.Data.PutAsync(
                tenant.Database("shop", $"/collections/items/documents/by-key?id={StorageFixture.Id($"b{i}")}"), body);
            written.EnsureSuccessStatusCode();
        }
        for (var i = 0; i < 8; i++)
        {
            using var warmed = await tenant.Data.GetAsync(tenant.Database("shop", $"/collections/items/documents/by-key?id={StorageFixture.Id($"b{i}")}"));
            warmed.EnsureSuccessStatusCode();
        }
        await RequestLogMemoryFixture.DrainAsync(management.Server);
        var budget = management.Server.Services.GetRequiredService<IMemoryBudget>();
        using (var oldRetention = budget.TryReserve(MemoryLane.Foreground, Budget - Budget / 16, database))
        {

            await Assert.That(oldRetention).IsNull();
        }
        using (var changed = await service.PutAsJsonAsync(planUri, new { plan = "hobby" }))
        {
            await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        using (var recovered = await tenant.Data.GetAsync(tenant.Database("shop", $"/collections/items/documents/by-key?id={StorageFixture.Id("b7")}")))
        {
            recovered.EnsureSuccessStatusCode();
        }
        await RequestLogMemoryFixture.DrainAsync(management.Server);
        using var released = budget.TryReserve(MemoryLane.Foreground, Budget / 16 - Budget / 64, database with { Plan = PlanTier.Hobby });
        await Assert.That(released).IsNotNull();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Result_memory_is_admitted_before_cold_or_cached_point_and_page_materialization(bool warm, bool page)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await RequestLogMemoryFixture.StartAsync(settings =>
            settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));
        using var hobby = await management.CreateTenantAsync("documents:read", "documents:write");
        using var paid = await management.CreateTenantAsync("documents:read", "documents:write");
        using (var service = management.Service(paid.User))
        using (var changed = await service.PutAsJsonAsync(
            new Uri($"/v1/management/organizations/{paid.OrganizationId}/plan", UriKind.Relative), new { plan = "pro" }, deadline.Token))
        {
            changed.EnsureSuccessStatusCode();
        }
        var database = await hobby.ContextAsync(management.Server, "shop", deadline.Token);
        await paid.ContextAsync(management.Server, "shop", deadline.Token);
        var padding = new Faker().Random.String2(32 * 1024, "abcdef");
        foreach (var tenant in new[] { hobby, paid })
        {
            await StorageFixture.CreateCollectionAsync(tenant, "shop", "items", deadline.Token);
            using var body = new StringContent($"{{\"name\":\"hot\",\"padding\":\"{padding}\"}}", Encoding.UTF8, "application/json");
            using var written = await tenant.Data.PutAsync(
                tenant.Database("shop", $"/collections/items/documents/by-key?id={StorageFixture.Id("hot")}"), body, deadline.Token);
            written.EnsureSuccessStatusCode();
        }
        var path = page ? "/collections/items/documents?limit=1" : $"/collections/items/documents/by-key?id={StorageFixture.Id("hot")}";
        if (warm)
        {
            using var warmed = await hobby.Data.GetAsync(hobby.Database("shop", path), deadline.Token);
            warmed.EnsureSuccessStatusCode();
        }
        await RequestLogMemoryFixture.DrainAsync(management.Server);
        var budget = management.Server.Services.GetRequiredService<IMemoryBudget>();
        using (var held = budget.TryReserve(MemoryLane.Bulk, Budget / 16 - 64 * 1024, database))
        {
            await Assert.That(held).IsNotNull();
            using var refused = await hobby.Data.GetAsync(hobby.Database("shop", path), deadline.Token);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That((await refused.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo("tenant_memory");
            using var unaffected = await paid.Data.GetAsync(paid.Database("shop", path), deadline.Token);
            unaffected.EnsureSuccessStatusCode();
        }
        using var recovered = await hobby.Data.GetAsync(hobby.Database("shop", path), deadline.Token);
        recovered.EnsureSuccessStatusCode();
        await RequestLogMemoryFixture.DrainAsync(management.Server);
        using var returned = budget.TryReserve(MemoryLane.Foreground, Budget / 16 - Budget / 64, database);
        await Assert.That(returned).IsNotNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Hobby_cache_churn_evicts_only_its_own_documents_and_pages(bool page)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await RequestLogMemoryFixture.StartAsync(settings =>
            settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture));
        using var hobby = await management.CreateTenantAsync("documents:read", "documents:write");
        using var paid = await management.CreateTenantAsync("documents:read", "documents:write");
        using (var service = management.Service(paid.User))
        using (var changed = await service.PutAsJsonAsync(
            new Uri($"/v1/management/organizations/{paid.OrganizationId}/plan", UriKind.Relative), new { plan = "pro" }, deadline.Token))
        {
            changed.EnsureSuccessStatusCode();
        }
        await hobby.ContextAsync(management.Server, "shop", deadline.Token);
        await paid.ContextAsync(management.Server, "shop", deadline.Token);
        await StorageFixture.CreateCollectionAsync(hobby, "shop", "items", deadline.Token);
        await StorageFixture.CreateCollectionAsync(paid, "shop", "items", deadline.Token);
        var padding = new Faker().Random.String2(48 * 1024, "abcdef");
        for (var i = 0; i < 24; i++)
        {
            using var body = new StringContent($"{{\"name\":\"hobby\",\"padding\":\"{padding}\"}}", Encoding.UTF8, "application/json");
            using var written = await hobby.Data.PutAsync(hobby.Database("shop", $"/collections/items/documents/by-key?id={StorageFixture.Id($"b{i}")}"), body, deadline.Token);
            written.EnsureSuccessStatusCode();
        }
        using (var body = new StringContent($"{{\"name\":\"paid\",\"padding\":\"{padding}\"}}", Encoding.UTF8, "application/json"))
        using (var written = await paid.Data.PutAsync(paid.Database("shop", $"/collections/items/documents/by-key?id={StorageFixture.Id("hot")}"), body, deadline.Token))
        {
            written.EnsureSuccessStatusCode();
        }
        await Assert.That(await ReadAsync(paid, "hot", page)).IsEqualTo("paid");
        await Assert.That(await ReadAsync(hobby, "b0", false)).IsEqualTo("hobby");

        await StorageFixture.ExecuteAsync(await paid.FileAsync(management.Server, "shop", deadline.Token),
            """UPDATE "items" SET doc = json_set(doc, '$.name', 'disk');""");
        await StorageFixture.ExecuteAsync(await hobby.FileAsync(management.Server, "shop", deadline.Token),
            $"UPDATE \"items\" SET doc = json_set(doc, '$.name', 'disk') WHERE id = '{StorageFixture.Id("b0")}';");
        for (var i = 1; i < 24; i++)
        {
            await Assert.That(await ReadAsync(hobby, $"b{i}", false)).IsEqualTo("hobby");
        }
        await Assert.That(await ReadAsync(paid, "hot", page)).IsEqualTo("paid");
        await Assert.That(await ReadAsync(hobby, "b0", false)).IsEqualTo("disk");

        async Task<string> ReadAsync(Tenant tenant, string id, bool list)
        {
            using var response = await tenant.Data.GetAsync(tenant.Database("shop", list
                ? "/collections/items/documents?limit=1"
                : $"/collections/items/documents/by-key?id={StorageFixture.Id(id)}"), deadline.Token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            return (list ? body.GetProperty("items")[0] : body).GetProperty("document").GetProperty("name").GetString()!;
        }
    }
}
