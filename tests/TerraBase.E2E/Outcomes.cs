using System.Collections.Concurrent;
using System.Text.Json;

namespace TerraBase.E2E;

/// <summary>
/// One line of JSON per test in the file named by <c>TERRABASE_E2E_RESULTS</c>, so summaries come from the run itself,
/// never from a pass count: a test that passed because its scenario is a strict expected failure is not a feature
/// passing. Categories: <c>pass</c> (the scenario ran and held), <c>expected-failure</c> (listed in <see cref="Gaps"/>,
/// with the slice it waits on), <c>not-supported</c> (an expected failure that hit a test hook not wired yet),
/// <c>skip</c> (with TUnit's reason) and <c>fail</c>. scripts/terrabase/e2e-summary.sh turns the file into the report.
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
        var (category, detail) = context.Execution.Result?.State switch
        {
            TestState.Passed => marked.Category is null ? ("pass", "") : marked,
            TestState.Skipped => ("skip", context.Execution.SkipReason ?? ""),
            var state => ("fail", $"{state}: {context.Execution.Result?.Exception?.Message}"),
        };
        var line = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["test"] = context.Metadata.DisplayName,
            ["category"] = category,
            ["detail"] = detail,
        });
        lock (FileGate)
        {
            File.AppendAllText(path, line + "\n");
        }
    }
}
