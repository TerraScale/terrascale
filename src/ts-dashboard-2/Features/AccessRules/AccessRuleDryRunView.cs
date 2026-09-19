namespace TerraScale.Dashboard2.Features.AccessRules;

/// <summary>Dry-run panel: echoed inputs plus the evaluator outcome.</summary>
public sealed record AccessRuleDryRunView(
    string RuleId,
    string PrincipalJson,
    string DocumentJson,
    string AuthMode,
    AccessRuleEvaluation? Result,
    string? Error);
