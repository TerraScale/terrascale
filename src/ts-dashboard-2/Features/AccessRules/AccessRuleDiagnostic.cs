namespace TerraScale.Dashboard2;

/// <summary>A single parser/evaluator diagnostic with 1-based source position.</summary>
public sealed record AccessRuleDiagnostic(
    AccessRuleDiagnosticCode Code,
    string Message,
    int Line,
    int Column);
