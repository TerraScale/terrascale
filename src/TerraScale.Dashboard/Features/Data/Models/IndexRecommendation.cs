namespace TerraScale.Dashboard.Features.Data.Models;

public sealed record IndexRecommendation(string Id, string[] Fields, string Reason, int? ExpectedCostReduction, string State);
