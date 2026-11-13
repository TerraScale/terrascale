using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TerraScale.Dashboard.Features.Billing;

public sealed partial class StripeBilling
{
    internal void RecordLateOverage() => telemetry.ReportLateOverage();

    internal void RecordSkippedOverageMonth(string organizationId, DateTimeOffset month, string reason)
    {
        telemetry.ReportSkippedOverageMonth();
        logger.UsageOverageMonthSkipped(organizationId, month, reason);
    }

    internal async Task ReportOverageAsync(OverageCharge charge, CancellationToken cancellationToken)
    {
        if (charge.AmountCents == 0 || charge.ProviderId is not null) { return; }
        var client = await ClientAsync(cancellationToken);
        string? providerId = null;
        await foreach (var item in client.V1.InvoiceItems.ListAutoPagingAsync(new() { Customer = charge.CustomerId, Limit = 100 }, cancellationToken: cancellationToken))
        {
            if (item.Metadata?.GetValueOrDefault("terrascale_charge") == charge.Id)
            {
                if (item.CustomerId != charge.CustomerId || item.Currency != charge.Currency || item.Quantity != charge.Quantity || item.Amount != charge.AmountCents)
                {
                    throw new IOException("The provider charge does not match its frozen ledger row.");
                }
                if (providerId is not null && providerId != item.Id) { throw new IOException("Multiple provider objects identify one charge."); }
                providerId = item.Id;
            }
        }
        if (providerId is null)
        {
            var binding = await storage.BindingRowAsync(charge.OrganizationId, cancellationToken);
            if (binding is null || binding.State == "deleting" || binding.ErasureId is not null) { return; }
            if (binding.EffectiveInterval == "annual")
            {
                await client.V1.Subscriptions.UpdateAsync(charge.SubscriptionId, new() { PendingInvoiceItemInterval = new() { Interval = "month", IntervalCount = 1 } }, cancellationToken: cancellationToken);
            }
            var item = await client.V1.InvoiceItems.CreateAsync(new()
            {
                Customer = charge.CustomerId,
                Subscription = charge.SubscriptionId,
                Currency = charge.Currency,
                Quantity = charge.Quantity,
                UnitAmountDecimal = charge.UnitRate,
                Description = charge.Description,
                Metadata = new()
                {
                    ["terrascale_charge"] = charge.Id,
                    ["terrascale_kind"] = charge.Meter,
                    ["terrascale_month"] = charge.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    ["terrascale_org"] = charge.OrganizationId
                }
            }, Idempotent("overage-" + charge.Id), cancellationToken);
            providerId = item.Id;
        }
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var stored = await db.OverageCharges.SingleAsync(row => row.Id == charge.Id, cancellationToken);
        if (stored.ProviderId is not null)
        {
            if (stored.ProviderId != providerId) { throw new IOException("The provider charge identity changed."); }
            return;
        }
        stored.ProviderId = providerId;
        stored.AcceptedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

}
