using System.Globalization;
using Cysharp.Text;

namespace TerraScale.DotNet;

/// <summary>A fully-bound public API route: the request path with query string.</summary>
internal readonly record struct TerraScaleBoundRoute(string RequestUri)
{
    public override string ToString() => RequestUri;
}

internal static class TerraScaleApiRoutes
{
    public const string Sync = "/api/v1/sync";

    public static TerraScaleBoundRoute OnboardingGetState() =>
        new TerraScaleBoundRoute("/api/v1/onboarding");

    public static TerraScaleBoundRoute OnboardingOrganizationCreate() =>
        new TerraScaleBoundRoute("/api/v1/onboarding/organization");

    public static TerraScaleBoundRoute OnboardingProjectCreate() =>
        new TerraScaleBoundRoute("/api/v1/onboarding/project");

    public static TerraScaleBoundRoute OrganizationsList(int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery("/api/v1/organizations", ("skip", Number(skip)), ("take", Number(take))));

    public static TerraScaleBoundRoute OrganizationsCreate() =>
        new TerraScaleBoundRoute("/api/v1/organizations");

    public static TerraScaleBoundRoute OrganizationGet(string organizationId) =>
        new TerraScaleBoundRoute($"/api/v1/organizations/{Escape(organizationId)}");

    public static TerraScaleBoundRoute OrganizationUpdate(string organizationId) =>
        new TerraScaleBoundRoute($"/api/v1/organizations/{Escape(organizationId)}");

    public static TerraScaleBoundRoute OrganizationDeletionRequest(string organizationId) =>
        new TerraScaleBoundRoute($"/api/v1/organizations/{Escape(organizationId)}:request-deletion");

    public static TerraScaleBoundRoute OrganizationLeave(string organizationId) =>
        new TerraScaleBoundRoute($"/api/v1/organizations/{Escape(organizationId)}:leave");

    public static TerraScaleBoundRoute OrganizationMembersList(string organizationId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery(
                $"/api/v1/organizations/{Escape(organizationId)}/members",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute OrganizationMemberUpdate(string organizationId, string userId) =>
        new TerraScaleBoundRoute($"/api/v1/organizations/{Escape(organizationId)}/members/{Escape(userId)}");

    public static TerraScaleBoundRoute OrganizationMemberRemove(string organizationId, string userId) =>
        new TerraScaleBoundRoute($"/api/v1/organizations/{Escape(organizationId)}/members/{Escape(userId)}:remove");

    public static TerraScaleBoundRoute OrganizationInvitationsList(string organizationId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery(
                $"/api/v1/organizations/{Escape(organizationId)}/invitations",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute OrganizationInvitationCreate(string organizationId) =>
        new TerraScaleBoundRoute($"/api/v1/organizations/{Escape(organizationId)}/invitations");

    public static TerraScaleBoundRoute OrganizationInvitationAccept(string organizationId, string invitationId) =>
        OrganizationInvitationAction(
            organizationId,
            invitationId,
            "accept");

    public static TerraScaleBoundRoute OrganizationInvitationDecline(string organizationId, string invitationId) =>
        OrganizationInvitationAction(
            organizationId,
            invitationId,
            "decline");

    public static TerraScaleBoundRoute OrganizationInvitationResend(string organizationId, string invitationId) =>
        OrganizationInvitationAction(
            organizationId,
            invitationId,
            "resend");

    public static TerraScaleBoundRoute OrganizationInvitationCancel(string organizationId, string invitationId) =>
        OrganizationInvitationAction(
            organizationId,
            invitationId,
            "cancel");

    public static TerraScaleBoundRoute RegionsList() =>
        new TerraScaleBoundRoute("/api/v1/regions");

    public static TerraScaleBoundRoute RegionGet(string regionCode) =>
        new TerraScaleBoundRoute($"/api/v1/regions/{Escape(regionCode)}");

    public static TerraScaleBoundRoute ProjectsList(string organizationId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery($"/api/v1/organizations/{Escape(organizationId)}/projects", ("skip", Number(skip)), ("take", Number(take))));

    public static TerraScaleBoundRoute ProjectsCreate(string organizationId) =>
        new TerraScaleBoundRoute($"/api/v1/organizations/{Escape(organizationId)}/projects");

    public static TerraScaleBoundRoute ProjectGet(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}");

    public static TerraScaleBoundRoute ProjectUpdate(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}");

    public static TerraScaleBoundRoute ProjectDelete(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}");

    public static TerraScaleBoundRoute ProjectRegionPlacementsList(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/regions");

    public static TerraScaleBoundRoute ProjectTeamMembersList(string projectId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery($"/api/v1/projects/{Escape(projectId)}/access/team-members", ("skip", Number(skip)), ("take", Number(take))));

    public static TerraScaleBoundRoute ProjectTeamMembersCreate(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/access/team-members");

    public static TerraScaleBoundRoute ProjectTeamMemberUpdate(string projectId, string userId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/access/team-members/{Escape(userId)}");

    public static TerraScaleBoundRoute ProjectTeamMemberDelete(string projectId, string userId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/access/team-members/{Escape(userId)}");

    public static TerraScaleBoundRoute ProjectDatabasesList(string projectId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery($"/api/v1/projects/{Escape(projectId)}/databases", ("skip", Number(skip)), ("take", Number(take))));

    public static TerraScaleBoundRoute ProjectDatabasesCreate(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases");

    public static TerraScaleBoundRoute ProjectDatabaseGet(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}");

    public static TerraScaleBoundRoute ProjectDatabaseDelete(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}");

    public static TerraScaleBoundRoute DatabaseBranchesCreate(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/database-branches");

    public static TerraScaleBoundRoute DatabaseBranchesCapabilityGet(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/database-branches/capability");

    public static TerraScaleBoundRoute ProjectSavedQueriesList(string projectId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery($"/api/v1/projects/{Escape(projectId)}/saved-queries", ("skip", Number(skip)), ("take", Number(take))));

    public static TerraScaleBoundRoute ProjectSavedQueriesCreate(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/saved-queries");

    public static TerraScaleBoundRoute ProjectSavedQueryGet(string projectId, string savedQueryId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/saved-queries/{Escape(savedQueryId)}");

    public static TerraScaleBoundRoute ProjectSavedQueryUpdate(string projectId, string savedQueryId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/saved-queries/{Escape(savedQueryId)}");

    public static TerraScaleBoundRoute ProjectSavedQueryDelete(string projectId, string savedQueryId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/saved-queries/{Escape(savedQueryId)}");

    public static TerraScaleBoundRoute ProjectAuditList(string projectId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/audit",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute ProjectAuditGet(string projectId, string auditEventId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/audit/{Escape(auditEventId)}");





















    public static TerraScaleBoundRoute DocumentsList(string repositoryId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery(
                $"/api/v1/repositories/{Escape(repositoryId)}/documents",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute DocumentsCreate(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents");

    public static TerraScaleBoundRoute DocumentGet(string repositoryId, string documentId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute DocumentPut(string repositoryId, string documentId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute DocumentPatch(string repositoryId, string documentId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute DocumentsBatch(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents/batch");

    public static TerraScaleBoundRoute DocumentDelete(string repositoryId, string documentId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute QueryExecute(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/tql");

    public static TerraScaleBoundRoute SchemaGet(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/schema");

    public static TerraScaleBoundRoute SchemaValidate(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/schema/validate");

    public static TerraScaleBoundRoute SchemaConfigure(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/schema");

    public static TerraScaleBoundRoute ProjectUsageGet(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/usage");

    public static TerraScaleBoundRoute ProjectUsageWorkloadGet(string projectId, string? range) =>
        new TerraScaleBoundRoute(WithQuery($"/api/v1/projects/{Escape(projectId)}/usage/workload", ("range", NormalizeOptional(range))));

    public static TerraScaleBoundRoute ProjectUsageHistoryGet(string projectId, string? range) =>
        new TerraScaleBoundRoute(WithQuery($"/api/v1/projects/{Escape(projectId)}/usage/history", ("range", NormalizeOptional(range))));

    public static TerraScaleBoundRoute ProjectLatencyGet(string projectId, double? latitude, double? longitude) =>
        new TerraScaleBoundRoute(WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/latency",
                ("latitude", Number(latitude)),
                ("longitude", Number(longitude))));

    public static TerraScaleBoundRoute OrganizationUsageGet(string organizationId) =>
        new TerraScaleBoundRoute($"/api/v1/organizations/{Escape(organizationId)}/usage");

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

    public static TerraScaleBoundRoute NotificationsList(
        int skip,
        int take,
        bool unreadOnly,
        string? organizationId = null,
        string? projectId = null,
        bool includeOrganizationNotifications = false) =>
        new TerraScaleBoundRoute(WithQuery(
                "/api/v1/users/me/notifications",
                ("skip", Number(skip)),
                ("take", Number(take)),
                ("unreadOnly", Boolean(unreadOnly)),
                ("organizationId", NormalizeOptional(organizationId)),
                ("projectId", NormalizeOptional(projectId)),
                ("includeOrganizationNotifications", Boolean(includeOrganizationNotifications))));

    public static TerraScaleBoundRoute NotificationMarkRead(string notificationId) =>
        new TerraScaleBoundRoute($"/api/v1/users/me/notifications/{Escape(notificationId)}:mark-read");

    public static TerraScaleBoundRoute NotificationsMarkAllRead(string? organizationId = null) =>
        new TerraScaleBoundRoute(WithQuery(
                "/api/v1/users/me/notifications:mark-all-read",
                ("organizationId", NormalizeOptional(organizationId))));

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

    public static string AdminBillingReconciliationTrigger(string organizationId) =>
        $"/api/v1/admin/billing/reconciliation/{Escape(organizationId)}:trigger";

    public static string AdminBillingReconciliationGet(string organizationId, string attemptId) =>
        $"/api/v1/admin/billing/reconciliation/{Escape(organizationId)}/{Escape(attemptId)}";

    public static TerraScaleBoundRoute BackupPolicyConfigure(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups/policy");

    public static TerraScaleBoundRoute BackupsPlan(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups:plan");

    public static TerraScaleBoundRoute BackupsExecute(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups:execute");

    public static TerraScaleBoundRoute BackupsList(string projectId, string databaseId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute BackupGet(string projectId, string databaseId, string backupId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups/{Escape(backupId)}");

    public static TerraScaleBoundRoute RestorePointsList(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restore-points");

    public static TerraScaleBoundRoute RestoresCreate(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores");

    public static TerraScaleBoundRoute RestoresList(string projectId, string databaseId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute RestoreGet(string projectId, string databaseId, string restoreId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores/{Escape(restoreId)}");


    private static TerraScaleBoundRoute OrganizationInvitationAction(
        string organizationId,
        string invitationId,
        string action) =>
        new TerraScaleBoundRoute($"/api/v1/organizations/{Escape(organizationId)}/invitations/{Escape(invitationId)}:{action}");

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

    private static string EscapeDocumentId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return string.Join(
            '/',
            value
                .Trim()
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));
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
