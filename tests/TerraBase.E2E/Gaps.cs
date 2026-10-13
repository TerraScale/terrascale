namespace TerraBase.E2E;

/// <summary>
/// The record of which e2e scenarios pass today and which wait on a replication slice
/// (docs/terrabase/multi-region-replication.md, section 21). A listed scenario is a strict expected failure: it passes
/// while the scenario fails the way its signature names (<see cref="Expected"/>) and fails once it passes, so the slice
/// that lands removes its rows here. A listed scenario failing any other way fails. A scenario that is
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

    // On main every listed scenario stops in the cluster's setup: the collection it creates in gru never reaches the other
    // regions without replication. That is reported as not supported (it never reached its own assertions), apart from a
    // scenario failing its own way.
    private const string AtSetup = "collection on sjc did not converge";

    private static readonly Dictionary<string, Gap> Waiting = new(StringComparer.Ordinal)
    {
        // ReplicationE2ETests, LatticeE2ETests, CatalogE2ETests (design section 20).
        ["E1"] = new(Streams, AtSetup),
        ["E2"] = new(Streams, AtSetup),
        ["E3"] = new(Streams, AtSetup),
        ["E4"] = new(Streams, AtSetup),
        ["E5"] = new(Streams, AtSetup),
        ["E6"] = new(Streams, AtSetup),
        ["E8"] = new(Counters, AtSetup),
        ["E9"] = new(Counters, AtSetup),
        ["E10"] = new("the changes feed (R8) over peer streams (R4a)", AtSetup),
        ["E11"] = new("SQL writes (R9) over peer streams (R4a)", AtSetup),
        ["E12"] = new("the catalog as a lattice database (R10a)", AtSetup),
        ["E13"] = new("home region and live move (R11)", AtSetup),
        ["E14"] = new("garbage collection (R7) over peer streams (R4a)", AtSetup),
        ["E15"] = new("the changes feed (R8) over peer streams (R4a)", AtSetup),

        // ThreeRegionE2ETests.
        ["pair"] = new(Streams, AtSetup),
        ["three-way-same-field"] = new(Streams, AtSetup),
        ["three-way-different-fields"] = new(Streams, AtSetup),
        ["three-way-delete-versus-edit"] = new(Streams, AtSetup),
        ["three-way-bulk"] = new(Streams, AtSetup),
        ["three-way-burst"] = new(Streams, AtSetup),
        ["large-documents"] = new(Streams, AtSetup),
        ["three-way-counters"] = new(Counters, AtSetup),
        ["three-way-sets"] = new(Counters, AtSetup),

        // ThreeRegionFaultE2ETests. Cluster.Partition needs R4a's streams to cut.
        ["partition-heal"] = new(Streams, AtSetup),
        ["region-down-rejoin"] = new(Streams, AtSetup),
        ["restart-mid-replication"] = new(Streams, AtSetup),
        ["ack-then-stop"] = new(Streams, AtSetup),
        ["restarted-sender-backlog"] = new(Streams, AtSetup),

        // CrashAndStallE2ETests. Node processes and lossy links also need test hooks on the peer path (COVERAGE.md).
        ["crash-replicated"] = new(Streams, AtSetup),
        ["storage-stall"] = new(Streams, AtSetup),
        // Passed on SQLite until the Turso engine (#98) took the file's write lock for its single writer.
        ["storage-stall-single"] = new("a storage stall hook for the Turso engine (#98 holds the file's write lock)", AtSetup),
        ["lossy-link"] = new(Streams, AtSetup),

        // RepairAndContractE2ETests.
        ["anti-entropy-repair"] = new("anti-entropy (R6) over peer streams fed by the CDC source (R3)", AtSetup),
        ["restore-older-state"] = new("refill and anti-entropy (R6) over peer streams fed by the CDC source (R3)", AtSetup),
        ["unique-under-partition"] = new(Streams, AtSetup),
        ["deleted-database-stays-deleted"] = new("the catalog as a lattice database (R10a) and garbage collection (R7)", AtSetup),
        ["replication-metrics"] = new("replication metrics with the CDC source (R3): tail lag, and stream lags that move", AtSetup),
        ["flow-control"] = new("peer streams fed by the CDC source (R3) and the catalog as a lattice database (R10a)", AtSetup),
        ["ddl-replication"] = new(Streams, AtSetup),
        ["backlog"] = new(Streams, AtSetup),
        ["skew-causal"] = new(Streams, AtSetup),
        ["skew-concurrent"] = new(Streams, AtSetup),
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

        // A pinned run is acceptance evidence: only passing scenarios count, so a listed one fails before its cluster starts.
        if (Cluster.FlyPinned && PinnedRefusal(id) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        if (!Waiting.TryGetValue(id, out var gap))
        {
            await scenario();
            return;
        }

        // A scenario that stopped at a test hook nobody has wired yet is reported apart: it proves less than one that ran.
        var failure = await Expected.FailureAsync(gap.Waits, gap.Fails, scenario);
        if (Unsupported(failure) is { } unsupported)
        {
            Outcomes.Mark("not-supported", $"{gap.Waits}; {unsupported.Message}");
        }
        else if (gap.Fails == AtSetup)
        {
            Outcomes.Mark("not-supported", $"{gap.Waits}; never reached its assertions: the cluster's setup cannot finish without it ({failure.GetType().Name} with \"{AtSetup}\")");
        }
        else
        {
            Outcomes.Mark("expected-failure", $"{gap.Waits}; failed as named: {failure.GetType().Name} with \"{gap.Fails}\"");
        }
    }

    /// <summary>Why a machine-pinned run refuses <paramref name="id"/>: it is still listed here; null when it must pass.</summary>
    public static string? PinnedRefusal(string id) => Waiting.TryGetValue(id, out var gap)
        ? $"{id} is still a strict expected failure waiting on {gap.Waits}; a machine-pinned run counts only scenarios that must pass, so its Gaps row must be removed first"
        : null;

    private static NotSupportedException? Unsupported(Exception? error) => error switch
    {
        null => null,
        NotSupportedException unsupported => unsupported,
        AggregateException aggregate => aggregate.InnerExceptions.Select(Unsupported).FirstOrDefault(found => found is not null),
        _ => Unsupported(error.InnerException),
    };
}

/// <summary>A gap: the slice the scenario waits on, and the text its own failure carries today (<see cref="Expected"/>).</summary>
internal sealed record Gap(string Waits, string Fails);

