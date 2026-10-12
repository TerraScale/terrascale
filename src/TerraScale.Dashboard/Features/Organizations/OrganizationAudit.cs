using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Features.Organizations;

public static class OrganizationAudit
{
    public static IEndpointRouteBuilder MapOrganizationAudit(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/audit", (HttpContext http) => AuthEndpoints.SeeOther("/organization?tab=audit" + (http.Request.QueryString.HasValue ? "&" + http.Request.QueryString.Value![1..] : "")));
        endpoints.MapGet("/audit/export", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, TimeProvider time) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountEndpoints.LoginRedirect(http);
            if (!context.CanManage) return Results.StatusCode(403);
            var filter = OrganizationFilters.For(http);
            var snapshot = await access.ReadAsync(context, http.RequestAborted);
            var events = filter.ApplyAudit(snapshot.AuditEvents, snapshot.AuditRetentionDays);
            http.Response.Headers.CacheControl = "no-store";
            var name = "terrascale-audit-" + time.GetUtcNow().ToString("yyyy-MM-ddTHH-mm-ssZ");
            if (http.Request.Query["format"] == "json")
                return Results.File(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    projectId = context.Project?.Id,
                    exportedAt = time.GetUtcNow(),
                    count = events.Count,
                    filters = new { filter.Action, filter.Search, filter.Range, filter.Severity },
                    events
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json", name + ".json");
            var body = new StringBuilder("auditEventId,occurredAt,actor,actorType,action,target,source,severity,outcome\r\n");
            foreach (var item in events) body.AppendJoin(',', new[] { item.Id, item.OccurredAt.ToString("O"), item.Actor, OrganizationFilters.AuditActorType(item), item.Action, item.Target, item.Source,
                OrganizationFilters.AuditSeverity(item), OrganizationFilters.AuditOutcome(item) }.Select(Csv)).Append("\r\n");
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
