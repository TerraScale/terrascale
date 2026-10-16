using System.Diagnostics;
using System.Text.Json;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardRootFixture>]
public sealed class FlyRolloutJourneyTests(DashboardRootFixture fixture)
{
    private const string Sha = "1234567890123456789012345678901234567890";

    [Test]
    [Arguments("slow-failed", true)]
    [Arguments("slow-running", true)]
    [Arguments("slow-absent", true)]
    [Arguments("ci-failed", false)]
    [Arguments("newer-ci-failed", false)]
    [Arguments("stale-main", false)]
    [Arguments("pull-request", false)]
    [Arguments("slow-trigger", false)]
    [Arguments("already-deployed", false)]
    public async Task OnlySuccessfulCurrentMainCiReleasesDeployment(string scenario, bool deploy)
    {
        var workflow = await File.ReadAllTextAsync(Path.Combine(fixture.Root, ".github/workflows/deploy.yml"));
        await Assert.That(workflow).Contains("workflows: [CI]");
        var script = workflow.Split("script: |\n", 2)[1].Split("\n  rollout:", 2)[0];
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--input-type=module");
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add("""
            const scenario = SCENARIO;
            const sha = 'tested-head';
            const ci = {id: 1, name: 'CI', event: 'push', head_branch: 'main', head_sha: sha, conclusion: 'success'};
            const run = {...ci};
            if (scenario === 'ci-failed') run.conclusion = ci.conclusion = 'failure';
            if (scenario === 'pull-request') run.event = 'pull_request';
            if (scenario === 'slow-trigger') run.name = 'Slow';
            const runs = [ci];
            if (scenario === 'slow-failed') runs.push({id: 2, name: 'Slow', event: 'push', conclusion: 'failure'});
            if (scenario === 'slow-running') runs.push({id: 2, name: 'Slow', event: 'push', conclusion: null});
            if (scenario === 'newer-ci-failed') runs.push({...ci, id: 3, conclusion: 'failure'});
            if (scenario === 'already-deployed') runs.push({id: 4, name: 'Deploy', conclusion: 'success'});
            const context = {payload: {workflow_run: run}, repo: {owner: 'TerraScale', repo: 'ts-dashboard'}};
            let output = null;
            const core = {setOutput: (name, value) => { if (name === 'sha') output = value; }};
            const github = {rest: {
              repos: {getBranch: async () => ({data: {commit: {sha: scenario === 'stale-main' ? 'new-main' : sha}}})},
              actions: {
                listWorkflowRunsForRepo: async () => ({data: {workflow_runs: runs}}),
                listJobsForWorkflowRun: async () => ({data: {jobs: [{name: 'rollout', conclusion: 'success'}]}})
              }
            }};
            await (async () => { SCRIPT })();
            process.stdout.write(output === sha ? 'deploy' : 'hold');
            """.Replace("SCENARIO", JsonSerializer.Serialize(scenario), StringComparison.Ordinal).Replace("SCRIPT", script, StringComparison.Ordinal));
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(process.ExitCode).IsEqualTo(0).Because(await error);
        await Assert.That(await output).IsEqualTo(deploy ? "deploy" : "hold");
    }

    [Test]
    public async Task OneImageRollsOutInOrderAndRecordsAllThreeRegionalTimes()
    {
        var result = await RunAsync("ok");
        await Assert.That(result.Exit).IsEqualTo(0);
        var deployments = result.Calls.Split('\n').Where(line => line.StartsWith("deploy ", StringComparison.Ordinal)).ToArray();
        await Assert.That(deployments.Length).IsEqualTo(3);
        foreach (var (region, index) in new[] { ("gru", 0), ("sjc", 1), ("fra", 2) })
        {
            await Assert.That(deployments[index]).Contains("--regions " + region);
            await Assert.That(deployments[index]).Contains("--image registry.fly.io/ts-dashboard:" + Sha);
            await Assert.That(result.Output).Contains("MERGE_TO_LIVE region=" + region);
        }
        await Assert.That(result.Calls.Contains("--build", StringComparison.Ordinal)).IsFalse();
        await Assert.That(result.Output).Contains("one running machine each in gru, sjc and fra");
    }

    [Test]
    [Arguments("wrong-region")]
    [Arguments("wrong-sha")]
    [Arguments("unready")]
    [Arguments("http-error")]
    public async Task AnUnprovenGruResponseStopsBeforeProvisioningAnotherRegion(string failure)
    {
        var result = await RunAsync(failure);
        await Assert.That(result.Exit).IsNotEqualTo(0);
        await Assert.That(result.Calls.Contains("scale ", StringComparison.Ordinal)).IsFalse();
        await Assert.That(result.Output.Contains("MERGE_TO_LIVE", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task AnExtraMachineCannotBeReportedAsTheApprovedThreeMachineFleet()
    {
        var result = await RunAsync("extra-machine");
        await Assert.That(result.Exit).IsNotEqualTo(0);
        await Assert.That(result.Error).Contains("Expected exactly three machines");
    }

    private async Task<(int Exit, string Output, string Error, string Calls)> RunAsync(string scenario)
    {
        var scratch = Path.Combine(fixture.Root, "test-results", "rollout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            await ToolAsync("gh", "printf '2026-10-13T00:00:00Z\\n'\n");
            await ToolAsync("flyctl", """
                printf '%s\n' "$*" >> "$FLY_AUDIT"
                if [[ "$1 $2" == 'machines list' ]]; then
                  printf '[{"region":"gru","state":"started"},{"region":"sjc","state":"started"},{"region":"fra","state":"started"}'
                  if [[ "$FLY_CASE" == extra-machine ]]; then printf ',{"region":"gru","state":"started"}'; fi
                  printf ']'
                fi
                """);
            await ToolAsync("curl", """
                [[ "$*" == *'https://dash.terrascale.tech/health/ready'* ]] || exit 98
                [[ "$FLY_CASE" != http-error ]] || exit 22
                case "$*" in
                  *'fly-prefer-region: gru'*) region=gru;;
                  *'fly-prefer-region: sjc'*) region=sjc;;
                  *'fly-prefer-region: fra'*) region=fra;;
                  *) exit 99;;
                esac
                status=ok
                sha=$SOURCE_REVISION
                [[ "$FLY_CASE" != wrong-region ]] || region=fra
                [[ "$FLY_CASE" != wrong-sha ]] || sha=old
                [[ "$FLY_CASE" != unready ]] || status=unavailable
                printf '{"status":"%s","version":"1.0.0+%s","region":"%s"}' "$status" "$sha" "$region"
                """);
            var info = new ProcessStartInfo("bash") { WorkingDirectory = fixture.Root, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(Path.Combine(fixture.Root, "scripts/fly-rollout.sh"));
            info.Environment["PATH"] = scratch + Path.PathSeparator + info.Environment["PATH"];
            info.Environment["SOURCE_REVISION"] = Sha;
            info.Environment["FLY_CASE"] = scenario;
            info.Environment["FLY_AUDIT"] = Path.Combine(scratch, "calls");
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return (process.ExitCode, await output, await error, await File.ReadAllTextAsync(info.Environment["FLY_AUDIT"]!));
        }
        finally { Directory.Delete(scratch, recursive: true); }

        async Task ToolAsync(string name, string script)
        {
            var path = Path.Combine(scratch, name);
            await File.WriteAllTextAsync(path, "#!/usr/bin/env bash\nset -euo pipefail\n" + script + "\n");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else throw new PlatformNotSupportedException("Fly rollout uses Linux CI.");
        }
    }
}
