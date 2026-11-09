using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

[ClassDataSource<DashboardRootFixture>]
public sealed class OrganizationStorageJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task ConcurrentOrganizationWritesKeepEveryRowAndAProjectRefusesADuplicateDatabaseName()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        var account = await DatabaseJourney.NativeWorkspaceAsync(client);
        var secret = await HttpJourney.CreatePatAsync(client, "Organization storage", account.Password);
        using var identity = await client.GetFromJsonAsync<JsonDocument>("/api/v1/auth/me");
        var organizationId = identity!.RootElement.GetProperty("organizationId").GetString()!;
        using (var plan = await producer.PostAsJsonAsync("/__test/control", new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId }))
        {
            plan.EnsureSuccessStatusCode();
        }

        string[] names = ["concurrent-orders", "concurrent-events"];
        var created = await Task.WhenAll(names.Select(name => HttpJourney.McpAsync(client, secret, "tools/call", new { name = "create_database", arguments = new { name, region = "GRU" } })));
        await Assert.That(created.All(rpc => !rpc.GetProperty("result").TryGetProperty("isError", out var error) || !error.GetBoolean())).IsTrue();
        await using var storage = await scenario.StorageAsync();
        var stored = await storage.ProjectDatabases.AsNoTracking().Where(row => row.OrganizationId == organizationId).ToListAsync();
        await Assert.That(stored.Select(row => row.Name).Order(StringComparer.Ordinal)).IsEquivalentTo(names.Order(StringComparer.Ordinal));
        await Assert.That(await storage.AuditEvents.CountAsync(row => row.OrganizationId == organizationId && row.Action == "database.created")).IsEqualTo(2);
        var duplicate = new ProjectDatabaseRow
        {
            Id = AccountService.NewId(),
            OrganizationId = organizationId,
            ProjectId = stored[0].ProjectId,
            Name = stored[0].Name,
            Region = "GRU",
            Regions = "[]",
            AllRegions = false,
            PlacementRevision = 0,
            Endpoint = "",
            Status = "active",
            SpendCapCents = 0,
            ReadLimit = 0,
            WriteLimit = 0,
            ReplicationLimit = 0,
            BackupRetentionDays = 0,
            CreatedAt = DateTime.UtcNow
        };
        storage.ProjectDatabases.Add(duplicate);
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => storage.SaveChangesAsync());
        await Assert.That(refused!.InnerException).IsTypeOf<TerraBaseSqlException>();
        await Assert.That(((TerraBaseSqlException)refused.InnerException!).Code).IsEqualTo(TerraBaseSqlErrorCodes.UniqueViolation);
    }
}
