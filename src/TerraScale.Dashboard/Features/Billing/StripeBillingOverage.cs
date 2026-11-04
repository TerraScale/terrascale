using System.Globalization;
using Stripe;

namespace TerraScale.Dashboard.Features.Billing
{
    internal sealed record OverageKind(string Kind, decimal UnitAmountCents, Func<long, DateTimeOffset, string> Describe);

    public sealed partial class StripeBilling
    {
        internal static OverageKind OperationUnits(BillingPlan plan) => new(OperationUnitOverage.Kind, OperationUnitOverage.UnitAmountCents,
            (quantity, month) => string.Create(CultureInfo.InvariantCulture, $"Operation units over the {plan.Name} allowance, {month:MMMM yyyy}: {quantity:N0} × {OperationUnitOverage.Rate}"));

        internal static OverageKind Storage(BillingPlan plan) => new(StorageOverage.Kind, StorageOverage.UnitAmountCents,
            (quantity, month) => string.Create(CultureInfo.InvariantCulture, $"Storage over the {plan.Name} allowance, {month:MMMM yyyy}: {quantity:N0} MB-months"));

        internal async Task ReportOverageAsync(BillingBinding binding, OverageKind kind, long overage, DateTimeOffset month, CancellationToken cancellationToken)
        {
            var client = await ClientAsync(cancellationToken);
            var (invoiced, pending) = await OverageItemsAsync(client, binding, kind.Kind, month, cancellationToken);
            var remaining = Math.Max(0, overage - invoiced);
            var keep = remaining > 0 ? pending.FirstOrDefault(item => item.Parent?.SubscriptionDetails?.Subscription == binding.SubscriptionId) : null;
            foreach (var item in pending.Where(item => item != keep))
            {
                await client.V1.InvoiceItems.DeleteAsync(item.Id, cancellationToken: cancellationToken);
            }

            if (remaining == 0)
            {
                return;
            }

            var description = kind.Describe(remaining, month);
            if (keep is null)
            {
                if (binding.EffectiveInterval == "annual")
                {
                    await client.V1.Subscriptions.UpdateAsync(binding.SubscriptionId, new()
                    { PendingInvoiceItemInterval = new() { Interval = "month", IntervalCount = 1 } }, cancellationToken: cancellationToken);
                }

                var monthKey = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                await client.V1.InvoiceItems.CreateAsync(new()
                {
                    Customer = binding.CustomerId,
                    Subscription = binding.SubscriptionId,
                    Currency = "usd",
                    Quantity = remaining,
                    UnitAmountDecimal = kind.UnitAmountCents,
                    Description = description,
                    Metadata = new() { ["terrascale_kind"] = kind.Kind, ["terrascale_month"] = monthKey, ["terrascale_org"] = binding.OrganizationId }
                }, Idempotent($"{kind.Kind}-{binding.OrganizationId}-{monthKey}-{binding.SubscriptionId}-{invoiced}-{remaining}"), cancellationToken);
            }
            else if (keep.Quantity != remaining || keep.Description != description)
            {
                await client.V1.InvoiceItems.UpdateAsync(keep.Id, new() { Quantity = remaining, UnitAmountDecimal = kind.UnitAmountCents, Description = description }, cancellationToken: cancellationToken);
            }
        }

        public async Task<long> OperationUnitOverageAsync(BillingBinding binding, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var (invoiced, pending) = await OverageItemsAsync(await ClientAsync(cancellationToken), binding, OperationUnitOverage.Kind, new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero), cancellationToken);
            return invoiced + pending.Sum(item => item.Quantity);
        }

        private static async Task<(long Invoiced, List<InvoiceItem> Pending)> OverageItemsAsync(StripeClient client, BillingBinding binding, string kind, DateTimeOffset month, CancellationToken cancellationToken)
        {
            var monthKey = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            long invoiced = 0;
            var pending = new List<InvoiceItem>();
            await foreach (var item in client.V1.InvoiceItems.ListAutoPagingAsync(new()
            { Customer = binding.CustomerId, Created = new DateRangeOptions { GreaterThanOrEqual = month.UtcDateTime }, Limit = 100 }, cancellationToken: cancellationToken))
            {
                if (item.Metadata?.GetValueOrDefault("terrascale_kind") != kind || item.Metadata.GetValueOrDefault("terrascale_month") != monthKey ||
                    item.Metadata.GetValueOrDefault("terrascale_org") != binding.OrganizationId)
                {
                    continue;
                }

                if (item.InvoiceId is { Length: > 0 })
                {
                    invoiced = checked(invoiced + item.Quantity);
                }
                else
                {
                    pending.Add(item);
                }
            }
            return (invoiced, pending);
        }
    }
}
