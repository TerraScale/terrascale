using System.Text;
using System.Text.Json;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Audit
{
    public sealed class AuditExportEndpoint(DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, TimeProvider time) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/audit/export");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            if (!context.CanManage)
            {
                return Results.StatusCode(403);
            }

            var filter = OrganizationFilters.For(http);
            var snapshot = await access.ReadAsync(context, http.RequestAborted);
            var events = filter.ApplyAudit(snapshot.AuditEvents, snapshot.AuditRetentionDays);
            http.Response.Headers.CacheControl = "no-store";
            var name = "terrascale-audit-" + time.GetUtcNow().ToString("yyyy-MM-ddTHH-mm-ssZ");
            var format = http.Request.Query["format"].ToString();
            if (format == "json")
            {
                return Results.File(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    projectId = context.Project?.Id,
                    exportedAt = time.GetUtcNow(),
                    count = events.Count,
                    retentionDays = snapshot.AuditRetentionDays,
                    filters = new { filter.Action, filter.Search, filter.Range, filter.Severity },
                    events = events.Select(AuditEventRow.From)
                }, OrganizationAudit.Json), "application/json", name + ".json");
            }

            if (format == "ndjson")
            {
                var lines = new StringBuilder();
                foreach (var item in events)
                {
                    lines.Append(JsonSerializer.Serialize(AuditEventRow.From(item), OrganizationAudit.Json)).Append('\n');
                }

                return Results.File(Encoding.UTF8.GetBytes(lines.ToString()), "application/x-ndjson", name + ".ndjson");
            }
            var body = new StringBuilder("auditEventId,occurredAt,actor,actorType,action,target,source,severity,outcome,ip\r\n");
            foreach (var item in events.Select(AuditEventRow.From))
            {
                body.AppendJoin(',', new[] { item.Id, item.OccurredAt.ToString("O"), item.Actor, item.ActorType, item.Action, item.Target, item.Source,
                item.Severity, item.Outcome, item.Ip }.Select(OrganizationAudit.Csv)).Append("\r\n");
            }

            return Results.File(Encoding.UTF8.GetBytes(body.ToString()), "text/csv; charset=utf-8", name + ".csv");
        }
    }
}
