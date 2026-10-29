using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBase.E2E;

public sealed class BackupQualificationE2ETests
{
    [Test]
    [Category("local-only")]
    [Scenario(Nodes.InProcess)]
    public async Task Acknowledged_local_writes_survive_snapshot_and_point_in_time_restores()
    {
        var run = await BackupQualificationRun.ExecuteAsync();
        var failures = BackupQualification.Failures(run.Plan, run.Outcomes);
        Outcomes.Mark(failures.Count == 0 ? "pass" : "fail", JsonSerializer.Serialize(new { backup = run, failures }));
        await Assert.That(failures).IsEmpty();
        var fault = run.Plan.Single(cut => !cut.Supported);
        foreach (var (code, status) in new (string? Code, int Status)[]
        {
            ("restore_point_unproven", 409), ("backup_unreadable", 409), ("backup_history_incomplete", 400), ("invalid_request", 400), (null, 409),
        })
        {
            var changed = run.Outcomes.Select(outcome => outcome.Cut == fault.Name ? outcome with { Refusal = code, Status = status } : outcome).ToArray();
            await Assert.That(BackupQualification.Failures(run.Plan, changed)).Contains($"cut {fault.Name} neither restored nor answered a typed refusal");
        }

        var wrongPlan = run.Plan.Select(cut => cut.Name == fault.Name ? cut with { ExpectedRefusal = "invalid_request", ExpectedStatus = 400 } : cut).ToArray();
        await Assert.That(BackupQualification.Failures(wrongPlan, run.Outcomes)).Contains($"cut {fault.Name} neither restored nor answered a typed refusal");
    }

    [Test]
    [Category("local-only")]
    [Scenario(Nodes.InProcess)]
    public async Task Rewriting_the_same_restored_body_fails_version_qualification()
    {
        var run = await BackupQualificationRun.ExecuteAsync(async (cluster, holder, path) =>
        {
            var documents = await BackupQualificationRun.ListAsync(holder, path, cluster.Collection, TestContext.Current!.Execution.CancellationToken);
            var first = documents.First();
            await Docs.PutAsync(holder, new Uri($"{path}/collections/{cluster.Collection}/documents/by-key?id={first.Key}", UriKind.Relative), JsonNode.Parse(first.Value.Body)!);
        });
        var failures = BackupQualification.Failures(run.Plan, run.Outcomes);
        Outcomes.Mark("harness", JsonSerializer.Serialize(new
        {
            control = "real single-node backup restores deliberately rewritten; judge must reject changed stored versions",
            failures,
        }));
        await Assert.That(failures.Count).IsEqualTo(3);
        await Assert.That(failures.All(failure => failure.Contains("versions differ", StringComparison.Ordinal))).IsTrue();
    }
}
