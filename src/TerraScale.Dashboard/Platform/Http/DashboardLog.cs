
namespace TerraScale.Dashboard.Platform.Http
{

    internal static partial class DashboardLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Dashboard dependency failed on {Route} ({ErrorType} {Status})")]
        internal static partial void DashboardDependencyFailedOn(this ILogger logger, object? route, object? errorType, object? status);

        [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Dashboard request failed on {Route}: {Failure}")]
        internal static partial void DashboardRequestFailed(this ILogger logger, object? route, object? failure);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Audit event was not recorded ({ErrorType})")]
        internal static partial void AuditEventWasNotRecorded(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Page timing {Method} {Route} {Status}: {Timing}")]
        internal static partial void PageTiming(this ILogger logger, object? method, object? route, object? status, object? timing);

        [LoggerMessage(EventId = 5, Message = "Upstream timing {Upstream} {Host} {Method} {Operation} {Status}: start {StartMs} ms, duration {DurationMs} ms")]
        internal static partial void UpstreamTimingStartMsDurationMs(this ILogger logger, LogLevel level, object? upstream, object? host, object? method, object? operation, object? status, object? startMs, object? durationMs);


        [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Dashboard request on {Route} lost a concurrent write race; answered 409")]
        internal static partial void DashboardRequestConflicted(this ILogger logger, object? route);

        [LoggerMessage(EventId = 7, Message = "Refresh of a stored ts-auth grant in {Table} failed ({ErrorType}, status {Status}, code {ErrorCode})")]
        internal static partial void RefreshOfAStoredTsAuth(this ILogger logger, LogLevel level, object? table, object? errorType, object? status, object? errorCode);

        [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Saving a refreshed ts-auth grant in {Table} failed ({ErrorType}); retrying")]
        internal static partial void SavingARefreshedTsAuthGrant(this ILogger logger, object? table, object? errorType);


        [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "MCP tool {Tool} failed upstream with {Status} ({ErrorType}, code {ErrorCode})")]
        internal static partial void MCPToolFailedUpstreamWithCode(this ILogger logger, object? tool, object? status, object? errorType, object? errorCode);

        [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "MCP tool {Tool} failed at the billing provider ({ErrorType})")]
        internal static partial void MCPToolFailedAtTheBilling(this ILogger logger, object? tool, object? errorType);

        [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "MCP unfinished registration cleanup is unavailable ({ErrorType}); retrying on a later pass.")]
        internal static partial void MCPUnfinishedRegistrationCleanupIsUnavailable(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Dashboard state is not initialized at startup ({ErrorType}); starting not ready and retrying.")]
        internal static partial void DashboardStateIsNotInitializedAt(this ILogger logger, string errorType);

        [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "Dashboard state initialized after {Attempts} attempts.")]
        internal static partial void DashboardStateInitializedAfterAttempts(this ILogger logger, int attempts);

        [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "Dashboard state is not initialized after {Attempts} attempts ({ErrorType}); retrying.")]
        internal static partial void DashboardStateIsNotInitializedAfter(this ILogger logger, int attempts, string errorType);

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

        [LoggerMessage(EventId = 24, Level = LogLevel.Warning, Message = "ts-auth sign-in failed: {Reference} ({Cause})")]
        internal static partial void TsAuthSignInFailed(this ILogger logger, object? reference, object? cause);

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

        [LoggerMessage(EventId = 39, Level = LogLevel.Warning, Message = "An allowance notification for organization {OrganizationId} could not be processed ({ErrorType}); other organizations continue.")]
        internal static partial void AnAllowanceNotificationForOrganizationCould(this ILogger logger, object? organizationId, object? errorType);

        [LoggerMessage(EventId = 40, Level = LogLevel.Warning, Message = "Revoke dashboard sessions after committed member removal for {UserId} ({ErrorType})")]
        internal static partial void RevokeDashboardSessionsAfterCommittedMember(this ILogger logger, object? userId, object? errorType);

        [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Dashboard deletion cleanup is unavailable ({ErrorType}); retrying in 30 seconds.")]
        internal static partial void DashboardDeletionCleanupIsUnavailableRetrying(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 42, Level = LogLevel.Warning, Message = "A support request could not be sent ({ErrorType}).")]
        internal static partial void ASupportRequestCouldNotBe(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 43, Level = LogLevel.Warning, Message = "Stripe event {EventId} ({EventType}) was not applied ({ErrorType}: {Error}); answering 503 so Stripe redelivers it.")]
        internal static partial void StripeEventWasNotAppliedAnswering(this ILogger logger, object? eventId, object? eventType, object? errorType, object? error);

        [LoggerMessage(EventId = 44, Level = LogLevel.Error, Message = "Dashboard panel {Panel} failed on {Route}; the page rendered without it: {Failure}")]
        internal static partial void DashboardPanelFailed(this ILogger logger, object? panel, object? route, object? failure);

        [LoggerMessage(EventId = 45, Level = LogLevel.Warning, Message = "Expired dashboard rows were not swept ({ErrorType}); retrying on the next pass.")]
        internal static partial void ExpiredRowsWereNotSwept(this ILogger logger, string errorType);

        [LoggerMessage(EventId = 46, Level = LogLevel.Warning, Message = "Stripe binding refresh is unavailable ({ErrorType}); retrying next day.")]
        internal static partial void StripeBindingRefreshIsUnavailableRetryingNext(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 47, Level = LogLevel.Warning, Message = "A Stripe binding could not be refreshed ({ErrorType}); other organizations continue.")]
        internal static partial void AStripeBindingCouldNotBeRefreshed(this ILogger logger, object? errorType);

        [LoggerMessage(EventId = 48, Level = LogLevel.Warning, Message = "Dashboard deletion cleanup of {Scope} {SubjectId} (proof {ProofId}) failed with {ErrorType} {ErrorCode}; retrying every 30 seconds, logged again when the error changes.")]
        internal static partial void DashboardDeletionCleanupFailed(this ILogger logger, string proofId, string scope, string subjectId, string errorType, string errorCode);

        [LoggerMessage(EventId = 49, Level = LogLevel.Warning, Message = "Account deletion did not end the billing of organization {OrganizationId} with {ErrorType} {ErrorCode}; the deletion cleanup retries it.")]
        internal static partial void AccountDeletionDidNotEndBilling(this ILogger logger, string organizationId, string errorType, string errorCode);

        [LoggerMessage(EventId = 50, Level = LogLevel.Warning, Message = "Revoking the ts-auth grant of expired dashboard session {SessionId} failed ({ErrorType}, status {Status}, code {ErrorCode}); its row stays for the next pass.")]
        internal static partial void RevokingAnExpiredSessionFailed(this ILogger logger, string sessionId, string errorType, object? status, string errorCode);

        [LoggerMessage(EventId = 100, Message = "Dropped a stored ts-auth grant from {Table}: {Reason}")]
        internal static partial void DroppedStoredGrant(this ILogger logger, LogLevel level, string table, string reason);
    }
}
