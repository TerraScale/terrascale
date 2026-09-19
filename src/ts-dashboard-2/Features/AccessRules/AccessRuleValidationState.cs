using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

/// <summary>Last "Validate" outcome for the rule editor: the edited (possibly
/// unsaved) name/source plus the compiler diagnostics and index analysis.</summary>
public sealed record AccessRuleValidationState(
    string RuleId,
    string Name,
    string Source,
    bool Ok,
    ImmutableArray<AccessRuleDiagnostic> Diagnostics,
    AccessRuleIndexAnalysis Indexes);
