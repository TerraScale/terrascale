using System.Text;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Audit;

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
        var filtered = DashboardStore.FilterAuditEvents(
            [.. store.AuditEvents],
            action: query["action"].FirstOrDefault(),
            severity: query["severity"].FirstOrDefault(),
            range: query["range"].FirstOrDefault(),
            search: query["search"].FirstOrDefault());

        if (format == "json")
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                projectId = store.ActiveProjectId,
                exportedAt = DateTimeOffset.UtcNow,
                count = filtered.Count,
                events = filtered,
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Web) { WriteIndented = true });
            return Task.FromResult<IResult>(Results.Text(json + "\n", "application/json", Encoding.UTF8));
        }

        var csv = DashboardStore.SerializeAuditEventsAsCsv(filtered);
        return Task.FromResult<IResult>(Results.Text(csv, "text/csv", Encoding.UTF8));
    }
}
