using System.Globalization;
using Cysharp.Text;

namespace TerraScale.DotNet;

internal static class TerraScaleApiRoutes
{
    public const string Sync = "/api/v1/sync";

    public static string RegionsList() =>
        "/api/v1/regions";

    public static string ProjectsList(string organizationId, int skip, int take) =>
        WithQuery($"/api/v1/organizations/{Escape(organizationId)}/projects", ("skip", Number(skip)), ("take", Number(take)));

    public static string ProjectsCreate(string organizationId) =>
        $"/api/v1/organizations/{Escape(organizationId)}/projects";

    public static string ProjectGet(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}";

    public static string ProjectUpdate(string projectId) =>
        ProjectGet(projectId);

    public static string ProjectDelete(string projectId) =>
        ProjectGet(projectId);

    public static string ProjectRegionPlacementsList(string projectId) =>
        $"{ProjectGet(projectId)}/regions";

    public static string ProjectTeamMembersList(string projectId, int skip, int take) =>
        WithQuery($"/api/v1/projects/{Escape(projectId)}/access/team-members", ("skip", Number(skip)), ("take", Number(take)));

    public static string ProjectTeamMembersCreate(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}/access/team-members";

    public static string ProjectTeamMemberUpdate(string projectId, string userId) =>
        $"{ProjectTeamMembersCreate(projectId)}/{Escape(userId)}";

    public static string ProjectTeamMemberDelete(string projectId, string userId) =>
        ProjectTeamMemberUpdate(projectId, userId);

    public static string ProjectDatabasesList(string projectId, int skip, int take) =>
        WithQuery($"/api/v1/projects/{Escape(projectId)}/databases", ("skip", Number(skip)), ("take", Number(take)));

    public static string ProjectDatabasesCreate(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}/databases";

    public static string ProjectDatabaseGet(string projectId, string databaseId) =>
        $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}";

    public static string ProjectDatabaseDelete(string projectId, string databaseId) =>
        ProjectDatabaseGet(projectId, databaseId);

    public static string DatabaseBranchesCreate(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}/database-branches";

    public static string DatabaseBranchesCapabilityGet(string projectId) =>
        $"{DatabaseBranchesCreate(projectId)}/capability";

    public static string ProjectSavedQueriesList(string projectId, int skip, int take) =>
        WithQuery($"/api/v1/projects/{Escape(projectId)}/saved-queries", ("skip", Number(skip)), ("take", Number(take)));

    public static string ProjectSavedQueriesCreate(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}/saved-queries";

    public static string ProjectSavedQueryUpdate(string projectId, string savedQueryId) =>
        $"{ProjectSavedQueriesCreate(projectId)}/{Escape(savedQueryId)}";

    public static string ProjectSavedQueryDelete(string projectId, string savedQueryId) =>
        ProjectSavedQueryUpdate(projectId, savedQueryId);

    public static string ProjectSavedQueriesV2List(string projectId, int skip, int take) =>
        WithQuery($"/api/v2/projects/{Escape(projectId)}/saved-queries", ("skip", Number(skip)), ("take", Number(take)));

    public static string ProjectSavedQueryV2Get(string projectId, string savedQueryId) =>
        $"/api/v2/projects/{Escape(projectId)}/saved-queries/{Escape(savedQueryId)}";

    public static string ProjectSavedQueryV2Update(string projectId, string savedQueryId) =>
        ProjectSavedQueryV2Get(projectId, savedQueryId);

    public static string ProjectSavedQueriesV3List(string projectId, string? cursor, int take) =>
        WithQuery($"/api/v3/projects/{Escape(projectId)}/saved-queries", ("cursor", cursor), ("take", Number(take)));

    public static string ProjectQueuesList(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}/queues";

    public static string ProjectQueuesCreate(string projectId) =>
        ProjectQueuesList(projectId);

    public static string ProjectQueueGet(string projectId, string queueId) =>
        $"{ProjectQueuesList(projectId)}/{Escape(queueId)}";

    public static string ProjectQueueDelete(string projectId, string queueId) =>
        ProjectQueueGet(projectId, queueId);

    public static string ProjectQueuePurge(string projectId, string queueId) =>
        $"{ProjectQueueGet(projectId, queueId)}:purge";

    public static string ProjectQueuePublish(string projectId, string queueId) =>
        $"{ProjectQueueGet(projectId, queueId)}:publish";

    public static string ProjectQueuePeek(string projectId, string queueId, int maxMessages) =>
        WithQuery($"{ProjectQueueGet(projectId, queueId)}:peek", ("maxMessages", Number(maxMessages)));

    public static string ProjectQueueReceive(string projectId, string queueId) =>
        $"{ProjectQueueGet(projectId, queueId)}:receive";

    public static string ProjectQueueComplete(string projectId, string queueId) =>
        $"{ProjectQueueGet(projectId, queueId)}:complete";

    public static string ProjectQueueAbandon(string projectId, string queueId) =>
        $"{ProjectQueueGet(projectId, queueId)}:abandon";

    public static string ProjectQueueDeadLetter(string projectId, string queueId) =>
        $"{ProjectQueueGet(projectId, queueId)}:dead-letter";

    public static string ProjectQueueRenewLock(string projectId, string queueId) =>
        $"{ProjectQueueGet(projectId, queueId)}:renew-lock";

    public static string EnvironmentsCreate(string projectId, string databaseId) =>
        $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/environments";

    public static string EnvironmentsList(string projectId, string databaseId, int skip, int take) =>
        WithQuery(EnvironmentsCreate(projectId, databaseId), ("skip", Number(skip)), ("take", Number(take)));

    public static string EnvironmentGet(string projectId, string databaseId, string environmentId) =>
        $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/environments/{Escape(environmentId)}";

    public static string EnvironmentDelete(string projectId, string databaseId, string environmentId) =>
        EnvironmentGet(projectId, databaseId, environmentId);

    public static string ProjectUsageGet(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}/usage";

    public static string ProjectUsageWorkloadGet(string projectId, string? range) =>
        WithQuery($"/api/v1/projects/{Escape(projectId)}/usage/workload", ("range", NormalizeOptional(range)));

    public static string ProjectUsageHistoryGet(string projectId, string? range) =>
        WithQuery($"/api/v1/projects/{Escape(projectId)}/usage/history", ("range", NormalizeOptional(range)));

    public static string ProjectLatencyGet(string projectId, double? latitude, double? longitude) =>
        WithQuery(
            $"/api/v1/projects/{Escape(projectId)}/latency",
            ("latitude", Number(latitude)),
            ("longitude", Number(longitude)));

    public static string OrganizationUsageGet(string organizationId) =>
        $"/api/v1/organizations/{Escape(organizationId)}/usage";

    public static string PricingPlansList() =>
        "/api/v1/pricing/plans";

    public static string BillingAccountGet(string organizationId) =>
        $"/api/v1/organizations/{Escape(organizationId)}/billing";

    public static string BillingInvoicesList(string organizationId, int skip, int take) =>
        WithQuery($"{BillingAccountGet(organizationId)}/invoices", ("skip", Number(skip)), ("take", Number(take)));

    public static string BillingCheckoutSessionsCreate(string organizationId) =>
        $"{BillingAccountGet(organizationId)}/checkout-sessions";

    public static string BillingPortalSessionsCreate(string organizationId) =>
        $"{BillingAccountGet(organizationId)}/portal-sessions";

    public static string BillingAccessList(string organizationId) =>
        $"{BillingAccountGet(organizationId)}/access";

    public static string BillingAccessUpdate(string organizationId, string userId) =>
        $"{BillingAccessList(organizationId)}/{Escape(userId)}";

    public static string ApiKeysList(string projectId, string? cursor, int limit) =>
        WithQuery($"/api/v1/projects/{Escape(projectId)}/auth/api-keys", ("cursor", NormalizeOptional(cursor)), ("limit", Number(limit)));

    public static string ApiKeysCreate(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}/auth/api-keys";

    public static string ApiKeyRotate(string projectId, string keyId) =>
        $"/api/v1/projects/{Escape(projectId)}/auth/api-keys/{Escape(keyId)}/rotate";

    public static string ApiKeyRevoke(string projectId, string keyId) =>
        $"/api/v1/projects/{Escape(projectId)}/auth/api-keys/{Escape(keyId)}";

    public static string M2MApplicationsList(string projectId, string? cursor, int limit) =>
        WithQuery($"/api/v1/projects/{Escape(projectId)}/auth/m2m-applications", ("cursor", NormalizeOptional(cursor)), ("limit", Number(limit)));

    public static string M2MApplicationsCreate(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}/auth/m2m-applications";

    public static string M2MApplicationRotate(string projectId, string clientId) =>
        $"/api/v1/projects/{Escape(projectId)}/auth/m2m-applications/{Escape(clientId)}/rotate";

    public static string M2MApplicationDelete(string projectId, string clientId) =>
        $"/api/v1/projects/{Escape(projectId)}/auth/m2m-applications/{Escape(clientId)}";

    public static string M2MTokenCreate(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}/auth/m2m/token";

    public static string NotificationsList(
        int skip,
        int take,
        bool unreadOnly,
        string? organizationId = null,
        string? projectId = null,
        bool includeOrganizationNotifications = false) =>
        WithQuery(
            "/api/v1/users/me/notifications",
            ("skip", Number(skip)),
            ("take", Number(take)),
            ("unreadOnly", Boolean(unreadOnly)),
            ("organizationId", NormalizeOptional(organizationId)),
            ("projectId", NormalizeOptional(projectId)),
            ("includeOrganizationNotifications", Boolean(includeOrganizationNotifications)));

    public static string NotificationMarkRead(string notificationId) =>
        $"/api/v1/users/me/notifications/{Escape(notificationId)}:mark-read";

    public static string NotificationsMarkAllRead(string? organizationId = null) =>
        WithQuery(
            "/api/v1/users/me/notifications:mark-all-read",
            ("organizationId", NormalizeOptional(organizationId)));

    public static string AdminOverviewGet() =>
        "/api/v1/admin";

    public static string AdminUsersList(int skip, int take, string? search) =>
        WithQuery("/api/v1/admin/users", ("skip", Number(skip)), ("take", Number(take)), ("search", NormalizeOptional(search)));

    public static string AdminUserGet(string userId) =>
        $"/api/v1/admin/users/{Escape(userId)}";

    public static string AdminUserEnable(string userId) =>
        $"{AdminUserGet(userId)}:enable";

    public static string AdminUserDisable(string userId) =>
        $"{AdminUserGet(userId)}:disable";

    public static string AdminSupportNotesCreate() =>
        "/api/v1/admin/support-notes";

    public static string AdminOrganizationsList(int skip, int take) =>
        WithQuery("/api/v1/admin/organizations", ("skip", Number(skip)), ("take", Number(take)));

    public static string AdminProjectsList(int skip, int take) =>
        WithQuery("/api/v1/admin/projects", ("skip", Number(skip)), ("take", Number(take)));

    public static string AdminDatabasesList(int skip, int take) =>
        WithQuery("/api/v1/admin/databases", ("skip", Number(skip)), ("take", Number(take)));

    public static string AdminUsageGet() =>
        "/api/v1/admin/usage";

    public static string AdminPlansList() =>
        "/api/v1/admin/plans";

    public static string AdminPlanGet(string planId) =>
        $"{AdminPlansList()}/{Escape(planId)}";

    public static string AdminAuditList(int skip, int take) =>
        WithQuery("/api/v1/admin/audit", ("skip", Number(skip)), ("take", Number(take)));

    public static string AdminNotificationsList(int skip, int take) =>
        WithQuery("/api/v1/admin/notifications", ("skip", Number(skip)), ("take", Number(take)));

    public static string AdminPaymentWebhookGet(string provider, string eventId) =>
        $"/api/v1/admin/billing/webhooks/{Escape(provider)}/{Escape(eventId)}";

    public static string AdminPaymentWebhookDeadLettersList(int skip, int take) =>
        WithQuery("/api/v1/admin/billing/webhooks/dead-lettered", ("skip", Number(skip)), ("take", Number(take)));

    public static string AdminPaymentWebhookReplay(string provider, string eventId) =>
        $"{AdminPaymentWebhookGet(provider, eventId)}:replay";

    public static string BackupPolicyConfigure(string projectId, string databaseId) =>
        $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups/policy";

    public static string BackupsCreate(string projectId, string databaseId) =>
        $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups";

    public static string BackupsPlan(string projectId, string databaseId) =>
        $"{BackupsCreate(projectId, databaseId)}:plan";

    public static string BackupsExecute(string projectId, string databaseId) =>
        $"{BackupsCreate(projectId, databaseId)}:execute";

    public static string BackupsList(string projectId, string databaseId, int skip, int take) =>
        WithQuery(BackupsCreate(projectId, databaseId), ("skip", Number(skip)), ("take", Number(take)));

    public static string BackupGet(string projectId, string databaseId, string backupId) =>
        $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups/{Escape(backupId)}";

    public static string RestorePointsList(string projectId, string databaseId) =>
        $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restore-points";

    public static string RestoresCreate(string projectId, string databaseId) =>
        $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores";

    public static string RestoresList(string projectId, string databaseId, int skip, int take) =>
        WithQuery(RestoresCreate(projectId, databaseId), ("skip", Number(skip)), ("take", Number(take)));

    public static string RestoreGet(string projectId, string databaseId, string restoreId) =>
        $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores/{Escape(restoreId)}";

    private static string WithQuery(string path, params (string Name, string? Value)[] values)
    {
        using var builder = ZString.CreateStringBuilder();
        builder.Append(path);
        var hasQuery = false;
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder.Append(hasQuery ? '&' : '?');
            builder.Append(Uri.EscapeDataString(name));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
            hasQuery = true;
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value.Trim());
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Number(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string? Number(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture);

    private static string Boolean(bool value) =>
        value.ToString().ToLowerInvariant();
}
