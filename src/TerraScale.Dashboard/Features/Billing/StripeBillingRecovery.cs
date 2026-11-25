using Dapper;
using System.Data;
using Stripe;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing;

public sealed record ReceiptSettled;

public sealed record ReceiptBusy;

public readonly union ReceiptRedelivery(StripeEventReceipt, ReceiptSettled, ReceiptBusy);

public sealed partial class StripeBilling
{
    public const int MaxReceiptAttempts = 12;
    private const int ReceiptsPerPass = 100;
    private static readonly TimeSpan ReceiptLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstReceiptRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestReceiptRetry = TimeSpan.FromHours(6);
    private const string PendingReceipts = DashboardSql.StripeEventReceipts.Select
        + " WHERE completed_at IS NULL AND processing_until <= @Now ORDER BY processing_until LIMIT @Limit";
    private const string ReceiptById = DashboardSql.StripeEventReceipts.Select + " WHERE id = @Id";
    private const string CleanReceipts = "DELETE FROM stripe_event_receipts WHERE received_at < @Cutoff AND processing_until < @Now";

    public async Task<OrganizationRecord> ResolveExpiredGraceAsync(string organizationId, CancellationToken cancellationToken)
    {
        var row = await organizations.FindAsync(organizationId, cancellationToken) ?? throw new IOException("Billing organization is unavailable.");
        if (row.Erased) { throw new IOException("Billing organization was erased."); }
        if (row.Organization.BillingStatus != "active" || row.Organization.PaymentGraceUntil is not { } deadline || deadline > time.GetUtcNow()) { return row.Organization; }
        try { await RefreshAsync(organizationId, cancellationToken); }
        catch (Exception error) when (error is DBConcurrencyException or StripeException or HttpRequestException or IOException or TerraBaseException or TimeoutException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
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
        List<StripeEventReceipt> pending;
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            pending = (await connection.QueryAsync<StripeEventReceipt>(PendingReceipts, new { Now = time.GetUtcNow(), Limit = ReceiptsPerPass })).AsList();
        }

        foreach (var receipt in pending)
        {
            receipt.ProcessingUntil = time.GetUtcNow() + ReceiptLease;
            if (await storage.ReplaceAsync(receipt, cancellationToken))
            {
                await ProcessReceiptAsync(receipt, cancellationToken);
            }
        }
    }

    private async Task<ReceiptRedelivery> ClaimRedeliveryAsync(string eventId, CancellationToken cancellationToken)
    {
        var stored = await ReceiptAsync(eventId, cancellationToken);
        if (stored is null || stored.CompletedAt is not null)
        {
            return new ReceiptSettled();
        }

        if (stored.ProcessingUntil > time.GetUtcNow())
        {
            return new ReceiptBusy();
        }

        stored.ProcessingUntil = time.GetUtcNow() + ReceiptLease;
        return await storage.ReplaceAsync(stored, cancellationToken) ? stored : new ReceiptBusy();
    }

    private async Task<StripeEventReceipt?> ReceiptAsync(string eventId, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<StripeEventReceipt>(ReceiptById, new { Id = eventId });
    }

    private async Task<Exception?> ProcessReceiptAsync(StripeEventReceipt receipt, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyEventReceiptAsync(receipt, cancellationToken);
            return null;
        }
        catch (Exception error) when (error is DBConcurrencyException or StripeException or HttpRequestException or IOException or TerraBaseException or TimeoutException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return await FailReceiptAsync(receipt, error);
        }
    }

    private async Task<Exception> FailReceiptAsync(StripeEventReceipt receipt, Exception error)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (await ReceiptAsync(receipt.Id, cleanup.Token) is not { CompletedAt: null } current || current.Version != receipt.Version)
        {
            return error;
        }

        var now = time.GetUtcNow();
        current.Attempts = checked(current.Attempts + 1);
        var deadLettered = current.Attempts >= MaxReceiptAttempts;
        current.ProcessingUntil = deadLettered ? now : now + ReceiptRetry(current.Attempts);
        current.CompletedAt = deadLettered ? now : null;
        current.DeadLetteredAt = deadLettered ? now : null;
        if (!await storage.ReplaceAsync(current, cleanup.Token))
        {
            return error;
        }

        if (deadLettered)
        {
            telemetry.ReportDeadLetteredStripeEvent();
            BillingLog.ReceiptDeadLettered(logger, current.Id, current.EventType, current.Attempts, error.GetType().Name);
        }
        else
        {
            BillingLog.ReceiptRetryScheduled(logger, current.Id, current.EventType, current.Attempts, error.GetType().Name, current.ProcessingUntil);
        }

        return error;
    }

    internal static TimeSpan ReceiptRetry(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        var delay = FirstReceiptRetry * Math.Pow(2, Math.Min(attempts - 1, 20));
        return delay < LongestReceiptRetry ? delay : LongestReceiptRetry;
    }

    public async Task CleanEventReceiptsAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-30);
        var now = time.GetUtcNow();
        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(CleanReceipts, new { Cutoff = cutoff, Now = now });
    }
}
