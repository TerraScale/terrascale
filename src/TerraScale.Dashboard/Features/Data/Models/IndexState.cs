namespace TerraScale.Dashboard.Features.Data.Models;

public sealed record IndexState(string Id, string Name, string[] Fields, string State, int? LogicalCost, int BackfillRemaining, string? BuildWatermark, string? MeasuredAt);
