using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Audit
{
    public sealed class AuditEventsEndpoint(DashboardSessions sessions, AccountService accounts, IOrganizationAccess access) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/api/v1/dashboard/audit-events").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (!AccountRequest.TryResolveApi(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            if (!context.CanManage)
            {
                return TypedResults.StatusCode(403);
            }

            var filter = OrganizationFilters.For(http);
            var snapshot = await access.ReadAuditAsync(context, filter, http.RequestAborted);
            var events = filter.ApplyAudit(snapshot.AuditEvents, snapshot.AuditRetentionDays);
            var pageSize = int.TryParse(http.Request.Query["pageSize"], out var size) ? Math.Clamp(size, 1, 100) : 50;
            var pages = Math.Max(1, (events.Count + pageSize - 1) / pageSize);
            var page = Math.Min(filter.Page, pages);
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Json(new
            {
                retentionDays = snapshot.AuditRetentionDays,
                total = events.Count,
                omitted = snapshot.AuditOmitted,
                page,
                pageCount = pages,
                filters = new { filter.Action, filter.Search, filter.Range, filter.Severity },
                events = events.Skip((page - 1) * pageSize).Take(pageSize).Select(AuditEventRow.From)
            }, OrganizationAudit.Json);
        }
    }
}
