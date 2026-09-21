namespace TerraScale.Dashboard.Features.Data;

public sealed record IndexRecommendation(string Id, string[] Fields, string Reason, int? ExpectedCostReduction, string State);
