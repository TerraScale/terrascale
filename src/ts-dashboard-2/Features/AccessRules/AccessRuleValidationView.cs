using System.Collections.Immutable;

namespace TerraScale.Dashboard2.Features.AccessRules;

/// <summary>Validation panel: compiler diagnostics plus index analysis.</summary>
public sealed record AccessRuleValidationView(
    bool Ok,
    ImmutableArray<AccessRuleDiagnostic> Diagnostics,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyList<string> RecommendedIndexes)
{
    /// <summary>Diagnostic code in the original kebab-case form (e.g. "unexpected-token").</summary>
    public static string FormatCode(AccessRuleDiagnosticCode code) =>
        string.Concat(code.ToString().Select((c, i) =>
            i > 0 && char.IsUpper(c) ? $"-{char.ToLowerInvariant(c)}" : char.ToLowerInvariant(c).ToString()));
}
