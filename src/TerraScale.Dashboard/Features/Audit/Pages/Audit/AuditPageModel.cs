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
    int Warn) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static AuditPageModel From(DashboardStore store, string href, string? search, string? action, string? severity, string? range)
    {
        var all = store.AuditEvents;
        var retained = DashboardStore.FilterAuditEvents([.. all], range: "90d", retentionDays: store.AuditRetentionDays);
        var filtered = DashboardStore.FilterAuditEvents(retained, action, severity, range, search, retentionDays: store.AuditRetentionDays);
        var actionOptions = new List<string> { "all" };
        actionOptions.AddRange(retained.Select(a => a.Action).Distinct().Order(StringComparer.Ordinal));

        return new(
            Rows: [.. filtered.Select(ToRow)],
            AllEvents: retained,
            ActionOptions: actionOptions,
            Search: search ?? "",
            Action: action is { Length: > 0 } && actionOptions.Contains(action) ? action : "all",
            Severity: DashboardStore.ParseAuditSeverity(severity),
            Range: DashboardStore.ParseAuditRange(range),
            Critical: CountSeverity(filtered, "critical"),
            Warn: CountSeverity(filtered, "warn"))
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
}
