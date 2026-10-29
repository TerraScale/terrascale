using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Engine.Turso;
using TerraBase.Testing;

namespace TerraBase.Fast.Revisions;

public sealed class DurableRevisionTests
{
    [Test]
    public async Task IncrementalPullsStayExactAcrossPolicyCheckpoints()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = new RevisionDatabase();
        await database.CreateAsync(api);
        await database.InsertAsync(api, 100);
        var replica = new RevisionReplica();
        var bootstrap = await replica.PullAsync(api.Server, database.Database);
        await Assert.That(bootstrap.Full).IsEqualTo(FullReason.Bootstrap);

        await database.InsertAsync(api, 3_000);
        await database.UpdateAsync(api, 200);
        await database.DeleteAsync(api, 100);
        var pull = await replica.PullAsync(api.Server, database.Database);

        await Assert.That(pull.Full).IsNull();
        await Assert.That(pull.Revision.Incarnation).IsEqualTo(bootstrap.Revision.Incarnation);
        await Assert.That(pull.Revision.Generation).IsGreaterThan(bootstrap.Revision.Generation);
        await AssertConvergedAsync(api, database, replica, pull);
    }

    [Test]
    public async Task IncrementalPullsSurviveAGracefulRestart()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-revisions-").FullName;
        try
        {
            var database = new RevisionDatabase();
            var replica = new RevisionReplica();
            RevisionPullResult before;
            await using (var first = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                await database.CreateAsync(first);
                await database.InsertAsync(first, 300);
                before = await replica.PullAsync(first.Server, database.Database);
                await database.InsertAsync(first, 1_200);
            }

            await using var second = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory);
            await database.InsertAsync(second, 50);
            var pull = await replica.PullAsync(second.Server, database.Database);

            await Assert.That(pull.Full).IsNull();
            await Assert.That(pull.Revision.Incarnation).IsEqualTo(before.Revision.Incarnation);
            await AssertConvergedAsync(second, database, replica, pull);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertConvergedAsync(ManagementApi api, RevisionDatabase database, RevisionReplica replica, RevisionPullResult pull)
    {
        var fresh = new RevisionReplica();
        var bootstrap = await fresh.PullAsync(api.Server, database.Database);
        await Assert.That(bootstrap.Revision).IsEqualTo(pull.Revision);
        await Assert.That(replica.SameAs(fresh)).IsTrue();
        await Assert.That(database.Differences(replica)).IsEqualTo(0);
        await Assert.That(await database.LostAckedWritesAsync(api)).IsEqualTo(0);
    }

}
