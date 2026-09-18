using System.Collections.Immutable;
using System.Text;

namespace TerraScale.Dashboard2;

// Audit view mapping + the audit-export filter/serialize contract (1:1 with
// src/features/audit/audit-export.ts). Extends the seeded store.
public sealed partial class DashboardStore
{
    public const int AuditRetentionDays = 90;

    public static readonly string[] AuditRangeOptions = ["24h", "7d", "30d", "90d"];
    public static readonly string[] AuditSeverityOptions = ["all", "info", "warn", "critical"];

    /// <summary>Maps the seeded risk levels onto the audit severities the
    /// dashboard renders: low → info, medium → warn, high → critical.</summary>
    public static string AuditSeverity(AuditEvent auditEvent) => auditEvent.Risk switch
    {
        "high" => "critical",
        "medium" => "warn",
        _ => "info",
    };

    public static bool AuditIsFailure(AuditEvent auditEvent) =>
        auditEvent.Action.Contains("delete", StringComparison.OrdinalIgnoreCase);

    public static string AuditActorType(AuditEvent auditEvent) =>
        auditEvent.Actor.Contains('(') || auditEvent.Actor is "system" ? "service" : "user";

    public static string ParseAuditRange(string? value) =>
        AuditRangeOptions.Contains(value) ? value! : "7d";

    public static string ParseAuditSeverity(string? value) =>
        AuditSeverityOptions.Contains(value) ? value! : "all";

    /// <summary>Port of filterAuditEvents: action, severity, range cutoff, and
    /// free-text search across actor/action/target/source/severity/time.</summary>
    public static IReadOnlyList<AuditEvent> FilterAuditEvents(
        IReadOnlyList<AuditEvent> events,
        string? action = null,
        string? severity = null,
        string? range = null,
        string? search = null,
        DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var cutoff = ParseAuditRange(range) switch
        {
            "24h" => current.AddDays(-1),
            "30d" => current.AddDays(-30),
            "90d" => current.AddDays(-90),
            _ => current.AddDays(-7),
        };

        var term = (search ?? "").Trim().ToLowerInvariant();
        return [.. events.Where(auditEvent =>
        {
            if (action is { Length: > 0 } && auditEvent.Action != action)
            {
                return false;
            }

            if (severity is "info" or "warn" or "critical" && AuditSeverity(auditEvent) != severity)
            {
                return false;
            }

            if (DateTimeOffset.TryParse(auditEvent.OccurredAt, out var occurred) && occurred < cutoff)
            {
                return false;
            }

            return term.Length is 0
                || $"{auditEvent.Actor} {auditEvent.Action} {auditEvent.Target} {auditEvent.Source} {AuditSeverity(auditEvent)} {auditEvent.OccurredAt}"
                    .Contains(term, StringComparison.OrdinalIgnoreCase);
        })];
    }

    /// <summary>Port of serializeAuditEventsAsCsv.</summary>
    public static string SerializeAuditEventsAsCsv(IReadOnlyList<AuditEvent> events)
    {
        var builder = new StringBuilder("auditEventId,occurredAt,actor,actorType,action,target,source,severity,outcome\n");
        foreach (var auditEvent in events)
        {
            var cells = new[]
            {
                auditEvent.Id,
                auditEvent.OccurredAt,
                auditEvent.Actor,
                AuditActorType(auditEvent),
                auditEvent.Action,
                auditEvent.Target,
                auditEvent.Source,
                AuditSeverity(auditEvent),
                AuditIsFailure(auditEvent) ? "failure" : "success",
            };
            builder.AppendLine(string.Join(",", cells.Select(CsvEscape)));
        }

        return builder.ToString();
    }

    private static string CsvEscape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
