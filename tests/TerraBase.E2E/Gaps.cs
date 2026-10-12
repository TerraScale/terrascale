namespace TerraBase.E2E;

/// <summary>
/// The record of which e2e scenarios pass today and which wait on a replication slice
/// (docs/terrabase/multi-region-replication.md, section 21). A listed scenario is a strict expected failure: it passes
/// while the scenario fails and fails once it passes, so the slice that lands removes its rows here. A scenario that is
/// not listed must pass.
/// </summary>
internal static class Gaps
{
    // R2a-1 (#48) deleted the old replication. R4a's peer streams carry change groups, but only the CDC tailer (R3) produces
    // them from real writes; until it lands nothing crosses regions, locally or on Fly.
    private const string Streams = "the CDC change group source (R3) for the peer streams (R4a)";
    // R5 landed the counter and set ops; Fast proves they converge when three nodes' states join
    // (CounterSetApiTests). Across regions they wait on the streams' source (R3).
    private const string Counters = "counters and sets over peer streams (R4a) fed by the CDC source (R3)";

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
        ["ack-then-stop"] = Streams,
        ["restarted-sender-backlog"] = Streams,

        // CrashAndStallE2ETests. Node processes and lossy links also need test hooks on the peer path (COVERAGE.md).
        ["crash-replicated"] = Streams,
        ["storage-stall"] = Streams,
        ["lossy-link"] = Streams,

        // RepairAndContractE2ETests.
        ["anti-entropy-repair"] = "anti-entropy (R6) over peer streams fed by the CDC source (R3)",
        ["restore-older-state"] = "refill and anti-entropy (R6) over peer streams fed by the CDC source (R3)",
        ["unique-under-partition"] = Streams,
        ["deleted-database-stays-deleted"] = "the catalog as a lattice database (R10a) and garbage collection (R7)",
        ["replication-metrics"] = "replication metrics with the CDC source (R3): tail lag, and stream lags that move",
        ["flow-control"] = "peer streams fed by the CDC source (R3) and the catalog as a lattice database (R10a)",
        ["ddl-replication"] = Streams,
        ["backlog"] = Streams,
        ["skew-causal"] = Streams,
        ["skew-concurrent"] = Streams,
    };

    // BLOCKED on Fly, not an expected failure there: the Fly suite's TerraBase API key holds documents:read/write on the e2e
    // project only, so these scenarios' database, index or schema writes (databases:write) are refused before the slice's
    // gap is even reached; counting that 403 as the gap would be a fake pass. Unblocked by a ts-auth project token with
    // databases:* (GitHub OIDC exchange).
    private const string NoDatabaseScope = "no appropriately-scoped auth on Fly (API keys can't hold databases:*)";

    private static readonly Dictionary<string, string> BlockedOnFly = new(StringComparer.Ordinal)
    {
        ["E12"] = NoDatabaseScope,
        ["E13"] = NoDatabaseScope,
        ["deleted-database-stays-deleted"] = NoDatabaseScope,
        ["flow-control"] = NoDatabaseScope,
        ["ddl-replication"] = NoDatabaseScope,
    };

    /// <summary>Runs <paramref name="scenario"/>: plainly, or as a strict expected failure while it is listed; skipped as BLOCKED on Fly when it is.</summary>
    public static async Task RunAsync(string id, Func<Task> scenario)
    {
        if (Cluster.FlyRequested && BlockedOnFly.TryGetValue(id, out var blocked))
        {
            Skip.Test($"BLOCKED on Fly: {blocked}");
        }

        if (!Waiting.TryGetValue(id, out var gap))
        {
            await scenario();
            return;
        }

        // A scenario that stopped at a test hook nobody has wired yet is reported apart: it proves less than one that ran.
        var failure = await Expected.FailureAsync(gap, scenario);
        if (Unsupported(failure) is { } unsupported)
        {
            Outcomes.Mark("not-supported", $"{gap}; {unsupported.Message}");
        }
        else
        {
            Outcomes.Mark("expected-failure", gap);
        }
    }

    private static NotSupportedException? Unsupported(Exception? error) => error switch
    {
        null => null,
        NotSupportedException unsupported => unsupported,
        AggregateException aggregate => aggregate.InnerExceptions.Select(Unsupported).FirstOrDefault(found => found is not null),
        _ => Unsupported(error.InnerException),
    };
}
