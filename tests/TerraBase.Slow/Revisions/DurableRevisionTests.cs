using TerraBase.Engine.Revisions;
using TerraBase.Testing;

namespace TerraBase.Slow.Revisions;

public sealed class DurableRevisionTests
{
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
                // Several pages and separate commits cover incremental catch-up;
                // bulk WAL/checkpoint stress belongs to the Slow revision tests.
                await database.InsertAsync(first, 25);
                before = await replica.PullAsync(first.Server, database.Database);
                await database.InsertAsync(first, 50);
            }

            await using var second = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory);
            await database.InsertAsync(second, 1);
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
