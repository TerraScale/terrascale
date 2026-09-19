using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Usage;

/// <summary>POST /api/v1/dashboard/usage/spending-controls — saves the paid-excess
/// consent checkbox and the monthly organization spending cap.</summary>
public sealed class SaveSpendingControlsEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/usage/spending-controls");
        Summary("Saves organization spending controls");
        Tags("Usage");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var enabled = form["paidExcess"] == "true";
        var cap = form["spendCap"].ToString().Trim();
        store.UpdateSpendingControls(enabled, cap.Length is 0 ? null : cap);
        var dbCaps = form.Where(pair => pair.Key.StartsWith("dbCap_", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key["dbCap_".Length..], pair => pair.Value.ToString());
        store.UpdateDatabaseSpendCaps(dbCaps);
        return Results.Redirect("/usage", false, false);
    }
}
