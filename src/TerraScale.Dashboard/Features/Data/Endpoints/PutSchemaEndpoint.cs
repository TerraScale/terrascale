using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data.Endpoints;

/// <summary>Validates and applies a proposed schema JSON — the SchemaViewer apply flow.</summary>
public sealed class PutSchemaEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/schema");
        Summary("Applies a proposed schema and starts its migration");
        Tags("Data");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var error = store.PutSchema(form["schema"].ToString());
        return Results.Redirect(
            "/schema" + (error is null ? "?applied=1" : "?schemaError=" + Uri.EscapeDataString(error)),
            false,
            false);
    }
}
