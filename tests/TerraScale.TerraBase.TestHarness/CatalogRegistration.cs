using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Testing;

/// <summary>
/// Registers databases of chosen ids in a server's catalog: a replication receiver takes only databases its catalog registers.
/// </summary>
public static class CatalogRegistration
{
    /// <summary>Registers <paramref name="database"/>, or a new database of random ids, and returns it.</summary>
    public static async Task<TenantContext> RegisterAsync(this TerraBaseTestServer server, TenantContext? database = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        database ??= new TenantContext(Guid.NewGuid().ToString("D"), PlanTier.Hobby, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        var (organization, project, id) = (database.OrganizationId, database.ProjectId!, database.DatabaseId!);
        var created = DateTimeOffset.UnixEpoch;
        (string Collection, string Id, byte[] Json)[] rows =
        [
            ("catalog", "version", "\"5\""u8.ToArray()),
            ("organizations", organization, JsonSerializer.SerializeToUtf8Bytes(
                new Organization(organization, "registered", "test", created, database.Plan), TerraBaseJsonContext.Default.Organization)),
            ($"organizations/{organization}/projects", project, JsonSerializer.SerializeToUtf8Bytes(
                new Project(project, organization, "registered", created), TerraBaseJsonContext.Default.Project)),
            ($"projects/{project}/databases", id, JsonSerializer.SerializeToUtf8Bytes(
                new ManagedDatabase(id, project, id, null, created), TerraBaseJsonContext.Default.ManagedDatabase)),
        ];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(deadline.Token);
        await using var write = await store.BeginTransactionAsync(write: true, deadline.Token);
        foreach (var (collection, key, json) in rows)
        {
            await write.CreateCollectionAsync(new CollectionInfo(collection, 0), deadline.Token);
            await write.PutAsync(collection, key, json, deadline.Token);
        }

        await write.CommitAsync(deadline.Token);
        return database;
    }
}
