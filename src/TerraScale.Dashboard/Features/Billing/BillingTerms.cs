using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Dashboard.Features.Billing;

public sealed record BillingSubscription : IBillingRow
{
    public string? OriginalVersion { get; set; }

    public string Version { get; set; } = "";
    public required string OrganizationId { get; init; }
    public required string SubscriptionId { get; init; }
    public required string CustomerId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CheckedAt { get; set; }

    public bool Ended => Status is "canceled" or "incomplete_expired";

    public bool KnownThrough(DateTimeOffset end) => CheckedAt >= end || EndedAt is { } ended && ended <= CheckedAt;
}

public sealed record BillingTerms
{
    public const string StartDate = "start_date";
    public const string ProviderEvent = "event";
    public const string FirstCheck = "first_check";
    public const string Unestablished = "unestablished";

    public required string OrganizationId { get; init; }
    public required string SubscriptionId { get; init; }
    public required DateTimeOffset EffectiveFrom { get; init; }
    public required string Basis { get; init; }
    public required DateTimeOffset LearnedAt { get; init; }
    public required string Plan { get; init; }
    public required string PlanName { get; init; }
    public required long OperationsAllowance { get; init; }
    public required long StorageAllowance { get; init; }
    public required decimal OperationRate { get; init; }
    public required decimal StorageRate { get; init; }
    public required string Currency { get; init; }
    public required string PricingVersion { get; init; }

    public bool Priced => PricingVersion.Length > 0;

    public static BillingTerms For(string organizationId, string subscriptionId, DateTimeOffset effectiveFrom, string basis, DateTimeOffset learnedAt, BillingPlan? plan)
    {
        var operationRate = plan is null ? 0 : OperationUnitOverage.UnitAmountCents;
        var storageRate = plan is null ? 0 : StorageOverage.UnitAmountCents;
        var text = plan is null ? "" : string.Create(CultureInfo.InvariantCulture, $"{plan.Key}|{plan.Name}|{plan.Allowances.MonthlyOperations}|{plan.Allowances.StorageBytes}|{operationRate}|{storageRate}|usd");
        return new()
        {
            OrganizationId = organizationId,
            SubscriptionId = subscriptionId,
            EffectiveFrom = effectiveFrom,
            Basis = basis,
            LearnedAt = learnedAt,
            Plan = plan?.Key ?? "",
            PlanName = plan?.Name ?? "",
            OperationsAllowance = plan?.Allowances.MonthlyOperations ?? 0,
            StorageAllowance = plan?.Allowances.StorageBytes ?? 0,
            OperationRate = operationRate,
            StorageRate = storageRate,
            Currency = "usd",
            PricingVersion = plan is null ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
        };
    }
}
