namespace TerraScale.Dashboard.Features.Billing;

internal static partial class BillingLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment grace expiry deferred: provider unavailable for more than 24 hours.")]
    internal static partial void ExpiryDeferred(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional subscription tax repair failed ({Kind}); entitlement refresh committed.")]
    internal static partial void TaxRepairFailed(ILogger logger, string kind);
}
