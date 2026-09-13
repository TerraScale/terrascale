using Microsoft.Data.Sqlite;
using TerraBase.Storage.Indexes;
using TerraBase.Storage.Schema;

namespace TerraBase.Tests;

/// <summary>Focused checks for durable index descriptor lifecycle state.</summary>
public static class SqliteIndexCatalogTests
{
    /// <summary>Verifies compare-and-set lifecycle transitions and reopen durability.</summary>
    public static async Task RunAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"terrabase-index-catalog-{Guid.NewGuid():N}.sqlite3");
        var descriptor = SqliteIndexDescriptor.Candidate(
            "index-users-email",
            "tenant-catalog",
            "users",
            "$.email",
            SqliteIndexValueType.Text);
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await SqliteSchema.EnsureCreatedAsync(connection);
                var catalog = new SqliteIndexCatalog(connection);
                await catalog.CreateAsync(descriptor);

                var created = await catalog.FindAsync(descriptor.DescriptorId);
                Assert(created is not null, "the index descriptor must be readable after creation");
                Assert(created!.State == SqliteIndexDescriptorState.Candidate, "new descriptors must start as candidates");
                Assert(!created.PlannerVisible && created.BackfillWatermark == 0, "new descriptors must start hidden at watermark zero");

                var listed = await catalog.ListAsync(descriptor.Tenant, descriptor.Collection);
                Assert(listed.Count == 1 && listed[0].DescriptorId == descriptor.DescriptorId, "collection listing must return the descriptor in stable order");

                await catalog.StartBuildAsync(descriptor.DescriptorId);
                await catalog.AdvanceBuildAsync(descriptor.DescriptorId, 7);
                await ExpectCatalogException(
                    () => catalog.AdvanceBuildAsync(descriptor.DescriptorId, 6),
                    "a backfill watermark must not move backwards");

                await catalog.ActivateAsync(descriptor.DescriptorId, 7);
                var active = await catalog.FindAsync(descriptor.DescriptorId);
                Assert(active is not null && active.State == SqliteIndexDescriptorState.Active && active.PlannerVisible, "activation must publish the completed descriptor");
                Assert(active is not null && active.BackfillWatermark == 7, "activation must retain the completed watermark");

                await catalog.HideAsync(descriptor.DescriptorId);
                await catalog.RetireAsync(descriptor.DescriptorId);
                var retired = await catalog.FindAsync(descriptor.DescriptorId);
                Assert(retired is not null && retired.State == SqliteIndexDescriptorState.Retired && !retired.PlannerVisible, "retirement must hide the descriptor permanently");
                await ExpectCatalogException(
                    () => catalog.StartBuildAsync(descriptor.DescriptorId),
                    "a retired descriptor must reject a second build");

                await ExpectCatalogException(
                    () => catalog.CreateAsync(descriptor),
                    "duplicate descriptor identities must be rejected");
            }

            await using (var reopenedConnection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await reopenedConnection.OpenAsync();
                await SqliteSchema.EnsureCreatedAsync(reopenedConnection);
                var reopened = await new SqliteIndexCatalog(reopenedConnection).FindAsync(descriptor.DescriptorId);
                Assert(reopened?.State == SqliteIndexDescriptorState.Retired, "index descriptor state must survive close and reopen");
                Assert(reopened is not null && reopened.BackfillWatermark == 7, "index descriptor watermark must survive close and reopen");
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete($"{path}-wal");
            File.Delete($"{path}-shm");
        }
    }

    private static async Task ExpectCatalogException(Func<Task> operation, string message)
    {
        try
        {
            await operation();
        }
        catch (SqliteIndexCatalogException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
