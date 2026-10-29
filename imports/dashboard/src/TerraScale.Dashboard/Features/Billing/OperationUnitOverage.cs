using TerraScale.Dashboard.Shared.Formatting;

namespace TerraScale.Dashboard.Features.Billing;

// The hourly Stripe reporter and the billing page share this overage math.
public static class OperationUnitOverage
{
    public const string Kind = "operation_unit_overage";

    // Stripe's unit_amount_decimal is in cents: $5 per million units is 0.0005 cents per unit.
    public static decimal UnitAmountCents => PlanCatalog.OperationUnitsPerMillionUsd * 100m / 1_000_000m;

    public static string Rate => MoneyFormat.Money(checked((long)(PlanCatalog.OperationUnitsPerMillionUsd * 100m))) + " per million";

    // Free has no overages: it stops new writes at its allowance.
    public static long Units(BillingPlan plan, long used) => plan.PriceCents > 0 ? Math.Max(0, used - plan.Allowances.MonthlyOperations) : 0;

    public static long Cents(long units) => checked((long)Math.Round(units * UnitAmountCents, 0, MidpointRounding.AwayFromZero));
}
