namespace TerraScale.Dashboard2.Features.Data;

public sealed record QueryTemplate(string Id, string Sql, int QueryCount, int LogicalCost, int TotalLogicalCost, int? P95Milliseconds);
