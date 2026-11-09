using System.Text;
using System.Text.Json;

namespace TerraScale.Dashboard.Features.Organizations.Audit
{
    // One audit event as the API, JSON and NDJSON exports and MCP list it. Ip is empty when the producer reported none (ts-auth events).
    public sealed record AuditEventRow(string Id, DateTimeOffset OccurredAt, string Actor, string ActorType, string Action, string Target, string TargetType,
        string Source, string Severity, string Outcome, string Ip)
    {
        public static AuditEventRow From(AuditRecord item)
        {
            return new(item.Id, item.OccurredAt, item.Actor, OrganizationFilters.AuditActorType(item), item.Action, item.Target,
            item.TargetType, item.Source, OrganizationFilters.AuditSeverity(item), OrganizationFilters.AuditOutcome(item), item.Ip ?? "");
        }
    }

    public static class OrganizationAudit
    {
        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        // How far back audit events are shown and exported: the plan's window, narrowed by the owner's retention setting. The dashboard keeps its own events; ts-auth keeps its
        // events at least 90 days, so a longer window lists the ts-auth events it still has.
        public static int RetentionDays(OrganizationRecord org)
        {
            return Math.Min(PlanRetentionDays(org), org.AuditRetentionDays ?? int.MaxValue);
        }

        private static int PlanRetentionDays(OrganizationRecord org)
        {
            return (org.InternalBilling is not null ? "internal" : PlanCatalog.Plans.FirstOrDefault(plan => plan.Id == org.PlanId)?.Key) switch
            {
                "starter" => 30,
                "pro" => 90,
                "team" or "internal" => 365,
                _ => 7
            };
        }

        public static string Csv(string value)
        {
            // Spreadsheet applications interpret formula prefixes even in quoted
            // CSV fields. Keep persisted user names and resource text as text.
            if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@')
            {
                value = "'" + value;
            }

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
