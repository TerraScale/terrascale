namespace TerraScale.Dashboard2;

/// <summary>Index coverage for a compiled rule: which document fields the rule
/// references, which lack a backing index, and the recommended index names.</summary>
public sealed record AccessRuleIndexAnalysis(
    string[] Required,
    string[] Recommended,
    string[] ReferencedFields);
