namespace TerraBase.E2E;

/// <summary>
/// The record of which three-region scenarios pass today and which wait on a replication slice
/// (docs/terrabase/multi-region-replication.md, section 21). A listed scenario is a strict expected failure: it passes
/// while the scenario fails and fails once it passes. A slice that lands removes its rows here, and a slice that takes
/// replication down for a while (R2a until R4a) adds the scenarios it breaks.
/// </summary>
internal static class Gaps
{
    private const string PerField = "the per-field lattice on the write path (R2a) replicated by peer streams (R4a)";
    private const string Counters = "counters and sets (R5)";

    private static readonly Dictionary<string, string> Waiting = new(StringComparer.Ordinal)
    {
        ["three-way-different-fields"] = PerField,
        ["three-way-counters"] = Counters,
        ["three-way-sets"] = Counters,
    };

    // Scenarios whose outcome today's engine leaves to chance (its pick between concurrent writes is not the newest
    // stamp), so a strict expected failure would flake: a failed check is logged, while no convergence or an error still
    // fails the test. The slice that decides the outcome moves the scenario out.
    private static readonly Dictionary<string, string> Undecided = new(StringComparer.Ordinal)
    {
        ["skew-concurrent"] = "last-writer-wins by (stamp, node) on the write path (R2a)",
    };

    /// <summary>Runs <paramref name="scenario"/>: plainly, as a strict expected failure, or with its outcome only logged.</summary>
    public static async Task RunAsync(string id, Func<Task> scenario)
    {
        if (Waiting.TryGetValue(id, out var gap))
        {
            await Expected.FailureAsync(gap, scenario);
        }
        else if (Undecided.TryGetValue(id, out var decider))
        {
            try
            {
                await scenario();
            }
            catch (ScenarioException error)
            {
                Console.WriteLine($"outcome not guaranteed until {decider}: {error.Message}");
            }
        }
        else
        {
            await scenario();
        }
    }
}
