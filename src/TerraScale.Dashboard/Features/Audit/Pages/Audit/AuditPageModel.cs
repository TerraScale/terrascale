using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Audit.Pages.Audit;

public sealed record AuditPageModel(
    IReadOnlyList<AuditRow> Rows,
    IReadOnlyList<AuditEvent> AllEvents,
    IReadOnlyList<string> ActionOptions,
    string Search,
    string Action,
    string Severity,
    string Range,
    int Critical,
    int Warn,
    int Info,
    int FilteredCount,
    int Page,
    int PageCount) : IPageModel
{
    public const int PageSize = 10;

    public ShellModel Shell { get; init; } = null!;

    public static AuditPageModel From(DashboardStore store, string href, string? search, string? action, string? severity, string? range, int page = 1)
    {
        var all = store.AuditEvents;
        var retained = DashboardStore.FilterAuditEvents([.. all], range: "90d", retentionDays: store.AuditRetentionDays);
        var filtered = DashboardStore.FilterAuditEvents(retained, action, severity, range, search, retentionDays: store.AuditRetentionDays);
        var actionOptions = new List<string> { "all" };
        actionOptions.AddRange(retained.Select(a => a.Action).Distinct().Order(StringComparer.Ordinal));
        var pageCount = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
        var currentPage = Math.Clamp(page, 1, pageCount);
        var rows = filtered.Skip((currentPage - 1) * PageSize).Take(PageSize);

        return new(
            Rows: [.. rows.Select(ToRow)],
            AllEvents: retained,
            ActionOptions: actionOptions,
            Search: search ?? "",
            Action: action is { Length: > 0 } && actionOptions.Contains(action) ? action : "all",
            Severity: DashboardStore.ParseAuditSeverity(severity),
            Range: DashboardStore.ParseAuditRange(range),
            Critical: CountSeverity(filtered, "critical"),
            Warn: CountSeverity(filtered, "warn"),
            Info: CountSeverity(filtered, "info"),
            FilteredCount: filtered.Count,
            Page: currentPage,
            PageCount: pageCount)
        {
            Shell = ShellFactory.Create(store, href),
        };
    }

    private static AuditRow ToRow(AuditEvent auditEvent)
    {
        var severity = DashboardStore.AuditSeverity(auditEvent);
        return new(
            Id: auditEvent.Id,
            Time: auditEvent.OccurredAt,
            TimeLabel: DashboardStore.FormatUtcDateTime(auditEvent.OccurredAt),
            Actor: auditEvent.Actor,
            ActorType: DashboardStore.AuditActorType(auditEvent),
            Action: auditEvent.Action,
            ActionLabel: ActionLabel(auditEvent.Action),
            ActionIcon: ActionIcon(auditEvent.Action),
            Target: auditEvent.Target,
            Source: auditEvent.Source,
            Severity: severity,
            SeverityLabel: severity switch { "info" => "Info", "warn" => "Warning", _ => "Critical severity" },
            SeverityTone: severity switch { "critical" => "danger", "warn" => "warning", _ => "info" },
            Failure: DashboardStore.AuditIsFailure(auditEvent));
    }

    private static int CountSeverity(IReadOnlyList<AuditEvent> events, string severity)
    {
        var count = 0;
        foreach (var auditEvent in events)
        {
            if (DashboardStore.AuditSeverity(auditEvent) == severity)
            {
                count++;
            }
        }

        return count;
    }

    private static string ActionIcon(string action) =>
        action.Contains("key") ? "KeyRound"
        : action.Contains("auth") || action.Contains("login") ? "ShieldCheck"
        : action.Contains("policy") || action.Contains("role") ? "Shield"
        : "ScrollText";

    private static string ActionLabel(string action) => action switch
    {
        "api_key.created" => "API key created",
        "member.role_changed" => "Member role changed",
        "document.batch_upsert" => "Documents batch upserted",
        "database.deleted" => "Database deleted",
        "backup.restore_completed" => "Backup restore completed",
        _ => FormatActionLabel(action),
    };

    private static string FormatActionLabel(string action)
    {
        var words = action.Replace('.', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is 0
            ? action
            : string.Join(' ', words.Select((word, index) => index is 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word));
    }
}
