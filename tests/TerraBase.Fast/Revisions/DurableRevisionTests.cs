using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Testing;

namespace TerraBase.Fast.Revisions;

public sealed class DurableRevisionTests
{
    [Test]
    public async Task IncrementalPullsStayExactAcrossPolicyCheckpoints()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var database = new RevisionDatabase();
        await database.CreateAsync(server);
        await database.InsertAsync(server, 100);
        var replica = new RevisionReplica();
        var bootstrap = await replica.PullAsync(server, database.Database);
        await Assert.That(bootstrap.Full).IsEqualTo(FullReason.Bootstrap);

        await database.InsertAsync(server, 4_000);
        await database.UpdateAsync(server, 200);
        await database.DeleteAsync(server, 100);
        var pull = await replica.PullAsync(server, database.Database);

        await Assert.That(pull.Full).IsNull();
        await Assert.That(pull.Revision.Incarnation).IsEqualTo(bootstrap.Revision.Incarnation);
        await Assert.That(pull.Revision.Generation).IsGreaterThan(bootstrap.Revision.Generation);
        await AssertConvergedAsync(server, database, replica, pull);
    }

    [Test]
    public async Task ACheckpointThenOneInsertIsNeverAnEmptyPull()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var database = new RevisionDatabase();
        await database.CreateAsync(server);
        await database.InsertAsync(server, 100);
        var replica = new RevisionReplica();
        await replica.PullAsync(server, database.Database);

        await database.ExecuteAsync(server, "PRAGMA wal_checkpoint(TRUNCATE)");
        await database.InsertAsync(server, 1);
        var pull = await replica.PullAsync(server, database.Database);

        await Assert.That(pull.Pages).IsGreaterThan(0);
        await AssertConvergedAsync(server, database, replica, pull);
    }

    [Test]
    public async Task IncrementalPullsSurviveARestart()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-revisions-").FullName;
        try
        {
            var database = new RevisionDatabase();
            var replica = new RevisionReplica();
            RevisionPullResult before;
            await using (var first = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory))
            {
                await database.CreateAsync(first);
                await database.InsertAsync(first, 300);
                before = await replica.PullAsync(first, database.Database);
                await database.InsertAsync(first, 1_200);
            }

            await using var second = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
            await database.InsertAsync(second, 50);
            var pull = await replica.PullAsync(second, database.Database);

            await Assert.That(pull.Full).IsNull();
            await Assert.That(pull.Revision.Incarnation).IsEqualTo(before.Revision.Incarnation);
            await AssertConvergedAsync(second, database, replica, pull);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ARewriteResendsEveryPage()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var database = new RevisionDatabase();
        await database.CreateAsync(server);
        await database.InsertAsync(server, 400);
        var replica = new RevisionReplica();
        await replica.PullAsync(server, database.Database);
        await database.DeleteAsync(server, 150);

        await server.Services.GetRequiredService<IHealthyRewrite>().RewriteAsync(database.Database, 0, CancellationToken.None);
        await database.InsertAsync(server, 10);
        var pull = await replica.PullAsync(server, database.Database);

        await Assert.That(pull.Full).IsEqualTo(FullReason.Rewritten);
        await AssertConvergedAsync(server, database, replica, pull);
    }

    [Test]
    public async Task ARestoreOrFailoverStartsANewIncarnationAndForcesARebootstrap()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var database = new RevisionDatabase();
        await database.CreateAsync(server);
        await database.InsertAsync(server, 300);
        var replica = new RevisionReplica();
        var before = await replica.PullAsync(server, database.Database);

        await Assert.That(await server.Services.GetRequiredService<IDatabaseRevisions>().StartIncarnationAsync(database.Database, CancellationToken.None)).IsTrue();
        await database.InsertAsync(server, 20);
        var pull = await replica.PullAsync(server, database.Database);

        await Assert.That(pull.Full).IsEqualTo(FullReason.OtherIncarnation);
        await Assert.That(pull.Revision.Incarnation).IsNotEqualTo(before.Revision.Incarnation);
        await AssertConvergedAsync(server, database, replica, pull);
        var next = await replica.PullAsync(server, database.Database);
        await Assert.That(next.Full).IsNull();
        await Assert.That(next.Pages).IsEqualTo(0);
    }

    private static async Task AssertConvergedAsync(TerraBaseTestServer server, RevisionDatabase database, RevisionReplica replica, RevisionPullResult pull)
    {
        var fresh = new RevisionReplica();
        var bootstrap = await fresh.PullAsync(server, database.Database);
        await Assert.That(bootstrap.Revision).IsEqualTo(pull.Revision);
        await Assert.That(replica.SameAs(fresh)).IsTrue();
        await Assert.That(database.Missing(replica)).IsEqualTo(0);
        await Assert.That(await database.LostAckedWritesAsync(server)).IsEqualTo(0);
    }
}
