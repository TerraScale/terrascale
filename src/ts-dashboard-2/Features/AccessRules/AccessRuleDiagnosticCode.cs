namespace TerraScale.Dashboard2;

/// <summary>Diagnostic codes emitted by the local CEL rule compiler
/// (1:1 with src/features/access-rules/access-rules-data.ts).</summary>
public enum AccessRuleDiagnosticCode
{
    UnexpectedToken,
    UnterminatedString,
    InvalidCharacter,
    MissingExpression,
    MissingClosingParen,
    UnknownFunction,
    InvalidExpression,
    NonBooleanResult,
}
