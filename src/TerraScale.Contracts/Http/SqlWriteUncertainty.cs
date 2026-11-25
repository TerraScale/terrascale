namespace TerraScale.Contracts.Http;

public static class SqlWriteUncertainty
{
    public const string SqlState = "08007";

    public static bool Matches(string code) => code is "in_region_quorum_unconfirmed" or "write_outcome_unknown" or "commit_uncertain";
}
