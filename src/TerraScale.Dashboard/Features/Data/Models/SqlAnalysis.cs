namespace TerraScale.Dashboard.Features.Data.Models;

public sealed record SqlAnalysis(
    bool Ok,
    string? Error,
    string? Collection,
    int ParameterSlots,
    bool HasLimit,
    string Explain,
    SqlSuggestion[] Suggestions)
{
    public static SqlAnalysis Invalid(string error) => new(false, error, null, 0, false, "", []);
}
