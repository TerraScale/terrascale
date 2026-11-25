namespace TerraScale.Dashboard.Features.Billing;

internal static partial class BillingLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment grace expiry deferred: provider unavailable for more than 24 hours.")]
    internal static partial void ExpiryDeferred(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional subscription tax repair failed ({Kind}); entitlement refresh committed.")]
    internal static partial void TaxRepairFailed(ILogger logger, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Billing terms were not recorded ({Kind}); the affected months stay unfrozen until a refresh succeeds.")]
    internal static partial void TermsNotRecorded(ILogger logger, string kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Overage charge {ChargeId} does not match its provider item; it is held for review and never charged again.")]
    internal static partial void OverageMismatch(ILogger logger, string chargeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Billing jobs are idle: the database has migration {Migration}, which this build does not know.")]
    internal static partial void BillingJobsIdle(ILogger logger, string migration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Usage of organization {OrganizationId} is unavailable ({Reason}); its allowance and billing checks wait for the next pass.")]
    internal static partial void UsageUnavailable(ILogger logger, string organizationId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Overage for organization {OrganizationId} waits for complete usage of {Month:yyyy-MM} ({Gaps}).")]
    internal static partial void UsageIncomplete(ILogger logger, string organizationId, DateTimeOffset month, string gaps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stripe event delivery rejected: {Reason}.")]
    internal static partial void StripeEventRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stripe event {EventId} ({EventType}) failed attempt {Attempt} ({ErrorType}); next attempt at {RetryAt:o}.")]
    internal static partial void ReceiptRetryScheduled(ILogger logger, string eventId, string eventType, int attempt, string errorType, DateTimeOffset retryAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stripe event {EventId} ({EventType}) failed {Attempt} attempts ({ErrorType}); it is dead-lettered and no longer retried.")]
    internal static partial void ReceiptDeadLettered(ILogger logger, string eventId, string eventType, int attempt, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stripe event {EventId} ({EventType}) recorded for organization draft {DraftId}.")]
    internal static partial void DraftEventRecorded(ILogger logger, string eventId, string eventType, string draftId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stripe event {EventId} ({EventType}) has no TerraScale customer or draft; nothing to apply.")]
    internal static partial void StripeEventUnowned(ILogger logger, string eventId, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abandoned organization checkout {DraftId} of user {UserId} (checkout {CheckoutId}) could not be settled ({ErrorType}); retrying on the next pass.")]
    internal static partial void DraftSettlementFailed(ILogger logger, string draftId, string userId, string checkoutId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abandoned organization checkout {DraftId} of user {UserId} (checkout {CheckoutId}) has a {RefundStatus} refund; retrying on the next pass.")]
    internal static partial void DraftRefundPending(ILogger logger, string draftId, string userId, string checkoutId, string refundStatus);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stripe owner metadata of organization {OrganizationId} was not stamped ({ErrorType}); retrying on a later pass.")]
    internal static partial void OwnerStampFailed(ILogger logger, string organizationId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abandoned organization checkout {DraftId} of user {UserId} was canceled after its completion window (checkout {CheckoutId}, customer {CustomerId}, subscription {SubscriptionId}, paid {Paid}, refunded {Refunded}).")]
    internal static partial void DraftAbandoned(ILogger logger, string draftId, string userId, string checkoutId, string customerId, string subscriptionId, bool paid, bool refunded);

    [LoggerMessage(Level = LogLevel.Information, Message = "Canceled organization checkout {DraftId} of user {UserId} finished in the background (checkout {CheckoutId}, customer {CustomerId}, subscription {SubscriptionId}, paid {Paid}, refunded {Refunded}).")]
    internal static partial void DraftCancellationFinished(ILogger logger, string draftId, string userId, string checkoutId, string customerId, string subscriptionId, bool paid, bool refunded);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stale billing change {RequestId} of organization {OrganizationId} was {Outcome}.")]
    internal static partial void StaleChangeReconciled(ILogger logger, string organizationId, string requestId, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stale billing change {RequestId} of organization {OrganizationId} could not be reconciled ({ErrorType}); retrying on a later pass.")]
    internal static partial void StaleChangeFailed(ILogger logger, string organizationId, string requestId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Billing recovery is unavailable ({ErrorType}); retrying next minute.")]
    internal static partial void RecoveryUnavailable(ILogger logger, string errorType);
}
