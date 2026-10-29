using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;

namespace TerraScale.Dashboard.Features.Organizations.Audit;

// One audit event as the API, JSON and NDJSON exports and MCP list it. Ip is empty when the producer reported none (ts-auth events).
public sealed record AuditEventRow(string Id, DateTimeOffset OccurredAt, string Actor, string ActorType, string Action, string Target, string TargetType,
    string Source, string Severity, string Outcome, string Ip)
{
    public static AuditEventRow From(AuditRecord item) => new(item.Id, item.OccurredAt, item.Actor, OrganizationFilters.AuditActorType(item), item.Action, item.Target,
        item.TargetType, item.Source, OrganizationFilters.AuditSeverity(item), OrganizationFilters.AuditOutcome(item), item.Ip ?? "");
}

public static class OrganizationAudit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // How far back audit events are shown and exported: the plan's window, narrowed by the owner's retention setting. The dashboard keeps its own events; ts-auth keeps its
    // events at least 90 days, so a longer window lists the ts-auth events it still has.
    public static int RetentionDays(OrganizationRecord org) => Math.Min(PlanRetentionDays(org), org.AuditRetentionDays ?? int.MaxValue);

    private static int PlanRetentionDays(OrganizationRecord org) =>
        (org.InternalBilling is not null ? "internal" : PlanCatalog.Plans.FirstOrDefault(plan => plan.Id == org.PlanId)?.Key) switch
        {
            "starter" => 30,
            "pro" => 90,
            "team" or "internal" => 365,
            _ => 7
        };

    public static IEndpointRouteBuilder MapOrganizationAudit(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/audit", (HttpContext http) => HttpRedirects.SeeOther("/organization?tab=audit" + (http.Request.QueryString.HasValue ? "&" + http.Request.QueryString.Value![1..] : "")));
        endpoints.MapGet("/api/v1/dashboard/audit-events", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            if (!context.CanManage) return Results.StatusCode(403);
            var filter = OrganizationFilters.For(http);
            var snapshot = await access.ReadAsync(context, http.RequestAborted);
            var events = filter.ApplyAudit(snapshot.AuditEvents, snapshot.AuditRetentionDays);
            var pageSize = int.TryParse(http.Request.Query["pageSize"], out var size) ? Math.Clamp(size, 1, 100) : 50;
            var pages = Math.Max(1, (events.Count + pageSize - 1) / pageSize);
            var page = Math.Min(filter.Page, pages);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                retentionDays = snapshot.AuditRetentionDays,
                total = events.Count,
                page,
                pageCount = pages,
                filters = new { filter.Action, filter.Search, filter.Range, filter.Severity },
                events = events.Skip((page - 1) * pageSize).Take(pageSize).Select(AuditEventRow.From)
            }, Json);
        });
        endpoints.MapGet("/audit/export", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, TimeProvider time) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            if (!context.CanManage) return Results.StatusCode(403);
            var filter = OrganizationFilters.For(http);
            var snapshot = await access.ReadAsync(context, http.RequestAborted);
            var events = filter.ApplyAudit(snapshot.AuditEvents, snapshot.AuditRetentionDays);
            http.Response.Headers.CacheControl = "no-store";
            var name = "terrascale-audit-" + time.GetUtcNow().ToString("yyyy-MM-ddTHH-mm-ssZ");
            var format = http.Request.Query["format"].ToString();
            if (format == "json")
                return Results.File(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    projectId = context.Project?.Id,
                    exportedAt = time.GetUtcNow(),
                    count = events.Count,
                    retentionDays = snapshot.AuditRetentionDays,
                    filters = new { filter.Action, filter.Search, filter.Range, filter.Severity },
                    events = events.Select(AuditEventRow.From)
                }, Json), "application/json", name + ".json");
            if (format == "ndjson")
            {
                var lines = new StringBuilder();
                foreach (var item in events) lines.Append(JsonSerializer.Serialize(AuditEventRow.From(item), Json)).Append('\n');
                return Results.File(Encoding.UTF8.GetBytes(lines.ToString()), "application/x-ndjson", name + ".ndjson");
            }
            var body = new StringBuilder("auditEventId,occurredAt,actor,actorType,action,target,source,severity,outcome,ip\r\n");
            foreach (var item in events.Select(AuditEventRow.From)) body.AppendJoin(',', new[] { item.Id, item.OccurredAt.ToString("O"), item.Actor, item.ActorType, item.Action, item.Target, item.Source,
                item.Severity, item.Outcome, item.Ip }.Select(Csv)).Append("\r\n");
            return Results.File(Encoding.UTF8.GetBytes(body.ToString()), "text/csv; charset=utf-8", name + ".csv");
        });
        return endpoints;
    }
    public static string Csv(string value)
    {
        // Spreadsheet applications interpret formula prefixes even in quoted
        // CSV fields. Keep persisted user names and resource text as text.
        if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
