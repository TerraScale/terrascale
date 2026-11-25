using System.Data;
using Dapper;
using Stripe;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Errors;
using CheckoutSession = Stripe.Checkout.Session;

namespace TerraScale.Dashboard.Features.Billing;

public sealed partial class StripeBilling
{
    internal static readonly TimeSpan DraftCompletionWindow = TimeSpan.FromHours(24);
    private const string AbandonedDraftReason = "Organization checkout was not completed within 24 hours.";
    private const int RecoveryPage = 50;
    private const string UnstampedOwners = """
        SELECT b.organization_id FROM billing b
        WHERE b.kind = 'stripe' AND b.erasure_id IS NULL AND b.state = 'active' AND length(b.request_id) = 26 AND b.subscription_id <> ''
            AND NOT EXISTS (SELECT 1 FROM stripe_owner_stamps s WHERE s.organization_id = b.organization_id AND s.subscription_id = b.subscription_id)
            AND (@After IS NULL OR b.organization_id > @After)
        ORDER BY b.organization_id LIMIT @Limit
        """;
    private const string RecordStamp = """
        INSERT INTO stripe_owner_stamps (organization_id, subscription_id, stamped_at) VALUES (@OrganizationId, @SubscriptionId, @StampedAt)
        ON CONFLICT (organization_id, subscription_id) DO NOTHING
        """;
    private const string AbandonedDrafts = DashboardSql.PendingOrganizationCheckouts.Select
        + " WHERE created_at < @Before AND state <> @Creating AND (@After IS NULL OR draft_id > @After) ORDER BY draft_id LIMIT @Limit";
    private string? stampCursor;
    private string? draftCursor;

    private async Task<Done> RecordDraftEventAsync(Event @event, CancellationToken cancellationToken)
    {
        if (DraftIdOf(@event.Data.Object) is not { } draftId || !TerraScaleIds.IsValid(draftId)
            || await ReadPendingOrganizationAsync(draftId, cancellationToken) is not { } draft)
        {
            BillingLog.StripeEventUnowned(logger, @event.Id, @event.Type);
            return new Done();
        }

        if (draft.State == OrganizationCheckoutStates.Pending && Paid(@event.Data.Object))
        {
            await RecordPaidDraftAsync(draft, cancellationToken);
        }

        BillingLog.DraftEventRecorded(logger, @event.Id, @event.Type, draftId);
        return new Done();
    }

    private static string? DraftIdOf(IHasObject? item) => item switch
    {
        CheckoutSession checkout => checkout.Metadata?.GetValueOrDefault("draft_id"),
        Subscription subscription => subscription.Metadata?.GetValueOrDefault("draft_id"),
        Invoice invoice => invoice.Parent?.SubscriptionDetails?.Metadata?.GetValueOrDefault("draft_id"),
        _ => null,
    };

    private static bool Paid(IHasObject? item) => item switch
    {
        CheckoutSession checkout => checkout is { Status: "complete", PaymentStatus: "paid" },
        Invoice invoice => invoice is { Status: "paid", AmountPaid: > 0 },
        _ => false,
    };

    public async Task StampDraftOwnersAsync(CancellationToken cancellationToken)
    {
        List<string> organizationIds;
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            organizationIds = (await connection.QueryAsync<string>(UnstampedOwners, new { After = Volatile.Read(ref stampCursor), Limit = RecoveryPage })).AsList();
        }

        Volatile.Write(ref stampCursor, organizationIds.Count < RecoveryPage ? null : organizationIds[^1]);
        foreach (var organizationId in organizationIds)
        {
            try
            {
                await StampOwnerAsync(organizationId, cancellationToken);
            }
            catch (Exception error) when (error is DBConcurrencyException or StripeException or HttpRequestException or IOException or TerraBaseException or TimeoutException
                || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                telemetry.ReportOrganizationMetadataFailure();
                BillingLog.OwnerStampFailed(logger, organizationId, error.GetType().Name);
            }
        }
    }

    private async Task StampOwnerAsync(string organizationId, CancellationToken cancellationToken)
    {
        if (await storage.BindingRowAsync(organizationId, cancellationToken) is not { Kind: "stripe", ErasureId: null, State: "active", SubscriptionId.Length: > 0 } binding)
        {
            return;
        }

        var client = await ClientAsync(cancellationToken);
        var subscription = await client.V1.Subscriptions.GetAsync(binding.SubscriptionId, new() { Expand = ["customer"] }, cancellationToken: cancellationToken);
        if (subscription.Id != binding.SubscriptionId || subscription.CustomerId != binding.CustomerId || !OwnedBy(subscription, binding))
        {
            throw new IOException("Subscription ownership does not match.");
        }

        var metadata = new Dictionary<string, string> { ["organization_id"] = binding.OrganizationId };
        if (subscription.Metadata.GetValueOrDefault("organization_id") != binding.OrganizationId)
        {
            await client.V1.Subscriptions.UpdateAsync(subscription.Id, new() { Metadata = metadata },
                Idempotent("organization-subscription-" + binding.RequestId), cancellationToken);
        }

        if (subscription.Customer is { Deleted: not true } customer && customer.Metadata?.GetValueOrDefault("organization_id") != binding.OrganizationId)
        {
            await client.V1.Customers.UpdateAsync(binding.CustomerId, new() { Metadata = metadata },
                Idempotent("organization-owner-" + binding.RequestId), cancellationToken);
        }

        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(RecordStamp, new { binding.OrganizationId, binding.SubscriptionId, StampedAt = time.GetUtcNow() });
    }

    public async Task ReconcileAbandonedDraftsAsync(CancellationToken cancellationToken)
    {
        List<PendingOrganizationCheckout> drafts;
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            drafts = (await connection.QueryAsync<PendingOrganizationCheckout>(AbandonedDrafts,
                new { Before = (time.GetUtcNow() - DraftCompletionWindow).UtcDateTime, Creating = OrganizationCheckoutStates.Creating, After = Volatile.Read(ref draftCursor), Limit = RecoveryPage })).AsList();
        }

        Volatile.Write(ref draftCursor, drafts.Count < RecoveryPage ? null : drafts[^1].DraftId);
        foreach (var draft in drafts)
        {
            try { await SettleAbandonedDraftAsync(draft.DraftId, cancellationToken); }
            catch (Exception error) when (error is DBConcurrencyException or StripeException or HttpRequestException or IOException or TerraBaseException or TimeoutException
                or AccountAccessException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                BillingLog.DraftSettlementFailed(logger, draft.DraftId, draft.UserId, draft.CheckoutId, error.GetType().Name);
            }
        }
    }

    private async Task SettleAbandonedDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        bool? abandoned = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var draft = await ReadPendingOrganizationAsync(draftId, cancellationToken);
            if (draft is null || draft.State == OrganizationCheckoutStates.Creating)
            {
                return;
            }

            abandoned ??= draft.State == OrganizationCheckoutStates.Pending;
            if (draft.State == OrganizationCheckoutStates.Pending)
            {
                draft.State = OrganizationCheckoutStates.Canceling;
                draft.RefusalReason = AbandonedDraftReason;
                if (!await storage.ReplaceAsync(draft, cancellationToken))
                {
                    continue;
                }
            }

            var refund = await CancelDraftPaymentAsync(draft, cancellationToken);
            var pendingRefund = refund switch
            {
                bool => null,
                OrganizationRefundPending pending => pending,
            };
            if (pendingRefund is not null)
            {
                await RecordUnsettledRefundAsync(draftId, pendingRefund.Status, cancellationToken);
                BillingLog.DraftRefundPending(logger, draftId, draft.UserId, draft.CheckoutId, pendingRefund.Status);
                return;
            }

            var current = await ReadPendingOrganizationAsync(draftId, cancellationToken);
            if (current is null)
            {
                return;
            }

            if (current.State == OrganizationCheckoutStates.Creating)
            {
                continue;
            }

            if (await storage.RemoveAsync(current, cancellationToken))
            {
                var refunded = refund switch { bool settled => settled, OrganizationRefundPending => false };
                if (abandoned == true)
                {
                    telemetry.ReportAbandonedDraftCheckout();
                    BillingLog.DraftAbandoned(logger, draftId, current.UserId, current.CheckoutId, current.CustomerId, current.SubscriptionId, current.PaidAt is not null, refunded);
                }
                else
                {
                    BillingLog.DraftCancellationFinished(logger, draftId, current.UserId, current.CheckoutId, current.CustomerId, current.SubscriptionId, current.PaidAt is not null, refunded);
                }

                return;
            }
        }
    }
}
