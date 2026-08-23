using YamlDotNet.RepresentationModel;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace TerraScale.DotNet.Tests;

public sealed class WorkflowTriggerTests
{
    private const string PinnedCheckout = "https://data.forgejo.org/actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803";
    private const string TrustedMainCondition = "${{ forgejo.event_name == 'push' && forgejo.ref == 'refs/heads/main' }}";

    [Test]
    public async Task PullRequestsToDevelopAndMainRunOnlyIsolatedSecretlessValidation()
    {
        var workflow = await ReadWorkflowAsync();
        var parsedWorkflow = ParseWorkflow(workflow);
        var jobs = parsedWorkflow.Jobs;
        var unexpectedJobs = jobs.Keys
            .Except(["pr-validation", "trusted-candidate-required"], StringComparer.Ordinal)
            .ToArray();

        await Assert.That(workflow).Contains("  pull_request:\n    branches: [develop, main]");
        await Assert.That(unexpectedJobs.Length).IsEqualTo(0);
        await Assert.That(FindWorkflowPolicyViolations(parsedWorkflow).Length).IsEqualTo(0);
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
    public async Task CompositeAndNegatedConditionsAreRestrictedAsPullRequestReachable()
    {
        foreach (var (name, condition) in new[]
                 {
                     ("combined-events", "${{ forgejo.event_name == 'pull_request' || forgejo.event_name == 'push' }}"),
                     ("negated-pull-request", "${{ !(forgejo.event_name != 'pull_request') }}")
                 })
        {
            var jobs = ParseJobs($$"""
                jobs:
                  {{name}}:
                    if: {{condition}}
                    runs-on: terrascale-builder
                    permissions: {}
                    env:
                      REGISTRY_TOKEN: not-a-secret
                    steps:
                      - uses: https://data.forgejo.org/actions/checkout@v6
                """);

            var violations = FindPullRequestPolicyViolations(jobs);

            await Assert.That(violations.Any(violation => violation.Contains(
                $"{name}: runs-on must be terrascale-untrusted.",
                StringComparison.Ordinal))).IsTrue();
            await Assert.That(violations.Any(violation => violation.Contains(
                $"{name}: must not define env.",
                StringComparison.Ordinal))).IsTrue();
            await Assert.That(violations.Any(violation => violation.Contains(
                $"{name}: action must be the approved pinned checkout action.",
                StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    public async Task DuplicateIfKeysAreRejectedBeforeReachabilityEvaluation()
    {
        var duplicateIf = """
            jobs:
              pr-validation:
                if: ${{ forgejo.event_name == 'push' && forgejo.ref == 'refs/heads/main' }}
                if: ${{ forgejo.event_name == 'pull_request' }}
                runs-on: terrascale-builder
                permissions: {}
                steps:
                  - uses: https://data.forgejo.org/actions/checkout@v6
            """;

        await Assert.That(RejectWorkflow(duplicateIf)).Contains("duplicate mapping key 'if'");
    }

    [Test]
    public async Task DuplicateKeysInStepWithMappingAreRejectedBeforePolicyEvaluation()
    {
        var duplicatePersistCredentials = """
            jobs:
              pr-validation:
                if: ${{ forgejo.event_name == 'pull_request' }}
                runs-on: terrascale-untrusted
                permissions:
                  contents: read
                steps:
                  - uses: https://data.forgejo.org/actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803
                    with:
                      persist-credentials: false
                      persist-credentials: true
            """;

        await Assert.That(RejectWorkflow(duplicatePersistCredentials)).Contains("duplicate mapping key 'persist-credentials'");
    }

    [Test]
    public async Task DuplicateKeysInSequenceItemMappingAreRejectedBeforePolicyEvaluation()
    {
        var duplicateStepEnvironment = """
            jobs:
              pr-validation:
                if: ${{ forgejo.event_name == 'pull_request' }}
                runs-on: terrascale-untrusted
                permissions:
                  contents: read
                steps:
                  - name: build
                    env:
                      CONFIGURATION: Release
                      CONFIGURATION: Debug
                    run: dotnet build
            """;

        await Assert.That(RejectWorkflow(duplicateStepEnvironment)).Contains("duplicate mapping key 'CONFIGURATION'");
    }

    [Test]
    public async Task RootEnvironmentAndDefaultsAreRejectedForPullRequestReachableJobs()
    {
        var parsedWorkflow = ParseWorkflow("""
            env:
              REGISTRY_TOKEN: not-a-secret
            defaults:
              run:
                shell: bash
            jobs:
              pr-validation:
                if: ${{ forgejo.event_name == 'pull_request' }}
                runs-on: terrascale-untrusted
                permissions:
                  contents: read
                steps:
                  - uses: https://data.forgejo.org/actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803
                    with:
                      persist-credentials: false
            """);

        var violations = FindWorkflowPolicyViolations(parsedWorkflow);

        await Assert.That(violations.Any(violation => violation == "root: PR-reachable jobs must not inherit env.")).IsTrue();
        await Assert.That(violations.Any(violation => violation == "root: PR-reachable jobs must not inherit defaults.")).IsTrue();
    }

    [Test]
    public async Task PullRequestAliasAndMergeInjectionsAreRejectedBeforePolicyEvaluation()
    {
        var aliasedEnvironment = """
            unsafe_environment: &unsafe_environment
              REGISTRY_TOKEN: not-a-secret
            jobs:
              pr-validation:
                if: ${{ forgejo.event_name == 'pull_request' }}
                runs-on: terrascale-untrusted
                permissions:
                  contents: read
                env: *unsafe_environment
                steps:
                  - uses: https://data.forgejo.org/actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803
                    with:
                      persist-credentials: false
            """;
        var mergedContainerAndAction = """
            jobs:
              pr-validation:
                <<:
                  container: untrusted-container
                  steps:
                    - uses: https://data.forgejo.org/actions/checkout@v6
                if: ${{ forgejo.event_name == 'pull_request' }}
                runs-on: terrascale-untrusted
                permissions:
                  contents: read
            """;

        await Assert.That(RejectWorkflow(aliasedEnvironment)).Contains("aliases and merge keys are not allowed");
        await Assert.That(RejectWorkflow(mergedContainerAndAction)).Contains("aliases and merge keys are not allowed");
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
        await Assert.That(GetScalar(protectedJob, "if")).IsEqualTo(TrustedMainCondition);
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

    private static string[] FindWorkflowPolicyViolations(ParsedWorkflow workflow)
    {
        var violations = new List<string>();

        if (workflow.Jobs.Values.Any(IsPullRequestReachable))
        {
            foreach (var inheritedSetting in new[] { "env", "defaults" })
            {
                if (GetValue(workflow.Root, inheritedSetting) is not null)
                {
                    violations.Add($"root: PR-reachable jobs must not inherit {inheritedSetting}.");
                }
            }
        }

        violations.AddRange(FindPullRequestPolicyViolations(workflow.Jobs));
        return [.. violations];
    }

    private static bool IsPullRequestReachable(YamlMappingNode job)
    {
        return GetScalar(job, "if") != TrustedMainCondition;
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

    private static IReadOnlyDictionary<string, YamlMappingNode> ParseJobs(string workflow) =>
        ParseWorkflow(workflow).Jobs;

    private static ParsedWorkflow ParseWorkflow(string workflow)
    {
        RejectAliasesAndDuplicateMappingKeys(workflow);

        var yaml = new YamlStream();
        yaml.Load(new StringReader(workflow));

        if (yaml.Documents.Count != 1)
        {
            throw new InvalidDataException("Workflow must be a single YAML mapping document.");
        }

        RejectMergeKeys(yaml.Documents[0].RootNode);

        if (yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException("Workflow must be a YAML mapping document.");
        }

        var jobs = GetMapping(root, "jobs") ?? throw new InvalidDataException("Workflow must define jobs.");
        return new ParsedWorkflow(
            root,
            jobs.Children.ToDictionary(
            pair => (pair.Key as YamlScalarNode)?.Value ?? throw new InvalidDataException("Workflow job names must be scalars."),
            pair => pair.Value as YamlMappingNode ?? throw new InvalidDataException("Workflow jobs must be mappings."),
            StringComparer.Ordinal));
    }

    private static string RejectWorkflow(string workflow)
    {
        try
        {
            _ = ParseJobs(workflow);
            return string.Empty;
        }
        catch (InvalidDataException exception)
        {
            return exception.Message;
        }
    }

    private static void RejectAliasesAndDuplicateMappingKeys(string workflow)
    {
        var parser = new Parser(new StringReader(workflow));
        while (parser.MoveNext())
        {
            if (parser.Current is StreamStart or DocumentStart or DocumentEnd or StreamEnd)
            {
                continue;
            }

            RejectYamlNode(parser);
        }
    }

    private static string? RejectYamlNode(IParser parser)
    {
        switch (parser.Current)
        {
            case AnchorAlias:
                throw new InvalidDataException("Workflow YAML aliases and merge keys are not allowed.");
            case Scalar scalar:
                return scalar.Value;
            case MappingStart:
                RejectDuplicateMappingKeys(parser);
                return null;
            case SequenceStart:
                RejectDuplicateMappingKeysInSequence(parser);
                return null;
            default:
                throw new InvalidDataException("Workflow YAML contains an unsupported parsing event.");
        }
    }

    private static void RejectDuplicateMappingKeys(IParser parser)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        while (parser.MoveNext())
        {
            if (parser.Current is MappingEnd)
            {
                return;
            }

            var key = RejectYamlNode(parser) ?? throw new InvalidDataException("Workflow YAML mapping keys must be scalars.");
            if (!keys.Add(key))
            {
                throw new InvalidDataException($"Workflow YAML contains duplicate mapping key '{key}'.");
            }

            if (!parser.MoveNext() || parser.Current is MappingEnd)
            {
                throw new InvalidDataException("Workflow YAML mapping keys must have values.");
            }

            _ = RejectYamlNode(parser);
        }

        throw new InvalidDataException("Workflow YAML contains an unterminated mapping.");
    }

    private static void RejectDuplicateMappingKeysInSequence(IParser parser)
    {
        while (parser.MoveNext())
        {
            if (parser.Current is SequenceEnd)
            {
                return;
            }

            _ = RejectYamlNode(parser);
        }

        throw new InvalidDataException("Workflow YAML contains an unterminated sequence.");
    }

    private static void RejectMergeKeys(YamlNode node)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach (var pair in mapping.Children)
            {
                if (pair.Key is YamlScalarNode { Value: "<<" })
                {
                    throw new InvalidDataException("Workflow YAML aliases and merge keys are not allowed.");
                }

                RejectMergeKeys(pair.Key);
                RejectMergeKeys(pair.Value);
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var child in sequence.Children)
            {
                RejectMergeKeys(child);
            }
        }
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

    private sealed record ParsedWorkflow(
        YamlMappingNode Root,
        IReadOnlyDictionary<string, YamlMappingNode> Jobs);
}
