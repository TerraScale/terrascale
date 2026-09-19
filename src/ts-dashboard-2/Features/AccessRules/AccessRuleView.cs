namespace TerraScale.Dashboard2.Features.AccessRules;

/// <summary>Rule row for the rules table.</summary>
public sealed record AccessRuleView(
    string Id,
    string Name,
    string Source,
    string Generation,
    bool IsValid,
    int DiagnosticCount,
    IReadOnlyList<string> ReferencedFields)
{
    public string StatusLabel => IsValid ? "Valid" : $"{DiagnosticCount} diagnostic{(DiagnosticCount == 1 ? "" : "s")}";
}
