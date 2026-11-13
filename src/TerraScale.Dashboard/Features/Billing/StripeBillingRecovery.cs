using Microsoft.EntityFrameworkCore;
using Stripe;
using TerraScale.TerraBase.Contracts;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing;

public sealed partial class StripeBilling
{
    public async Task<OrganizationRecord> ResolveExpiredGraceAsync(string organizationId, CancellationToken cancellationToken)
    {
        var row = await organizations.FindAsync(organizationId, cancellationToken) ?? throw new IOException("Billing organization is unavailable.");
        if (row.Erased) { throw new IOException("Billing organization was erased."); }
        if (row.Organization.BillingStatus != "active" || row.Organization.PaymentGraceUntil is not { } deadline || deadline > time.GetUtcNow()) { return row.Organization; }
        try { await RefreshAsync(organizationId, cancellationToken); }
        catch (Exception error) when (error is DbUpdateException or StripeException or HttpRequestException or IOException or TerraBaseException or TimeoutException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            telemetry.ReportDeferredGraceExpiry();
            while (true)
            {
                var binding = await storage.BindingRowAsync(organizationId, cancellationToken) ?? throw new IOException("Billing state is unavailable.");
                var now = time.GetUtcNow();
                binding.ExpiryUnavailableSince ??= now;
                var log = now - binding.ExpiryUnavailableSince > TimeSpan.FromHours(24) &&
                    (binding.ExpiryLastLoggedAt is null || binding.ExpiryLastLoggedAt.Value.UtcDateTime.Date < now.UtcDateTime.Date);
                if (log) { binding.ExpiryLastLoggedAt = now; }
                if (!await storage.ReplaceAsync(binding, cancellationToken)) { continue; }
                if (log) { BillingLog.ExpiryDeferred(logger); }
                break;
            }
        }
        var fresh = await organizations.FindAsync(organizationId, cancellationToken) ?? throw new IOException("Billing organization is unavailable.");
        return fresh.Erased ? throw new IOException("Billing organization was erased.") : fresh.Organization;
    }

    private async Task RepairTaxAsync(StripeClient client, string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            await client.V1.Subscriptions.UpdateAsync(subscriptionId, new() { AutomaticTax = new() { Enabled = true } }, cancellationToken: cancellationToken);
        }
        catch (Exception error) when (error is StripeException or HttpRequestException or IOException or TimeoutException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            telemetry.ReportFailedTaxRepair();
            BillingLog.TaxRepairFailed(logger, error.GetType().Name);
        }
    }

    private async Task ApplyEventReceiptAsync(StripeEventReceipt receipt, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(4));
        var binding = await ReadAsync(receipt.OrganizationId, budget.Token);
        if (binding is not null && binding.State != "deleting" && (receipt.SubscriptionId.Length == 0 || binding.SubscriptionId == receipt.SubscriptionId))
        {
            if (receipt.InvoiceId.Length > 0)
            {
                await ApplyPaymentAsync(binding, receipt.InvoiceId, receipt.EventType, receipt.EventCreated.UtcDateTime, budget.Token);
            }
            await RefreshAsync(receipt.OrganizationId, budget.Token);
        }
        receipt.CompletedAt = time.GetUtcNow();
        if (!await storage.ReplaceAsync(receipt, budget.Token)) { throw new IOException("Stripe receipt changed during processing."); }
    }

    public async Task RecoverEventReceiptsAsync(CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow();
        var pending = await db.StripeEventReceipts.AsNoTracking().Where(row => row.CompletedAt == null && row.ProcessingUntil <= now).ToListAsync(cancellationToken);
        foreach (var receipt in pending)
        {
            receipt.ProcessingUntil = now.AddMinutes(5);
            if (!await storage.ReplaceAsync(receipt, cancellationToken)) { continue; }
            try { await ApplyEventReceiptAsync(receipt, cancellationToken); }
            catch (Exception error) when (error is DbUpdateException or StripeException or HttpRequestException or IOException or TerraBaseException or TimeoutException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                receipt.ProcessingUntil = time.GetUtcNow();
                await storage.ReplaceAsync(receipt, cancellationToken);
                logger.AStripeBindingCouldNotBeRefreshed(error.GetType().Name);
            }
        }
    }

    public async Task CleanEventReceiptsAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-30);
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow();
        await db.StripeEventReceipts.Where(row => row.ReceivedAt < cutoff && row.ProcessingUntil < now).ExecuteDeleteAsync(cancellationToken);
    }
}
