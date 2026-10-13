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
    // On main every listed scenario stops in the cluster's setup: the collection it creates in gru never reaches the other
    // regions without replication. That is reported as not supported (it never reached its own assertions), apart from a
    // scenario failing its own way.
    private const string AtSetup = "collection on sjc did not converge";

    // Live run of this candidate (E2E-LIVE, not for main): it carries R3, R4a, R5, R6, R7 and R10a, so only the slices it
    // may lack stay expected failures (R8 E10/E15, R9 E11, R11 E13); every other scenario must pass outright.
    private static readonly Dictionary<string, Gap> Waiting = new(StringComparer.Ordinal)
    {
        ["E10"] = new("the changes feed (R8) over peer streams (R4a)", AtSetup),
        ["E11"] = new("SQL writes (R9) over peer streams (R4a)", AtSetup),
        ["E13"] = new("home region and live move (R11)", AtSetup),
        ["E15"] = new("the changes feed (R8) over peer streams (R4a)", AtSetup),
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

