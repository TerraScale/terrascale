namespace TerraBase.E2E;

/// <summary>
/// The record of which e2e scenarios pass today and which wait on a replication slice
/// (docs/terrabase/multi-region-replication.md, section 21). A listed scenario is a strict expected failure: it passes
/// while the scenario fails and fails once it passes, so the slice that lands removes its rows here. A scenario that is
/// not listed must pass.
/// </summary>
internal static class Gaps
{
    // Live run of this candidate (E2E-LIVE): it carries R3, R4a, R5, R6, R7 and R10a, so only the slices it lacks stay
    // expected failures (R8, R9, R11); every other scenario must pass outright. Not on main: the strict-test owner keeps
    // main's list.
    private static readonly Dictionary<string, string> Waiting = new(StringComparer.Ordinal)
    {
        ["E10"] = "the changes feed (R8) over peer streams (R4a)",
        ["E11"] = "SQL writes (R9) over peer streams (R4a)",
        ["E13"] = "home region and live move (R11)",
        ["E15"] = "the changes feed (R8) over peer streams (R4a)",
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
    public static Task RunAsync(string id, Func<Task> scenario)
    {
        if (Cluster.FlyRequested && BlockedOnFly.TryGetValue(id, out var blocked))
        {
            Skip.Test($"BLOCKED on Fly: {blocked}");
        }

        return Waiting.TryGetValue(id, out var gap) ? Expected.FailureAsync(gap, scenario) : scenario();
    }
}
