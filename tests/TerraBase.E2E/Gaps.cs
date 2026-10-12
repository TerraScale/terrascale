namespace TerraBase.E2E;

/// <summary>
/// The record of which e2e scenarios pass today and which wait on a replication slice
/// (docs/terrabase/multi-region-replication.md, section 21). A listed scenario is a strict expected failure: it passes
/// while the scenario fails and fails once it passes, so the slice that lands removes its rows here. A scenario that is
/// not listed must pass.
/// </summary>
internal static class Gaps
{
    // R2a-1 (#48) deleted the old replication; nothing crosses regions, locally or on Fly, until peer streams land.
    private const string Streams = "peer streams (R4a): nothing replicates between R2a-1 and R4a";
    // R5 landed the counter and set ops; Fast proves they converge when three nodes' states join
    // (CounterSetApiTests). Across regions they wait on the streams.
    private const string Counters = "counters and sets over peer streams (R4a)";

    private static readonly Dictionary<string, string> Waiting = new(StringComparer.Ordinal)
    {
        // ReplicationE2ETests, LatticeE2ETests, CatalogE2ETests (design section 20).
        ["E1"] = Streams,
        ["E2"] = Streams,
        ["E3"] = Streams,
        ["E4"] = Streams,
        ["E5"] = Streams,
        ["E6"] = Streams,
        ["E8"] = Counters,
        ["E9"] = Counters,
        ["E10"] = "the changes feed (R8) over peer streams (R4a)",
        ["E11"] = "SQL writes (R9) over peer streams (R4a)",
        ["E12"] = "the catalog as a lattice database (R10a)",
        ["E13"] = "home region and live move (R11)",
        ["E14"] = "garbage collection (R7) over peer streams (R4a)",
        ["E15"] = "the changes feed (R8) over peer streams (R4a)",

        // ThreeRegionE2ETests.
        ["pair"] = Streams,
        ["three-way-same-field"] = Streams,
        ["three-way-different-fields"] = Streams,
        ["three-way-delete-versus-edit"] = Streams,
        ["three-way-bulk"] = Streams,
        ["three-way-burst"] = Streams,
        ["large-documents"] = Streams,
        ["three-way-counters"] = Counters,
        ["three-way-sets"] = Counters,

        // ThreeRegionFaultE2ETests. Cluster.Partition needs R4a's streams to cut.
        ["partition-heal"] = Streams,
        ["region-down-rejoin"] = Streams,
        ["restart-mid-replication"] = Streams,
        ["backlog"] = Streams,
        ["skew-causal"] = Streams,
        ["skew-concurrent"] = Streams,
    };

    /// <summary>Runs <paramref name="scenario"/>: plainly, or as a strict expected failure while it is listed.</summary>
    public static Task RunAsync(string id, Func<Task> scenario) =>
        Waiting.TryGetValue(id, out var gap) ? Expected.FailureAsync(gap, scenario) : scenario();
}
