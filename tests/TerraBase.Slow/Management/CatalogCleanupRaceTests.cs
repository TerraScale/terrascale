using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class CatalogCleanupRaceTests
{
    [Test]
    public async Task Startup_preserves_a_registered_database_with_a_pending_shared_creation_intent()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-shared-create-intent-").FullName;
        var user = EntityIds.New().ToString();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            ManagedDatabaseRef database;
            TenantContext tenant;
            Ulid incarnation;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: time, userId: user))
            {
                database = await api.CreateManagedDatabaseAsync();
                tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
                var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
                incarnation = (await engine.OpenDatabaseAsync(tenant, token))!.Incarnation;
                var catalog = await engine.OpenSystemDatabaseAsync(token);
                await using var write = await catalog.BeginTransactionAsync(write: true, token);

                _ = await write.PutAsync("pending_database_cleanup", database.Id,
                    JsonSerializer.SerializeToUtf8Bytes(tenant, TerraBaseJsonContext.Default.TenantContext), token);
                _ = await write.CommitAsync(token);
            }

            await using var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: time, userId: user);
            var storage = restarted.Server.Services.GetRequiredService<IStorageEngine>();
            await Assert.That((await storage.OpenDatabaseAsync(tenant, token))!.Incarnation).IsEqualTo(incarnation);
            var system = await storage.OpenSystemDatabaseAsync(token);
            await using (var read = await system.BeginTransactionAsync(write: false, token))
            {
                await Assert.That(await read.GetAsync($"projects/{database.Project}/databases", database.Id, token)).IsNotNull();
                await Assert.That(await read.GetAsync("pending_database_cleanup", database.Id, token)).IsNotNull();
            }

            using var removed = await restarted.Client.DeleteAsync(ManagementApi.Uri(
                $"/v1/management/organizations/{database.Organization}/projects/{database.Project}/databases/{database.Id}"), token);
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(await storage.OpenDatabaseAsync(tenant, token)).IsNull();
            await using var final = await system.BeginTransactionAsync(write: false, token);
            await Assert.That(await final.GetAsync("pending_database_cleanup", database.Id, token)).IsNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

}
