namespace TerraScale.DotNet.Tests;

public sealed class WorkflowTriggerTests
{
    [Test]
    public async Task PullRequestsToDevelopAndMainRunOnlySecretlessValidation()
    {
        var workflow = await ReadWorkflowAsync();
        var jobs = ParseJobs(workflow);
        var pullRequestJobs = jobs
            .Where(pair => IsPullRequestReachable(pair.Value))
            .ToArray();

        await Assert.That(workflow).Contains("  pull_request:\n    branches: [develop, main]");
        await Assert.That(jobs.Count).IsEqualTo(2);
        await Assert.That(pullRequestJobs.Length).IsEqualTo(1);
        await Assert.That(pullRequestJobs[0].Key).IsEqualTo("pr-validation");

        foreach (var (_, job) in pullRequestJobs)
        {
            foreach (var forbidden in new[]
                     {
                         "secrets.", "vars.", "candidate", "promote", "publish", "sign",
                         "upload-artifact", "download-artifact", "terrascale-deploy", "dotnet tool",
                         "nuget ", "curl ", "wget "
                     })
            {
                await Assert.That(job.Contains(forbidden, StringComparison.OrdinalIgnoreCase)).IsFalse();
            }

            await Assert.That(job).Contains("persist-credentials: false");
            await Assert.That(job).Contains("dotnet build TerraScale.DotNet.slnx --configuration Release");
            await Assert.That(job).Contains("dotnet test TerraScale.DotNet.slnx --configuration Release --no-build --no-progress");

            var actions = System.Text.RegularExpressions.Regex.Matches(
                job,
                @"^\s*-\s+uses:\s+\S+@(?<revision>\S+)$",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            await Assert.That(actions.Count).IsEqualTo(1);
            await Assert.That(job).Contains("uses: https://data.forgejo.org/actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803");

            foreach (System.Text.RegularExpressions.Match action in actions)
            {
                await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(
                    action.Groups["revision"].Value,
                    "^[0-9a-f]{40}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant)).IsTrue();
            }
        }
    }

    [Test]
    public async Task DeliveryCannotReusePullRequestCandidatesOrCredentials()
    {
        var workflow = await ReadWorkflowAsync();
        var jobs = ParseJobs(workflow);

        await Assert.That(workflow).Contains("  push:\n    branches: [main]");
        await Assert.That(workflow.Contains("forgejo.head_ref", StringComparison.Ordinal)).IsFalse();

        foreach (var forbidden in new[]
                 {
                     "secrets", "${{ vars.", "REGISTRY_TOKEN", "FORGEJO_TOKEN",
                     "--store-password-in-clear-text", "candidate-store", "build-candidate",
                     "candidate-manifest", "ci promote", "upload-artifact", "download-artifact",
                     "terrascale-deploy", "dotnet tool", "resolve-cli", "publish", "sign"
                 })
        {
            await Assert.That(workflow.Contains(forbidden, StringComparison.OrdinalIgnoreCase)).IsFalse();
        }

        await Assert.That(jobs.ContainsKey("trusted-candidate-required")).IsTrue();
        var protectedJob = jobs["trusted-candidate-required"];
        await Assert.That(protectedJob).Contains("forgejo.event_name == 'push' && forgejo.ref == 'refs/heads/main'");
        await Assert.That(protectedJob).Contains("permissions: {}");
        await Assert.That(protectedJob).Contains("TerraScale.Orchestrator must provide a trusted post-merge candidate with verified provenance.");
        await Assert.That(protectedJob).Contains("exit 1");
        await Assert.That(protectedJob.Contains("uses:", StringComparison.Ordinal)).IsFalse();
    }

    private static async Task<string> ReadWorkflowAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, ".forgejo", "workflows", "ci.yml")))
        {
            root = root.Parent;
        }

        await Assert.That(root).IsNotNull();
        return (await File.ReadAllTextAsync(
            Path.Combine(root!.FullName, ".forgejo", "workflows", "ci.yml"))).ReplaceLineEndings("\n");
    }

    private static IReadOnlyDictionary<string, string> ParseJobs(string workflow)
    {
        var jobsStart = workflow.IndexOf("\njobs:\n", StringComparison.Ordinal);
        ArgumentOutOfRangeException.ThrowIfLessThan(jobsStart, 0);
        var jobsSection = workflow[jobsStart..];
        var headers = System.Text.RegularExpressions.Regex.Matches(
            jobsSection,
            "^  (?<name>[a-z0-9-]+):\\n",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var jobs = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < headers.Count; index++)
        {
            var start = headers[index].Index;
            var end = index + 1 < headers.Count ? headers[index + 1].Index : jobsSection.Length;
            jobs.Add(headers[index].Groups["name"].Value, jobsSection[start..end]);
        }

        return jobs;
    }

    private static bool IsPullRequestReachable(string job)
    {
        var condition = System.Text.RegularExpressions.Regex.Match(
            job,
            "^    if: (?<value>.+)$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        return !condition.Success || condition.Groups["value"].Value.Contains("forgejo.event_name == 'pull_request'", StringComparison.Ordinal);
    }
}
