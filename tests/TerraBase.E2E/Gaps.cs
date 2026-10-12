namespace TerraBase.E2E;

/// <summary>
/// The record of which e2e scenarios pass today and which wait on a replication slice
/// (docs/terrabase/multi-region-replication.md, section 21). A listed scenario is a strict expected failure: it passes
/// while the scenario fails and fails once it passes, so the slice that lands removes its rows here. A scenario that is
/// not listed must pass.
/// </summary>
internal static class Gaps
{
    private static readonly Dictionary<string, string> Waiting = new(StringComparer.Ordinal)
    {
        // ReplicationE2ETests, LatticeE2ETests, CatalogE2ETests (design section 20).
        ["E11"] = "SQL writes (R9) over peer streams (R4a)",
        ["E12"] = "the catalog as a lattice database (R10a)",
        ["E13"] = "home region and live move (R11)",
        ["E15"] = "ts-auth refresh-token reuse across regions (R12) on the changes feed (R8)",

        // RepairAndContractE2ETests.
        ["deleted-database-stays-deleted"] = "the catalog as a lattice database (R10a) and garbage collection (R7)",
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
