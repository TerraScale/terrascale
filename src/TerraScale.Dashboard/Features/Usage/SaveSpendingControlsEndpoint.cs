using System.Text.RegularExpressions;
using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Usage;

/// <summary>POST /api/v1/dashboard/usage/spending-controls — saves the paid-excess
/// consent checkbox and the monthly organization spending cap.</summary>
public sealed partial class SaveSpendingControlsEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
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
        var cap = form["spendCap"].ToString().Trim();
        var dbCaps = form.Where(pair => pair.Key.StartsWith("dbCap_", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key["dbCap_".Length..], pair => pair.Value.ToString().Trim());

        // v1 client validation: blank, or a non-negative dollar amount with at most two decimals.
        if (!IsValidCap(cap) || !dbCaps.Values.All(IsValidCap))
        {
            return Results.Redirect("/usage?saved=invalid", false, false);
        }

        var enabled = form["paidExcess"] == "true";
        store.UpdateSpendingControls(enabled, cap.Length is 0 ? null : cap);
        store.UpdateDatabaseSpendCaps(dbCaps);
        return Results.Redirect("/usage?saved=ok", false, false);
    }

    private static bool IsValidCap(string value) => value.Length is 0 || CapPattern().IsMatch(value);

    [GeneratedRegex(@"^\d+(\.\d{1,2})?$")]
    private static partial Regex CapPattern();
}
