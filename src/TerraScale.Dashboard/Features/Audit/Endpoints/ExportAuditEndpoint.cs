using System.Text;
using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Audit.Endpoints;

/// <summary>GET /audit/export — CSV or JSON export of the filtered audit view
/// (parity with AuditExportButton + serializeAuditEventsAs{Csv,Json}).</summary>
public sealed class ExportAuditEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/audit/export");
        Summary("Exports filtered audit events as CSV or JSON");
        Tags("Audit");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var format = query["format"].FirstOrDefault() is "json" ? "json" : "csv";
        var action = query["action"].FirstOrDefault();
        var search = (query["search"].FirstOrDefault() ?? "").Trim();
        var range = DashboardStore.ParseAuditRange(query["range"].FirstOrDefault());
        var severity = DashboardStore.ParseAuditSeverity(query["severity"].FirstOrDefault());
        var filtered = DashboardStore.FilterAuditEvents(
            [.. store.AuditEvents],
            action: action,
            severity: severity,
            range: range,
            search: search);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var projectSegment = store.ActiveProjectId.Length is 0 ? "current-project" : store.ActiveProjectId;
        HttpContext.Response.Headers.ContentDisposition = $"attachment; filename=terrascale-audit-{projectSegment}-{timestamp}.{format}";

        if (format == "json")
        {
            var filters = new Dictionary<string, object>();
            if (action is { Length: > 0 } && action != "all")
            {
                filters["action"] = action;
            }

            if (search.Length > 0)
            {
                filters["search"] = search;
            }

            filters["range"] = range;
            filters["severity"] = severity;

            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                projectId = store.ActiveProjectId,
                exportedAt = DateTimeOffset.UtcNow,
                count = filtered.Count,
                filters,
                events = filtered,
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Web) { WriteIndented = true });
            return Task.FromResult<IResult>(Results.Text(json + "\n", "application/json", Encoding.UTF8));
        }

        var csv = DashboardStore.SerializeAuditEventsAsCsv(filtered);
        return Task.FromResult<IResult>(Results.Text(csv, "text/csv", Encoding.UTF8));
    }
}
