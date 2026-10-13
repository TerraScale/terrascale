using Contents = TerraBase.E2E.BackupQualification.Contents;
using Cut = TerraBase.E2E.BackupQualification.Cut;
using Outcome = TerraBase.E2E.BackupQualification.Outcome;

namespace TerraBase.E2E;

/// <summary>The A7YES qualification rules of <see cref="BackupQualification"/>: what makes a backup run green, and what never does.</summary>
public sealed class BackupQualificationTests
{
    private static readonly Contents Committed = Of(3, ("a", "v1"), ("b", "v2"), ("c", "v1"));
    private static readonly Contents Shipped = Of(2, ("a", "v1"), ("b", "v1"));
    private static readonly Contents Stalled = Of(1, ("a", "v1"));

    private static readonly Cut[] Plan =
    [
        new("after-commit", true, Committed),
        new("shipped-checkpoint", true, Shipped),
        new("during-stall", false, Stalled),
    ];

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task Supported_cuts_that_restore_exactly_and_a_typed_refusal_of_a_fault_cut_are_green()
    {
        var failures = BackupQualification.Failures(Plan, [new("after-commit", Committed, null), new("shipped-checkpoint", Shipped, null), new("during-stall", null, "backup_incomplete")]);
        await Assert.That(failures).IsEmpty();
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_fault_cut_that_restores_exactly_what_was_acknowledged_is_green()
    {
        var failures = BackupQualification.Failures(Plan, [new("after-commit", Committed, null), new("shipped-checkpoint", Shipped, null), new("during-stall", Stalled, null)]);
        await Assert.That(failures).IsEmpty();
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task Every_cut_refused_is_not_green()
    {
        var failures = BackupQualification.Failures(Plan, [new("after-commit", null, "backup_incomplete"), new("shipped-checkpoint", null, "backup_incomplete"), new("during-stall", null, "backup_incomplete")]);
        await Assert.That(failures.Count(failure => failure.Contains("a supported cut must restore", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_plan_without_a_supported_cut_is_not_green_even_when_every_refusal_is_typed()
    {
        var failures = BackupQualification.Failures([new("during-stall", false, Stalled)], [new("during-stall", null, "backup_incomplete")]);
        await Assert.That(failures).Contains("the plan has no supported cut: refusals alone prove safety, never a working restore");
    }

    // Each restore differs from what was acknowledged in one way; each is a failure of the cut, supported or not.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments("after-commit", "count")]
    [Arguments("after-commit", "digest")]
    [Arguments("after-commit", "version")]
    [Arguments("after-commit", "extra")]
    [Arguments("after-commit", "source")]
    [Arguments("after-commit", "regions")]
    [Arguments("during-stall", "count")]
    [Arguments("during-stall", "version")]
    public async Task A_restore_that_differs_from_the_acknowledged_writes_fails_its_cut(string name, string change)
    {
        var acknowledged = Plan.Single(cut => cut.Name == name).Acknowledged;
        var wrong = change switch
        {
            "count" => acknowledged with { Count = acknowledged.Count - 1 },
            "digest" => acknowledged with { Digest = "other" },
            "version" => acknowledged with { Versions = acknowledged.Versions.ToDictionary(pair => pair.Key, pair => pair.Key == "a" ? "v0" : pair.Value) },
            "extra" => acknowledged with { Versions = acknowledged.Versions.Append(new("z", "v1")).ToDictionary() },
            "source" => acknowledged with { BranchedFrom = "another-database" },
            _ => acknowledged with { Regions = ["gru", "sjc"] },
        };
        Outcome[] outcomes = [.. Plan.Select(cut => cut.Name == name ? new Outcome(name, wrong, null) : new Outcome(cut.Name, cut.Acknowledged, null))];
        var failures = BackupQualification.Failures(Plan, outcomes);
        await Assert.That(failures).HasSingleItem();
        await Assert.That(failures[0]).StartsWith($"cut {name} restored a different database");
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_missing_untyped_unplanned_or_repeated_outcome_is_not_green()
    {
        var failures = BackupQualification.Failures(
            [.. Plan, new("after-commit", true, Committed)],
            [new("after-commit", Committed, null), new("after-commit", Committed, null), new("during-stall", null, " "), new("added-later", Committed, null)]);
        await Assert.That(failures).Contains("cut after-commit is planned twice");
        await Assert.That(failures).Contains("cut added-later was not in the frozen plan");
        await Assert.That(failures).Contains("cut after-commit has 2 outcomes, not one");
        await Assert.That(failures).Contains("cut shipped-checkpoint has 0 outcomes, not one");
        await Assert.That(failures).Contains("cut during-stall neither restored nor answered a typed refusal");
    }

    private static Contents Of(int count, params (string Id, string Version)[] versions) =>
        new(count, $"digest-{count}", versions.ToDictionary(pair => pair.Id, pair => pair.Version), "source-database", ["gru", "sjc", "fra"]);
}
