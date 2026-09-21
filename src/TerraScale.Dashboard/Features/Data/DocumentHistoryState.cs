namespace TerraScale.Dashboard.Features.Data;

/// <summary>v1's documents.history capability routes every /history request into
/// the error page; the port keeps only the error + retention surface.</summary>
public sealed record DocumentHistoryState(
    string Message,
    int RetentionDays,
    bool LimitedByRetention);
