namespace TerraBase.E2E;

/// <summary>The flyctl sequences the Fly fault tests run, against a fake flyctl that records each call.</summary>
public sealed class FlyctlTests
{
    // A live run (#173) started a machine while it was still stopping, and Fly refused the start: the start now waits for
    // stopped first, bounded, and fails rather than starting when the wait fails.
    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_stopped_machine_is_started_only_after_fly_reports_it_stopped()
    {
        List<string> calls = [];
        await Flyctl.StartStoppedAsync("tsb-e2e", "m-sjc", arguments =>
        {
            calls.Add(string.Join(' ', arguments));
            return Task.FromResult("");
        });

        await Assert.That(string.Join(" | ", calls))
            .IsEqualTo("machine wait m-sjc --app tsb-e2e --state stopped --wait-timeout 2m | machine start m-sjc --app tsb-e2e");
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_machine_that_never_reaches_stopped_is_not_started()
    {
        List<string> calls = [];
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Flyctl.StartStoppedAsync("tsb-e2e", "m-sjc", arguments =>
        {
            calls.Add(arguments[1]);
            return arguments[1] == "wait" ? throw new InvalidOperationException("flyctl machine wait exited 1: timeout") : Task.FromResult("");
        }));

        await Assert.That(error!.Message).Contains("timeout");
        await Assert.That(calls).IsEquivalentTo(["wait"]);
    }
}
