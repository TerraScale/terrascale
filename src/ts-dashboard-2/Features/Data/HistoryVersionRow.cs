namespace TerraScale.Dashboard2.Features.Data;

public sealed record HistoryVersionRow(
    string Id,
    HistoryDot Dot,
    string Hlc,
    HistoryContext Context,
    string Kind,
    string Role,
    string? ValueJson,
    string Explanation);
