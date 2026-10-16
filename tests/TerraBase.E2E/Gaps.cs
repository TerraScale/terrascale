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
    // Every other scenario runs plainly: R3's CDC source, R4a's peer streams, R5's lattice, R6's repair and R8's feed are on
    // main, and those scenarios pass (triage of main e30ffdc2, two full runs). A listed scenario waits on a slice that is
    // not on main, and fails today exactly as its signature says, the way it must stop failing when the slice lands.
    private static readonly Dictionary<string, Gap> Waiting = new(StringComparer.Ordinal)
    {
        // LatticeE2ETests: SQL writes are R9 (no PR yet); the subset refuses an UPDATE before the scenario's checks.
        ["E11"] = new("SQL writes over peer streams (R9, no PR yet)", "only SELECT statements are supported"),
        // GC is R7 (#120, R7-FIX): no tombstone of a deleted document is ever collected on main.
        ["E14"] = new("garbage collection (R7, #120 / R7-FIX)", "GC collects the tombstones of deleted documents"),

        // CatalogE2ETests: live move is R11 (no PR yet), whose route does not exist.
        ["E13"] = new("home region and live move (R11, no PR yet)", "unknown_route"),

        // RepairAndContractE2ETests: the new database stops at the same catalog step; its deletion while a region is away
        // also needs R7.
        ["deleted-database-stays-deleted"] = new("the catalog as a lattice database (R10a, #116/#121), then garbage collection (R7, #120)", "the new database on sjc did not converge"),

        // CrashAndStallE2ETests: harness hooks not wired to these scenarios, reported as not supported (they never reach
        // their assertions). Node processes as regions exist (RegionProcesses) and the storage stall hook exists
        // (Cluster.StallStorageAsync); wiring them here is open E2E harness work.
        ["crash-replicated"] = new("E2E harness: node processes as regions (RegionProcesses) not wired to this scenario", "not supported"),
        ["storage-stall"] = new("E2E harness: the storage stall hook (Cluster.StallStorageAsync) not wired to this scenario", "not supported"),
        ["storage-stall-single"] = new("E2E harness: the storage stall hook (Cluster.StallStorageAsync) not wired to this scenario", "not supported"),
    };

    // On Fly the suite authenticates with a TerraBase API key of its own project (#81), and API keys cannot hold
    // databases:*. A scenario that creates or deletes a database therefore cannot pass there, whatever slice lands: it
    // stays a strict expected failure on Fly until an appropriately scoped token replaces the key (the OIDC exchange in
    // .cos/e2e-credential-plan.md). Locally the nodes accept project tokens with databases:write, so only the slice gap
    // applies there.
    private const string NoDatabaseScope = "no appropriately-scoped auth on Fly (API keys can't hold databases:*)";

    private static readonly HashSet<string> CreatesOrDeletesDatabases =
        [with(StringComparer.Ordinal), "E12", "E13", "deleted-database-stays-deleted", "flow-control"];

    /// <summary>Runs <paramref name="scenario"/>: plainly, or as a strict expected failure while it is listed.</summary>
    /// <remarks>
    /// No scenario is skipped for its credential: on Fly every request carries the bound e2e project's ts-auth project token
    /// (databases:* and documents:*), so an authentication refusal is a failure, never a gap. A scenario that faults Fly
    /// machines keeps its own guard (SkipOnFlyUnlessOutageRun, Cluster's fault checks).
    /// </remarks>
    public static async Task RunAsync(string id, Func<Task> scenario)
    {
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

