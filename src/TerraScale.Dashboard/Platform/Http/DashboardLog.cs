
namespace TerraScale.Dashboard.Platform.Http
{

    internal static partial class DashboardLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Dashboard dependency failed on {Route} ({ErrorType} {Status})")]
        internal static partial void DashboardDependencyFailedOn(this ILogger logger, object? route, object? errorType, object? status);

        [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Dashboard request failed ({ErrorType})")]
        internal static partial void DashboardRequestFailed(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Audit event was not recorded ({ErrorType})")]
        internal static partial void AuditEventWasNotRecorded(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Page timing {Method} {Route} {Status}: {Timing}")]
        internal static partial void PageTiming(this ILogger logger, object? method, object? route, object? status, object? timing);

        [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Upstream timing {Upstream} {Host} {Method} {Operation} {Status}: start {StartMs} ms, duration {DurationMs} ms")]
        internal static partial void UpstreamTimingStartMsDurationMs(this ILogger logger, object? upstream, object? host, object? method, object? operation, object? status, object? startMs, object? durationMs);

        [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Partial session cleanup failed ({ErrorType})")]
        internal static partial void PartialSessionCleanupFailed(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Refresh of a stored ts-auth grant in {Collection} failed ({ErrorType}, status {Status}, code {ErrorCode})")]
        internal static partial void RefreshOfAStoredTsAuth(this ILogger logger, object? collection, object? errorType, object? status, object? errorCode);

        [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Saving a refreshed ts-auth grant in {Collection} failed ({ErrorType}); retrying")]
        internal static partial void SavingARefreshedTsAuthGrant(this ILogger logger, object? collection, object? errorType);

        [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Revoked session index cleanup failed ({ErrorType})")]
        internal static partial void RevokedSessionIndexCleanupFailed(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "MCP tool {Tool} failed upstream with {Status} ({ErrorType}, code {ErrorCode})")]
        internal static partial void MCPToolFailedUpstreamWithCode(this ILogger logger, object? tool, object? status, object? errorType, object? errorCode);

        [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "MCP tool {Tool} failed at the billing provider ({ErrorType})")]
        internal static partial void MCPToolFailedAtTheBilling(this ILogger logger, object? tool, object? errorType);

        [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "MCP unfinished registration cleanup is unavailable ({ErrorType}); retrying on a later pass.")]
        internal static partial void MCPUnfinishedRegistrationCleanupIsUnavailable(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Dashboard state is not initialized at startup ({ErrorType}: {Error}); starting not ready and retrying.")]
        internal static partial void DashboardStateIsNotInitializedAt(this ILogger logger, object? errorType, object? error);

        [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "Dashboard state initialized after {Attempts} attempts.")]
        internal static partial void DashboardStateInitializedAfterAttempts(this ILogger logger, int attempts);

        [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "Dashboard state is not initialized after {Attempts} attempts ({ErrorType}: {Error}); retrying.")]
        internal static partial void DashboardStateIsNotInitializedAfter(this ILogger logger, object? attempts, object? errorType, object? error);

        [LoggerMessage(EventId = 16, Level = LogLevel.Warning, Message = "Readiness: ts-auth answered {StatusCode}")]
        internal static partial void ReadinessTsAuthAnswered(this ILogger logger, object? statusCode);

        [LoggerMessage(EventId = 17, Level = LogLevel.Warning, Message = "Readiness: ts-auth unreachable ({ErrorType})")]
        internal static partial void ReadinessTsAuthUnreachable(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 18, Level = LogLevel.Warning, Message = "Readiness: TerraBase state unavailable ({ErrorType})")]
        internal static partial void ReadinessTerraBaseStateUnavailable(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 19, Level = LogLevel.Warning, Message = "TerraBase refused plan {Plan} for organization {Organization}: {Status}")]
        internal static partial void TerraBaseRefusedPlanForOrganization(this ILogger logger, object? plan, object? organization, object? status);

        [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "TerraBase refused plan {Plan} for project {Project}: {Status}")]
        internal static partial void TerraBaseRefusedPlanForProject(this ILogger logger, object? plan, object? project, object? status);

        [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "TerraBase plan publish failed for organization {Organization} ({ErrorType})")]
        internal static partial void TerraBasePlanPublishFailedForOrganization(this ILogger logger, object? organization, object? errorType);

        [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "TerraBase plan publish timed out for organization {Organization}")]
        internal static partial void TerraBasePlanPublishTimedOutFor(this ILogger logger, object? organization);

        [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Rejected account grant cleanup failed ({ErrorType})")]
        internal static partial void RejectedAccountGrantCleanupFailed(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 24, Level = LogLevel.Warning, Message = "ts-auth sign-in failed: {Reference} ({ErrorType})")]
        internal static partial void TsAuthSignInFailed(this ILogger logger, object? reference, object? errorType);

        [LoggerMessage(EventId = 25, Level = LogLevel.Warning, Message = "Checkout for organization {OrganizationId} is unavailable ({ErrorType}: {Error})")]
        internal static partial void CheckoutForOrganizationIsUnavailable(this ILogger logger, object? organizationId, object? errorType, object? error);

        [LoggerMessage(EventId = 26, Level = LogLevel.Warning, Message = "Billing MCP tool failed ({ErrorType}: {Error})")]
        internal static partial void BillingMCPToolFailed(this ILogger logger, object? errorType, object? error);

        [LoggerMessage(EventId = 27, Level = LogLevel.Warning, Message = "Billing {Action} {RequestId} applied in Stripe; reconciliation deferred ({ErrorType}: {Error})")]
        internal static partial void BillingAppliedInStripeReconciliationDeferred(this ILogger logger, object? action, object? requestId, object? errorType, object? error);

        [LoggerMessage(EventId = 28, Level = LogLevel.Warning, Message = "Billing change failed ({ErrorType}: {Error})")]
        internal static partial void BillingChangeFailed(this ILogger logger, object? errorType, object? error);

        [LoggerMessage(EventId = 29, Level = LogLevel.Warning, Message = "Plan reconciliation is unavailable ({ErrorType}); retrying in 30 seconds.")]
        internal static partial void PlanReconciliationIsUnavailableRetryingIn(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "A pending checkout did not reconcile ({ErrorType}); retrying in 30 seconds.")]
        internal static partial void APendingCheckoutDidNotReconcile(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 31, Level = LogLevel.Information, Message = "Stripe event {EventId} ({EventType}) applied for organization {OrganizationId}")]
        internal static partial void StripeEventAppliedForOrganization(this ILogger logger, object? eventId, object? eventType, object? organizationId);

        [LoggerMessage(EventId = 32, Level = LogLevel.Warning, Message = "Usage overage could not be reported for one organization ({ErrorType}); other organizations continue.")]
        internal static partial void UsageOverageCouldNotBeReported(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 33, Level = LogLevel.Warning, Message = "Usage overage reporting is unavailable ({ErrorType}); retrying next hour.")]
        internal static partial void UsageOverageReportingIsUnavailableRetrying(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 34, Level = LogLevel.Warning, Message = "Log drains are unavailable ({ErrorType}); retrying shortly.")]
        internal static partial void LogDrainsAreUnavailableRetryingShortly(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 35, Level = LogLevel.Warning, Message = "A project log drain could not run ({ErrorType}); other projects continue.")]
        internal static partial void AProjectLogDrainCouldNot(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 36, Level = LogLevel.Warning, Message = "Alert rules are unavailable ({ErrorType}); retrying next minute.")]
        internal static partial void AlertRulesAreUnavailableRetryingNext(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 37, Level = LogLevel.Warning, Message = "Alert rules for a project could not be evaluated ({ErrorType}); other projects continue.")]
        internal static partial void AlertRulesForAProjectCould(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 38, Level = LogLevel.Warning, Message = "Allowance notifications are unavailable ({ErrorType}); retrying next minute.")]
        internal static partial void AllowanceNotificationsAreUnavailableRetryingNext(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 39, Level = LogLevel.Warning, Message = "An allowance notification for organization {OrganizationId} could not be processed ({ErrorType}: {Error}); other organizations continue.")]
        internal static partial void AnAllowanceNotificationForOrganizationCould(this ILogger logger, object? organizationId, object? errorType, object? error);

        [LoggerMessage(EventId = 40, Level = LogLevel.Warning, Message = "Revoke dashboard sessions after committed member removal for {UserId} ({ErrorType})")]
        internal static partial void RevokeDashboardSessionsAfterCommittedMember(this ILogger logger, object? userId, object? errorType);

        [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Dashboard deletion cleanup is unavailable ({ErrorType}); retrying in 30 seconds.")]
        internal static partial void DashboardDeletionCleanupIsUnavailableRetrying(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 42, Level = LogLevel.Warning, Message = "A support request could not be sent ({ErrorType}).")]
        internal static partial void ASupportRequestCouldNotBe(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 43, Level = LogLevel.Warning, Message = "Stripe event {EventId} ({EventType}) was not applied ({ErrorType}: {Error}); answering 503 so Stripe redelivers it.")]
        internal static partial void StripeEventWasNotAppliedAnswering(this ILogger logger, object? eventId, object? eventType, object? errorType, object? error);

        [LoggerMessage(EventId = 100, Message = "Dropped a stored ts-auth grant from {Collection}: {Reason}")]
        internal static partial void DroppedStoredGrant(this ILogger logger, LogLevel level, string collection, string reason);
    }
}
