namespace TerraScale.Dashboard;

public sealed record UsageCycle(
    string Period,
    long StorageMb,
    long StorageLimitMb,
    long Requests,
    long RequestLimit,
    long ReadUnits,
    long WriteUnits,
    string InvoiceStatus,   // Paid | Pending | None
    decimal OverrunEstimateUsd);
