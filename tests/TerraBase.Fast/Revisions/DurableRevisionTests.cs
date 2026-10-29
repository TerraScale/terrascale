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
    public async Task PullsDuringConcurrentWritesConverge()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = new RevisionDatabase();
        await database.CreateAsync(api);
        var replica = new RevisionReplica();
        await replica.PullAsync(api.Server, database.Database);

        var writes = database.InsertAsync(api, 3_000);
        while (!writes.IsCompleted)
        {
            var pull = await replica.PullAsync(api.Server, database.Database);
            await Assert.That(pull.Full).IsNull();
        }

        await writes;
        await AssertConvergedAsync(api, database, replica, await replica.PullAsync(api.Server, database.Database));
    }

    [Test]
    public async Task AnUncoordinatedCheckpointThenOneInsertIsNeverAnEmptyPull()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = new RevisionDatabase();
        await database.CreateAsync(api);
        await database.InsertAsync(api, 100);
        var replica = new RevisionReplica();
        await replica.PullAsync(api.Server, database.Database);

        var store = (TursoDatabaseStore?)await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database.Database, CancellationToken.None);
        await store!.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            return command.ExecuteNonQuery();
        }, "an uncoordinated checkpoint", CancellationToken.None);
        await database.InsertAsync(api, 1);
        var pull = await replica.PullAsync(api.Server, database.Database);

        await Assert.That(pull.Full).IsEqualTo(FullReason.Rewritten);
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

    [Test]
    public async Task IncrementalPullsSurviveACrashImage()
    {
        var crashed = Directory.CreateTempSubdirectory("terrabase-revisions-crash-").FullName;
        try
        {
            var database = new RevisionDatabase();
            var replica = new RevisionReplica();
            RevisionPullResult before;
            await using (var first = await ManagementApi.StartAsync())
            {
                await database.CreateAsync(first);
                await database.InsertAsync(first, 300);
                before = await replica.PullAsync(first.Server, database.Database);
                await database.InsertAsync(first, 500);
                CopyDirectory(first.Server.DataDirectory, crashed);
            }

            await using var second = await ManagementApi.StartAsync(settings => settings["data-directory"] = crashed);
            var pull = await replica.PullAsync(second.Server, database.Database);

            await Assert.That(pull.Full).IsNull();
            await Assert.That(pull.Revision.Incarnation).IsEqualTo(before.Revision.Incarnation);
            await AssertConvergedAsync(second, database, replica, pull);
        }
        finally
        {
            Directory.Delete(crashed, recursive: true);
        }
    }

    [Test]
    public async Task ARewriteResendsEveryPage()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = new RevisionDatabase();
        await database.CreateAsync(api);
        await database.InsertAsync(api, 400);
        var replica = new RevisionReplica();
        await replica.PullAsync(api.Server, database.Database);
        await database.DeleteAsync(api, 150);

        await api.Server.Services.GetRequiredService<IHealthyRewrite>().RewriteAsync(database.Database, 0, CancellationToken.None);
        await database.InsertAsync(api, 10);
        var pull = await replica.PullAsync(api.Server, database.Database);

        await Assert.That(pull.Full).IsEqualTo(FullReason.Rewritten);
        await AssertConvergedAsync(api, database, replica, pull);
    }

    [Test]
    public async Task ARestoreOrFailoverStartsANewIncarnationAndForcesARebootstrap()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = new RevisionDatabase();
        await database.CreateAsync(api);
        await database.InsertAsync(api, 300);
        var replica = new RevisionReplica();
        var before = await replica.PullAsync(api.Server, database.Database);

        await Assert.That(await api.Server.Services.GetRequiredService<IDatabaseRevisions>().StartIncarnationAsync(database.Database, CancellationToken.None)).IsTrue();
        await database.InsertAsync(api, 20);
        var pull = await replica.PullAsync(api.Server, database.Database);

        await Assert.That(pull.Full).IsEqualTo(FullReason.OtherIncarnation);
        await Assert.That(pull.Revision.Incarnation).IsNotEqualTo(before.Revision.Incarnation);
        await AssertConvergedAsync(api, database, replica, pull);
        var next = await replica.PullAsync(api.Server, database.Database);
        await Assert.That(next.Full).IsNull();
        await Assert.That(next.Pages).IsEqualTo(0);
    }

    private static async Task AssertConvergedAsync(ManagementApi api, RevisionDatabase database, RevisionReplica replica, RevisionPullResult pull)
    {
        var fresh = new RevisionReplica();
        var bootstrap = await fresh.PullAsync(api.Server, database.Database);
        await Assert.That(bootstrap.Revision).IsEqualTo(pull.Revision);
        await Assert.That(replica.SameAs(fresh)).IsTrue();
        await Assert.That(database.Missing(replica)).IsEqualTo(0);
        await Assert.That(await database.LostAckedWritesAsync(api)).IsEqualTo(0);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(file => Path.GetFileName(file) != ".terrabase.lock"))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        File.WriteAllBytes(Path.Combine(to, ".terrabase.lock"), Encoding.ASCII.GetBytes(StorageFormat.Id));
    }
}
