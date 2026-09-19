using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

/// <summary>Result of compiling a CEL access rule: empty diagnostics when ok.</summary>
public sealed record AccessRuleCompileResult(
    bool Ok,
    string Source,
    ImmutableArray<AccessRuleDiagnostic> Diagnostics,
    AccessRuleIndexAnalysis Indexes);
