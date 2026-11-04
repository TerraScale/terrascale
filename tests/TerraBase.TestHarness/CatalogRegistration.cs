using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Testing;

public static class CatalogRegistration
{
    public static async Task<TenantContext> RegisterAsync(this TerraBaseTestServer server, TenantContext? database = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        database ??= new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        var (organization, project, id) = (database.OrganizationId, database.ProjectId!, database.DatabaseId!);
        var created = DateTimeOffset.UnixEpoch;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        if (await server.Services.GetRequiredService<EngineManagementService>().EnsureCatalogAsync(deadline.Token) is not Done)
        {
            throw new InvalidOperationException("the catalog is unavailable");
        }

        var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(deadline.Token);
        await using var write = await store.BeginTransactionAsync(write: true, deadline.Token);
        _ = await CatalogTables.PutOrganizationAsync(write, new Organization(organization, "registered", EntityIds.New().ToString(), created, database.Plan), deadline.Token);
        _ = await CatalogTables.PutProjectAsync(write, new Project(project, organization, "registered", created), deadline.Token);
        await CatalogTables.PutDatabaseAsync(write, new ManagedDatabase(id, project, id, null, created), deadline.Token);
        await write.CommitAsync(deadline.Token);
        return database;
    }
}
