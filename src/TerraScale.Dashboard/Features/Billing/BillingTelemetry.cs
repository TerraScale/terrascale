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
}
