namespace TerraScale.Dashboard2;

/// <summary>Last dry-run inputs and outcome for the local evaluator panel.</summary>
public sealed record AccessRuleDryRunState(
    string RuleId,
    string PrincipalJson,
    string DocumentJson,
    string AuthMode,
    AccessRuleEvaluation? Result,
    string? Error);
