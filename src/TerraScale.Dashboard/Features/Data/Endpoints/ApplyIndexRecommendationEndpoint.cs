using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data.Endpoints;

public sealed class ApplyIndexRecommendationEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/indexes/recommendations/apply");
        Summary("Applies an index recommendation and starts its build");
        Tags("Data");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var applied = store.ApplyIndexRecommendation(form["recommendationId"].ToString());
        return Results.Redirect("/indexes" + (applied ? "" : "?applyError=1"), false, false);
    }
}
