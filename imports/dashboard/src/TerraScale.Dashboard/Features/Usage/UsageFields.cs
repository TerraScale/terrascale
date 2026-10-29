namespace TerraScale.Dashboard.Features.Usage;

public sealed record UsageShare(string Name, double Percent, string Of);
public sealed record SpendingField(string Id, string Name, string Label, string Hint, string Value, string InputMode = "decimal", string Pattern = @"\d+(\.\d{1,2})?");
