namespace TerraScale.Dashboard.Features.Usage;

public sealed record UsageShare(string Name, int Percent, string Of);
public sealed record SpendingFields(string Kind, string Prefix, string Id, long CapCents, TerraScale.Dashboard.Features.Accounts.OperationLimits Operations);
public sealed record SpendingField(string Id, string Name, string Label, string Hint, string Value, string InputMode = "decimal", string Pattern = @"\d+(\.\d{1,2})?");
