namespace TerraScale.Dashboard2.Features.Data;

public sealed record DocumentHistoryState(
    string Database,
    string Collection,
    string DocumentId,
    string MergePolicy,
    int RetentionDays,
    bool LimitedByRetention,
    HistoryVersionRow? Winner,
    HistoryVersionRow[] Versions,
    HistoryVersionRow[] Losers);
