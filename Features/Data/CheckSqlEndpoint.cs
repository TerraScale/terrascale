using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

/// <summary>Server-side read-only SQL validation for the SQL console view,
/// mirroring validateExplorerSqlAction.</summary>
public sealed class CheckSqlEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/data/sql/check");
        Summary("Validates a read-only SQL statement");
        Tags("Data");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var statement = form["sql"].ToString();
        var collection = form["collection"].ToString();
        var analysis = SqlAnalyzer.Analyze(statement);
        return Results.Redirect(
            $"/data?view=sql&sql={Uri.EscapeDataString(statement)}&collection={Uri.EscapeDataString(collection)}" +
            (analysis.Ok ? "&checked=1" : ""),
            false,
            false);
    }
}
