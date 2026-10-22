using System.Collections.Concurrent;
using System.Text.Json;

namespace TerraBase.E2E;

/// <summary>
/// One line of JSON per test in the file named by <c>TERRABASE_E2E_RESULTS</c>, so summaries come from the run itself,
/// never from a pass count: a test that passed because its scenario is a strict expected failure is not a feature
/// passing. Categories: <c>pass</c> (a feature scenario ran and held), <c>harness</c> (a check of the harness itself),
/// <c>expected-failure</c> (listed in <see cref="Gaps"/>, with the slice it waits on), <c>not-supported</c> (an expected
/// failure that hit a hook or feature not built yet), <c>skip</c> (with TUnit's reason) and <c>fail</c>. Each line also
/// says where the nodes ran and which faults the test injected (<see cref="ScenarioAttribute"/>). scripts/terrabase/e2e-summary.sh turns the file into the report.
/// </summary>
internal static class Outcomes
{
    public const string Variable = "TERRABASE_E2E_RESULTS";

    private static readonly ConcurrentDictionary<string, (string Category, string Detail)> Marked = new(StringComparer.Ordinal);
    private static readonly Lock FileGate = new();

    /// <summary>Records why the current test passed when the reason is not its scenario holding.</summary>
    public static void Mark(string category, string detail) => Marked[TestContext.Current!.Id] = (category, detail);

    [AfterEvery(HookType.Test)]
    public static void Record(TestContext context)
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _ = Marked.TryRemove(context.Id, out var marked);
        var details = context.Metadata.TestDetails;
        var scenario = details.ClassType.GetMethod(details.MethodName)?.GetCustomAttributes(typeof(ScenarioAttribute), inherit: false).OfType<ScenarioAttribute>().FirstOrDefault();
        var (category, detail) = context.Execution.Result?.State switch
        {
            TestState.Passed => marked.Category is not null ? marked : scenario?.Nodes == Nodes.Harness ? ("harness", "") : ("pass", ""),
            TestState.Skipped => ("skip", context.Execution.SkipReason ?? ""),
            var state => ("fail", $"{state}: {context.Execution.Result?.Exception?.Message}" + (marked.Detail is null ? "" : "\n" + marked.Detail)),
        };
        var line = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["test"] = context.Metadata.DisplayName,
            ["category"] = category,
            ["detail"] = detail,
            ["nodes"] = scenario is null ? "undeclared" : scenario.Nodes switch
            {
                Nodes.Harness => "no node (harness)",
                Nodes.Cluster => Cluster.Live ? "ts-orch nodes" : "in-process nodes",
                Nodes.InProcess => "in-process nodes",
                Nodes.Processes => "separate OS processes",
                _ => "undeclared",
            },
            ["faults"] = scenario is null ? "undeclared" : scenario.Faults == Faults.None ? "none" : scenario.Faults.ToString(),
        });
        lock (FileGate)
        {
            File.AppendAllText(path, line + "\n");
        }
    }
}
