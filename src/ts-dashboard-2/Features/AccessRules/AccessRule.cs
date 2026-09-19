namespace TerraScale.Dashboard2;

/// <summary>A stored CEL access rule with its revision context
/// (revision/entitlement epoch default to the local-draft values from
/// access-rules-data.ts).</summary>
public sealed record AccessRule(
    string Id,
    string Name,
    string Source,
    string Revision,
    string EntitlementEpoch)
{
    /// <summary>accessRuleGeneration from access-rules-data.ts.</summary>
    public string Generation => $"{Revision}:{EntitlementEpoch}";
}
