using Microsoft.Data.Sqlite;
using TerraBase.Storage.Indexes;
using TerraBase.Storage.Schema;

namespace TerraBase.Api.Tests;

/// <summary>Focused checks for durable index descriptor lifecycle state.</summary>
public sealed class SqliteIndexCatalogTests
{
    /// <summary>Verifies compare-and-set lifecycle transitions and reopen durability.</summary>
    [Test]
    public async Task DurableLifecycleTransitionsSurviveReopen()
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
                await Assert.That(created).IsNotNull();
                await Assert.That(created!.State).IsEqualTo(SqliteIndexDescriptorState.Candidate);
                await Assert.That(created.PlannerVisible).IsFalse();
                await Assert.That(created.BackfillWatermark).IsEqualTo(0L);

                var listed = await catalog.ListAsync(descriptor.Tenant, descriptor.Collection);
                await Assert.That(listed).Count().IsEqualTo(1);
                await Assert.That(listed[0].DescriptorId).IsEqualTo(descriptor.DescriptorId);

                await catalog.StartBuildAsync(descriptor.DescriptorId);
                await catalog.AdvanceBuildAsync(descriptor.DescriptorId, 7);
                await Assert.That(() => catalog.AdvanceBuildAsync(descriptor.DescriptorId, 6))
                    .Throws<SqliteIndexCatalogException>();

                await Assert.That(() => catalog.ActivateAsync(descriptor.DescriptorId, 6))
                    .Throws<SqliteIndexCatalogException>();
                await catalog.ActivateAsync(descriptor.DescriptorId, 7);
                var active = await catalog.FindAsync(descriptor.DescriptorId);
                await Assert.That(active).IsNotNull();
                await Assert.That(active!.State).IsEqualTo(SqliteIndexDescriptorState.Active);
                await Assert.That(active.PlannerVisible).IsTrue();
                await Assert.That(active.BackfillWatermark).IsEqualTo(7L);

                await catalog.HideAsync(descriptor.DescriptorId);
                await catalog.RetireAsync(descriptor.DescriptorId);
                var retired = await catalog.FindAsync(descriptor.DescriptorId);
                await Assert.That(retired).IsNotNull();
                await Assert.That(retired!.State).IsEqualTo(SqliteIndexDescriptorState.Retired);
                await Assert.That(retired.PlannerVisible).IsFalse();
                await Assert.That(() => catalog.StartBuildAsync(descriptor.DescriptorId))
                    .Throws<SqliteIndexCatalogException>();
                await Assert.That(() => catalog.CreateAsync(descriptor))
                    .Throws<SqliteIndexCatalogException>();
            }

            await using (var reopenedConnection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await reopenedConnection.OpenAsync();
                await SqliteSchema.EnsureCreatedAsync(reopenedConnection);
                var reopened = await new SqliteIndexCatalog(reopenedConnection).FindAsync(descriptor.DescriptorId);
                await Assert.That(reopened?.State).IsEqualTo(SqliteIndexDescriptorState.Retired);
                await Assert.That(reopened?.BackfillWatermark).IsEqualTo(7L);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, $"{path}-wal", $"{path}-shm" })
        {
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // The test connection has already disposed its handles. A
                // concurrent SQLite checkpoint may still have a sidecar open.
            }
        }
    }
}
