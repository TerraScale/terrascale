using System.Text.Json;

namespace TerraScale.Dashboard2;

/// <summary>Context safe for local evaluation. It contains claims, never a
/// bearer token. Auth is a JSON object such as {"mode":"publishable-key"}.</summary>
public sealed record AccessRuleEvaluationContext(
    JsonElement Principal,
    JsonElement Document,
    JsonElement Auth);
