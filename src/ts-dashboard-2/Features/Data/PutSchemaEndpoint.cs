using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

/// <summary>Validates a proposed schema JSON, computes the typed diff, and
/// previews the migration — the SchemaViewer apply flow.</summary>
public sealed class PutSchemaEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/schema");
        Summary("Validates a proposed schema and previews the migration diff");
        Tags("Data");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var error = store.PutSchema(form["schema"].ToString());
        return Results.Redirect(
            "/schema" + (error is null ? "?diff=1" : "?schemaError=" + Uri.EscapeDataString(error)),
            false,
            false);
    }
}
