namespace TerraScale.Dashboard2;

/// <summary>Outcome of evaluating a rule against a context
/// (1:1 with AccessRuleEvaluation in access-rules-data.ts).</summary>
public sealed record AccessRuleEvaluation(
    bool Allowed,
    string Decision,
    AccessRuleDiagnostic? Error = null);
