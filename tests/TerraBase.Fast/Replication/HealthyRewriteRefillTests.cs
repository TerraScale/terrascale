using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Storage;
using TerraBase.Primitives;
using TerraBase.Testing;
using ReplicaCluster = TerraBase.Fast.Replication.PeerStreamTests.Cluster;

namespace TerraBase.Fast.Replication;

/// <summary>
/// TS-873: a healthy rewrite gives the file a new incarnation. A peer offline across it holds stale acknowledgements and the
/// tombstone of a deleted document; once back it catches up from the new file without resurrecting the deleted document or
/// losing a survivor, and the GC floor stays.
/// </summary>
public sealed class HealthyRewriteRefillTests
{
    [Test]
    public async Task An_offline_peer_catches_up_after_a_healthy_rewrite_without_resurrection_or_loss()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await ReplicaCluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var (kept, gone, offline, after) = (Id(), Id(), Id(), Id());
        await PutAsync(gru, kept, """{"n":1}""");
        await PutAsync(gru, gone, """{"secret":"erased"}""");
        await Assert.That(await sjc.UntilAsync(gone, """{"secret":"erased"}""")).IsTrue();
        await gru.WriteAsync(async transaction => _ = await transaction.DeleteAsync("items", gone, CancellationToken.None));
        await Assert.That(await sjc.UntilAbsentAsync(gone)).IsTrue();
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, await gru.HeadAsync(cluster.Database))).IsTrue();
        await Assert.That(await gru.AckedAsync(cluster.Database, sjc.Node)).IsGreaterThan(0);
        // shortcut: a synthetic floor below every stamp proves the floor is carried, not a real certified GC across the rewrite.
        await StorageFixture.ExecuteWhileWriterHeldAsync(gru.Api.Server, cluster.Database, "UPDATE _tb_gc_floor SET stamp = 1;");

        var peer = sjc.Node;
        await sjc.StopAsync();
        await PutAsync(gru, offline, """{"n":2}""");
        var engine = gru.Api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var source = await gru.IncarnationAsync();
        var rewrite = await engine.RewriteAsync(cluster.Database, 0, token);
        await Assert.That(rewrite.SourceIncarnation).IsEqualTo(source);
        await Assert.That(await gru.IncarnationAsync()).IsEqualTo(rewrite.TargetIncarnation);
        // sjc's acknowledgements named the old file's change ids: none counts for the new one.
        await Assert.That(await gru.AckedAsync(cluster.Database, peer)).IsEqualTo(0);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, token))!;
        await Assert.That(Scalar(store.FilePath, "SELECT stamp FROM _tb_gc_floor;")).IsEqualTo("1");
        await PutAsync(gru, after, """{"n":3}""");

        await sjc.StartAsync();
        await Assert.That(await sjc.UntilAsync(offline, """{"n":2}""")).IsTrue();
        await Assert.That(await sjc.UntilAsync(after, """{"n":3}""")).IsTrue();
        await Assert.That(await sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await sjc.ReadAsync(gone)).IsNull();
        await Assert.That(await gru.UntilAckedAsync(cluster.Database, sjc.Node, await gru.HeadAsync(cluster.Database))).IsTrue();

        // And back: sjc's write reaches the new file, and nothing sjc held brings the deleted document back.
        var back = Id();
        await PutAsync(sjc, back, """{"n":4}""");
        await Assert.That(await gru.UntilAsync(back, """{"n":4}""")).IsTrue();
        await Assert.That(await gru.ReadAsync(gone)).IsNull();
        await Assert.That(await gru.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await gru.ReadAsync(offline)).IsEqualTo("""{"n":2}""");
    }

    [Test]
    public async Task Peer_applies_during_the_snapshot_survive_the_rewrite()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await ReplicaCluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var id = Id();
        await PutAsync(gru, id, """{"n":1}""");
        await Assert.That(await sjc.UntilAsync(id, """{"n":1}""")).IsTrue();
        var engine = gru.Api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(cluster.Database, token))!;
        var pinned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        store.RewriteSnapshotPinned = () =>
        {
            pinned.TrySetResult();
            release.Wait(token);
        };
        var rewrite = engine.RewriteAsync(cluster.Database, 0, token).AsTask();
        try
        {
            await pinned.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await PutAsync(sjc, id, """{"n":2}""");
            await Assert.That(await gru.UntilAsync(id, """{"n":2}""")).IsTrue();
        }
        finally
        {
            release.Set();
        }
        _ = await rewrite;
        await Assert.That(await gru.ReadAsync(id)).IsEqualTo("""{"n":2}""");
    }

    private static string Id() => EntityIds.New().ToString();

    private static Task PutAsync(PeerStreamTests.Machine machine, string id, string json) =>
        machine.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
            _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes(json), CancellationToken.None);
        });

    private static string? Scalar(string path, string sql)
    {
        using var connection = new Turso.TursoConnection(TestMasterKey.ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
