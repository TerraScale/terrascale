using TerraScale.Dashboard.Common.Formatting;

namespace TerraScale.Dashboard.Features.Billing
{
    public static class OperationUnitOverage
    {
        public const string Kind = "operation_unit_overage";

        public static decimal UnitAmountCents => PlanCatalog.OperationUnitsPerMillionUsd * 100m / 1_000_000m;

        public static string Rate => MoneyFormat.Money(checked((long)(PlanCatalog.OperationUnitsPerMillionUsd * 100m))) + " per million";

        public static long Units(BillingPlan plan, long used)
        {
            return plan.PriceCents > 0 ? Math.Max(0, used - plan.Allowances.MonthlyOperations) : 0;
        }

        public static long Cents(long units)
        {
            return checked((long)Math.Round(units * UnitAmountCents, 0, MidpointRounding.AwayFromZero));
        }
    }

    public static class StorageOverage
    {
        public const string Kind = "storage_overage";

        public static decimal UnitAmountCents => PlanCatalog.StorageGbMonthUsd * 100m / 1_000m;

    }
}
