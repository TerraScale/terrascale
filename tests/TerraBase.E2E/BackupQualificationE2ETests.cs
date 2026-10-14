using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBase.E2E;

public sealed class BackupQualificationE2ETests
{
    // Creates and restores whole databases: the required local stage runs it, and on Fly only a pinned run of the approved
    // disposable app does (Cluster.StartBackupAsync refuses any other target); the deploy run excludes the local-only category.
    [Test]
    [Category("local-only")]
    [Scenario(Nodes.InProcess)]
    public async Task Acknowledged_three_region_writes_survive_snapshot_and_point_in_time_restores()
    {
        var run = await BackupQualificationRun.ExecuteAsync();
        var failures = BackupQualification.Failures(run.Plan, run.Outcomes);
        Outcomes.Mark(failures.Count == 0 ? "pass" : "fail", JsonSerializer.Serialize(new { backup = run, failures }));
        await Assert.That(failures).IsEmpty();
    }

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Rewriting_the_same_restored_body_fails_version_qualification()
    {
        Skip.When(Cluster.FlyRequested, "local integration regression deliberately changes this test's restored databases");
        var run = await BackupQualificationRun.ExecuteAsync(async (cluster, path) =>
        {
            var documents = await BackupQualificationRun.ListAsync(cluster[Cluster.Gru], path, cluster.Collection, TestContext.Current!.Execution.CancellationToken);
            var first = documents.First();
            await Docs.PutAsync(cluster[Cluster.Gru], new Uri($"{path}/collections/{cluster.Collection}/documents/by-key?id={first.Key}", UriKind.Relative), JsonNode.Parse(first.Value.Body)!);
        });
        var failures = BackupQualification.Failures(run.Plan, run.Outcomes);
        Outcomes.Mark("harness", "real three-node backup restores deliberately rewritten; judge must reject changed stored versions");
        await Assert.That(failures.Count).IsEqualTo(3);
        await Assert.That(failures.All(failure => failure.Contains("versions differ", StringComparison.Ordinal))).IsTrue();
    }
}
