using TerraScale.Dashboard.Platform.Persistence;
using Dapper;
using System.Diagnostics;
using System.Globalization;
using Stripe;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing;

internal abstract record class OverageOutcome;
internal sealed record class ChargedAtProvider(string ProviderId, long AmountCents) : OverageOutcome;
internal sealed record class AdoptedFromPreviousReporter(string ProviderId, long AmountCents) : OverageOutcome;
internal sealed record class MismatchedAtProvider(string ProviderId, long AmountCents) : OverageOutcome;
internal sealed record class RetiredWithoutCharge(string Reason) : OverageOutcome;
internal sealed record class Released : OverageOutcome;

public sealed partial class StripeBilling
{
    internal static readonly TimeSpan DispatchClaim = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DispatchAttempt = TimeSpan.FromMinutes(4);

    internal void RecordLateOverage() => telemetry.ReportLateOverage();

    internal void RecordSkippedOverageMeter(DateTimeOffset month, string reason)
    {
        telemetry.ReportSkippedOverageMeter();
        logger.UsageOverageMonthSkipped(month, reason);
    }

    internal void RecordWaitingOverageMonths(long count, DateTimeOffset? oldest) => telemetry.ReportWaitingOverageMonths(count, oldest);

    private const string ClaimOverage = """
        UPDATE billing_overage_charges SET claim_owner = @ClaimOwner, claim_until = @ClaimUntil
        WHERE id = @Id AND state = @Pending AND (claim_until IS NULL OR claim_until < @ClaimedAt)
        """;

    private const string OverageById = DashboardSql.BillingOverageCharges.Select + " WHERE id = @Id";

    private const string CompleteOverage = """
        UPDATE billing_overage_charges
        SET state = @State, provider_id = @ProviderId, provider_amount_cents = @ProviderAmountCents, accepted_at = @AcceptedAt, skip_reason = @SkipReason,
            claim_owner = NULL, claim_until = NULL
        WHERE id = @Id AND claim_owner = @Owner AND state = @Pending
        """;

    internal async Task DispatchOverageAsync(string chargeId, CancellationToken cancellationToken)
    {
        var owner = AccountService.NewId();
        var claimedAt = time.GetUtcNow();
        var claimUntil = claimedAt + DispatchClaim;
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            var claimed = await connection.ExecuteAsync(ClaimOverage,
                new { Id = chargeId, OverageCharge.Pending, ClaimedAt = claimedAt, ClaimOwner = owner, ClaimUntil = claimUntil });
            if (claimed != 1)
            {
                return;
            }
        }
        OverageOutcome outcome = new Released();
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(DispatchAttempt);
            outcome = await AttemptOverageAsync(chargeId, attempt.Token);
        }
        finally
        {
            await CompleteOverageAsync(chargeId, owner, outcome);
        }
    }

    private async Task<OverageOutcome> AttemptOverageAsync(string chargeId, CancellationToken cancellationToken)
    {
        OverageCharge charge;
        BillingSubscription? subscription;
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            charge = await connection.QuerySingleAsync<OverageCharge>(OverageById, new { Id = chargeId });
            subscription = await connection.QuerySingleOrDefaultAsync<BillingSubscription>(SubscriptionOf, new { charge.OrganizationId, charge.SubscriptionId });
        }
        var binding = await storage.BindingRowAsync(charge.OrganizationId, cancellationToken);
        if (binding is null || binding.State == "deleting" || binding.ErasureId is not null) { return new Released(); }
        var client = await ClientAsync(cancellationToken);
        var month = charge.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        InvoiceItem? own = null;
        var ownCount = 0;
        var previous = new List<InvoiceItem>();
        await foreach (var item in client.V1.InvoiceItems.ListAutoPagingAsync(new() { Customer = charge.CustomerId, Created = new DateRangeOptions { GreaterThanOrEqual = charge.Month.UtcDateTime }, Limit = 100 }, cancellationToken: cancellationToken))
        {
            var metadata = item.Metadata ?? [];
            if (metadata.TryGetValue("terrascale_charge", out var tag))
            {
                if (tag == charge.Id)
                {
                    own ??= item;
                    ownCount++;
                }
            }
            else if (metadata.GetValueOrDefault("terrascale_kind") == charge.Meter && metadata.GetValueOrDefault("terrascale_month") == month && metadata.GetValueOrDefault("terrascale_org") == charge.OrganizationId)
            {
                previous.Add(item);
            }
        }
        if (own is not null)
        {
            return ownCount == 1 && Matches(own, charge) ? new ChargedAtProvider(own.Id, own.Amount) : new MismatchedAtProvider(own.Id, own.Amount);
        }
        if (previous.Count > 0)
        {
            return new AdoptedFromPreviousReporter(previous.MinBy(item => item.Date)!.Id, previous.Sum(item => item.Amount));
        }
        if (subscription is null) { return new RetiredWithoutCharge("subscription_unknown"); }
        if (subscription.Ended) { return new RetiredWithoutCharge("subscription_ended"); }
        if (binding.SubscriptionId == charge.SubscriptionId && binding.EffectiveInterval == "annual")
        {
            await client.V1.Subscriptions.UpdateAsync(charge.SubscriptionId, new() { PendingInvoiceItemInterval = new() { Interval = "month", IntervalCount = 1 } }, cancellationToken: cancellationToken);
        }
        var created = await client.V1.InvoiceItems.CreateAsync(new()
        {
            Customer = charge.CustomerId,
            Subscription = charge.SubscriptionId,
            Currency = charge.Currency,
            Amount = charge.AmountCents,
            Description = charge.Description,
            Metadata = new()
            {
                ["terrascale_charge"] = charge.Id,
                ["terrascale_kind"] = charge.Meter,
                ["terrascale_month"] = month,
                ["terrascale_org"] = charge.OrganizationId
            }
        }, Idempotent("overage-" + charge.Id), cancellationToken);
        return Matches(created, charge) ? new ChargedAtProvider(created.Id, created.Amount) : new MismatchedAtProvider(created.Id, created.Amount);
    }

    private static bool Matches(InvoiceItem item, OverageCharge charge) => item.Amount == charge.AmountCents && item.CustomerId == charge.CustomerId && item.Currency == charge.Currency;

    private async Task CompleteOverageAsync(string chargeId, string owner, OverageOutcome outcome)
    {
        var now = time.GetUtcNow();
        var (state, providerId, providerAmount, reason) = outcome switch
        {
            ChargedAtProvider(var id, var amount) => (OverageCharge.Charged, id, (long?)amount, (string?)null),
            AdoptedFromPreviousReporter(var id, var amount) => (OverageCharge.Adopted, id, amount, null),
            MismatchedAtProvider(var id, var amount) => (OverageCharge.Mismatch, id, amount, null),
            RetiredWithoutCharge(var skip) => (OverageCharge.Skipped, null, null, skip),
            Released => (OverageCharge.Pending, null, null, null),
            _ => throw new UnreachableException(),
        };
        DateTimeOffset? acceptedAt = providerId is null ? null : now;
        await using var connection = await storage.OpenAsync(CancellationToken.None);
        var saved = await connection.ExecuteAsync(CompleteOverage, new
        {
            Id = chargeId,
            Owner = owner,
            OverageCharge.Pending,
            State = state,
            ProviderId = providerId,
            ProviderAmountCents = providerAmount,
            AcceptedAt = acceptedAt,
            SkipReason = reason,
        });
        if (saved != 1) { return; }
        switch (outcome)
        {
            case AdoptedFromPreviousReporter:
                telemetry.ReportAdoptedOverage();
                break;
            case MismatchedAtProvider:
                telemetry.ReportMismatchedOverage();
                BillingLog.OverageMismatch(logger, chargeId);
                break;
            default:
                break;
        }
    }
}
