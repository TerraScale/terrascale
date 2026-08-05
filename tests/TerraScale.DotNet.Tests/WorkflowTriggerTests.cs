using YamlDotNet.RepresentationModel;

namespace TerraScale.DotNet.Tests;

public sealed class WorkflowTriggerTests
{
    private const string PinnedCheckout = "https://data.forgejo.org/actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803";

    [Test]
    public async Task PullRequestsToDevelopAndMainRunOnlyIsolatedSecretlessValidation()
    {
        var workflow = await ReadWorkflowAsync();
        var jobs = ParseJobs(workflow);
        var unexpectedJobs = jobs.Keys
            .Except(["pr-validation", "trusted-candidate-required"], StringComparer.Ordinal)
            .ToArray();

        await Assert.That(workflow).Contains("  pull_request:\n    branches: [develop, main]");
        await Assert.That(unexpectedJobs.Length).IsEqualTo(0);
        await Assert.That(FindPullRequestPolicyViolations(jobs).Length).IsEqualTo(0);
    }

    [Test]
    public async Task UnderscoreNamedPullRequestJobWithUnpinnedActionIsRejected()
    {
        var jobs = ParseJobs("""
            jobs:
              pr_validation_extra:
                if: ${{ forgejo.event_name == 'pull_request' }}
                runs-on: terrascale-untrusted
                permissions:
                  contents: read
                steps:
                  - uses: https://data.forgejo.org/actions/checkout@v6
                    with:
                      persist-credentials: false
            """);

        var violations = FindPullRequestPolicyViolations(jobs);

        await Assert.That(jobs.ContainsKey("pr_validation_extra")).IsTrue();
        await Assert.That(violations.Any(violation => violation.Contains("pr_validation_extra: action", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task PullRequestRunnerMutationToBuilderIsRejected()
    {
        var workflow = await ReadWorkflowAsync();
        var mutatedWorkflow = workflow.Replace(
            "runs-on: terrascale-untrusted",
            "runs-on: terrascale-builder",
            StringComparison.Ordinal);

        var violations = FindPullRequestPolicyViolations(ParseJobs(mutatedWorkflow));

        await Assert.That(violations.Any(violation => violation.Contains(
            "pr-validation: runs-on must be terrascale-untrusted.",
            StringComparison.Ordinal))).IsTrue();
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

        var protectedJob = jobs["trusted-candidate-required"];
        await Assert.That(GetScalar(protectedJob, "if")).IsEqualTo("${{ forgejo.event_name == 'push' && forgejo.ref == 'refs/heads/main' }}");
        await Assert.That(GetMapping(protectedJob, "permissions")?.Children.Count).IsEqualTo(0);
        await Assert.That(ScalarTexts(protectedJob).Any(text => text.Contains(
            "TerraScale.Orchestrator must provide a trusted post-merge candidate with verified provenance.",
            StringComparison.Ordinal))).IsTrue();
        await Assert.That(ScalarTexts(protectedJob).Any(text => text.Contains("exit 1", StringComparison.Ordinal))).IsTrue();
        await Assert.That(ActionReferences(protectedJob).Length).IsEqualTo(0);
    }

    private static string[] FindPullRequestPolicyViolations(IReadOnlyDictionary<string, YamlMappingNode> jobs)
    {
        var violations = new List<string>();

        foreach (var (name, job) in jobs.Where(pair => IsPullRequestReachable(pair.Value)))
        {
            if (GetScalar(job, "runs-on") != "terrascale-untrusted")
            {
                violations.Add($"{name}: runs-on must be terrascale-untrusted.");
            }

            foreach (var forbiddenKey in new[] { "container", "services", "volumes", "privileged", "env", "environment", "token", "credentials", "ssh-key" })
            {
                if (HasMappingKey(job, forbiddenKey))
                {
                    violations.Add($"{name}: must not define {forbiddenKey}.");
                }
            }

            var permissions = GetMapping(job, "permissions");
            if (permissions is null || permissions.Children.Count != 1 || GetScalar(permissions, "contents") != "read")
            {
                violations.Add($"{name}: permissions must be limited to contents: read.");
            }

            var text = string.Join('\n', ScalarTexts(job));
            foreach (var forbidden in new[]
                     {
                         "secrets", "vars.", "candidate", "promote", "publish", "sign", "cache",
                         "terrascale-builder", "upload-artifact", "download-artifact", "terrascale-deploy",
                         "dotnet tool", "nuget ", "curl ", "wget "
                     })
            {
                if (text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{name}: contains forbidden {forbidden}.");
                }
            }

            var actions = ActionReferences(job);
            if (actions.Length != 1 || actions[0] != PinnedCheckout)
            {
                violations.Add($"{name}: action must be the approved pinned checkout action.");
            }

            var persistCredentials = MappingNodes(job)
                .Select(mapping => GetScalar(mapping, "persist-credentials"))
                .Where(value => value is not null)
                .ToArray();
            if (persistCredentials.Length != 1 || persistCredentials[0] != "false")
            {
                violations.Add($"{name}: checkout credentials must not persist.");
            }
        }

        return [.. violations];
    }

    private static bool IsPullRequestReachable(YamlMappingNode job)
    {
        var condition = GetScalar(job, "if");
        return condition is null ||
               (!condition.Contains("forgejo.event_name == 'push'", StringComparison.Ordinal) &&
                !condition.Contains("forgejo.event_name != 'pull_request'", StringComparison.Ordinal));
    }

    private static string[] ActionReferences(YamlNode node) =>
        [.. MappingNodes(node)
            .Select(mapping => GetScalar(mapping, "uses"))
            .Where(value => value is not null)!];

    private static bool HasMappingKey(YamlNode node, string key) =>
        MappingNodes(node).Any(mapping => GetValue(mapping, key) is not null);

    private static IEnumerable<YamlMappingNode> MappingNodes(YamlNode node)
    {
        if (node is YamlMappingNode mapping)
        {
            yield return mapping;
            foreach (var pair in mapping.Children)
            {
                foreach (var child in MappingNodes(pair.Value))
                {
                    yield return child;
                }
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var childNode in sequence.Children)
            {
                foreach (var child in MappingNodes(childNode))
                {
                    yield return child;
                }
            }
        }
    }

    private static IEnumerable<string> ScalarTexts(YamlNode node)
    {
        if (node is YamlScalarNode scalar && scalar.Value is not null)
        {
            yield return scalar.Value;
        }
        else if (node is YamlMappingNode mapping)
        {
            foreach (var pair in mapping.Children)
            {
                foreach (var keyText in ScalarTexts(pair.Key))
                {
                    yield return keyText;
                }

                foreach (var valueText in ScalarTexts(pair.Value))
                {
                    yield return valueText;
                }
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var childNode in sequence.Children)
            {
                foreach (var text in ScalarTexts(childNode))
                {
                    yield return text;
                }
            }
        }
    }

    private static YamlMappingNode? GetMapping(YamlMappingNode mapping, string key) =>
        GetValue(mapping, key) as YamlMappingNode;

    private static string? GetScalar(YamlMappingNode mapping, string key) =>
        (GetValue(mapping, key) as YamlScalarNode)?.Value;

    private static YamlNode? GetValue(YamlMappingNode mapping, string key)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar && scalar.Value == key)
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, YamlMappingNode> ParseJobs(string workflow)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(workflow));

        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException("Workflow must be a single YAML mapping document.");
        }

        var jobs = GetMapping(root, "jobs") ?? throw new InvalidDataException("Workflow must define jobs.");
        return jobs.Children.ToDictionary(
            pair => (pair.Key as YamlScalarNode)?.Value ?? throw new InvalidDataException("Workflow job names must be scalars."),
            pair => pair.Value as YamlMappingNode ?? throw new InvalidDataException("Workflow jobs must be mappings."),
            StringComparer.Ordinal);
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
}
