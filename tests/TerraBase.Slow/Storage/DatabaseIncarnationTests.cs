using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

/// <summary>A deletion of one incarnation of a database file never removes the file that replaced it.</summary>
public sealed class DatabaseIncarnationTests
{
    [Test]
    public async Task Deleting_an_expected_incarnation_never_deletes_one_that_replaced_it()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        var file = Path.Combine(server.DataDirectory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");
        await Assert.That(await engine.CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
        var stale = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!.Incarnation;
        await Assert.That(await engine.DeleteDatabaseAsync(database, CancellationToken.None)).IsTrue();
        await Assert.That(await engine.CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
        var replacement = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!.Incarnation;
        await Assert.That(replacement).IsNotEqualTo(stale);

        await Assert.That(await engine.DeleteDatabaseAsync(database, CancellationToken.None, incarnation: stale)).IsFalse();
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That(await engine.DeleteDatabaseAsync(database, CancellationToken.None, incarnation: replacement)).IsTrue();
        await Assert.That(File.Exists(file)).IsFalse();
    }
}
