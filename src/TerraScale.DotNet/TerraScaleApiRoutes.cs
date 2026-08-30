using System.Globalization;
using Cysharp.Text;

namespace TerraScale.DotNet;

internal static class TerraScaleApiRoutes
{
    public const string Sync = "/api/v1/sync";

    public static TerraScaleBoundRoute OnboardingGetState() =>
        Bind(TerraScalePublicRouteCatalog.OnboardingGetState, "/api/v1/onboarding");

    public static TerraScaleBoundRoute OnboardingOrganizationCreate() =>
        Bind(TerraScalePublicRouteCatalog.OnboardingOrganizationCreate, "/api/v1/onboarding/organization");

    public static TerraScaleBoundRoute OnboardingProjectCreate() =>
        Bind(TerraScalePublicRouteCatalog.OnboardingProjectCreate, "/api/v1/onboarding/project");

    public static TerraScaleBoundRoute OrganizationsList(int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsList,
            WithQuery("/api/v1/organizations", ("skip", Number(skip)), ("take", Number(take))));

    public static TerraScaleBoundRoute OrganizationsCreate() =>
        Bind(TerraScalePublicRouteCatalog.OrganizationsCreate, "/api/v1/organizations");

    public static TerraScaleBoundRoute OrganizationGet(string organizationId) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsGet,
            $"/api/v1/organizations/{Escape(organizationId)}");

    public static TerraScaleBoundRoute OrganizationUpdate(string organizationId) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsUpdate,
            $"/api/v1/organizations/{Escape(organizationId)}");

    public static TerraScaleBoundRoute OrganizationDeletionRequest(string organizationId) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsDeletionRequest,
            $"/api/v1/organizations/{Escape(organizationId)}:request-deletion");

    public static TerraScaleBoundRoute OrganizationLeave(string organizationId) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsLeave,
            $"/api/v1/organizations/{Escape(organizationId)}:leave");

    public static TerraScaleBoundRoute OrganizationMembersList(string organizationId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsMembersList,
            WithQuery(
                $"/api/v1/organizations/{Escape(organizationId)}/members",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute OrganizationMemberUpdate(string organizationId, string userId) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsMembersUpdateRole,
            $"/api/v1/organizations/{Escape(organizationId)}/members/{Escape(userId)}");

    public static TerraScaleBoundRoute OrganizationMemberRemove(string organizationId, string userId) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsMembersRemove,
            $"/api/v1/organizations/{Escape(organizationId)}/members/{Escape(userId)}:remove");

    public static TerraScaleBoundRoute OrganizationInvitationsList(string organizationId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsInvitationsList,
            WithQuery(
                $"/api/v1/organizations/{Escape(organizationId)}/invitations",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute OrganizationInvitationCreate(string organizationId) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsInvitationsCreate,
            $"/api/v1/organizations/{Escape(organizationId)}/invitations");

    public static TerraScaleBoundRoute OrganizationInvitationAccept(string organizationId, string invitationId) =>
        OrganizationInvitationAction(
            TerraScalePublicRouteCatalog.OrganizationsInvitationsAccept,
            organizationId,
            invitationId,
            "accept");

    public static TerraScaleBoundRoute OrganizationInvitationDecline(string organizationId, string invitationId) =>
        OrganizationInvitationAction(
            TerraScalePublicRouteCatalog.OrganizationsInvitationsDecline,
            organizationId,
            invitationId,
            "decline");

    public static TerraScaleBoundRoute OrganizationInvitationResend(string organizationId, string invitationId) =>
        OrganizationInvitationAction(
            TerraScalePublicRouteCatalog.OrganizationsInvitationsResend,
            organizationId,
            invitationId,
            "resend");

    public static TerraScaleBoundRoute OrganizationInvitationCancel(string organizationId, string invitationId) =>
        OrganizationInvitationAction(
            TerraScalePublicRouteCatalog.OrganizationsInvitationsCancel,
            organizationId,
            invitationId,
            "cancel");

    public static TerraScaleBoundRoute RegionsList() =>
        Bind(TerraScalePublicRouteCatalog.RegionsList, "/api/v1/regions");

    public static TerraScaleBoundRoute RegionGet(string regionCode) =>
        Bind(TerraScalePublicRouteCatalog.RegionsGet, $"/api/v1/regions/{Escape(regionCode)}");

    public static TerraScaleBoundRoute ProjectsList(string organizationId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsProjectsList,
            WithQuery($"/api/v1/organizations/{Escape(organizationId)}/projects", ("skip", Number(skip)), ("take", Number(take))));

    public static TerraScaleBoundRoute ProjectsCreate(string organizationId) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsProjectsCreate,
            $"/api/v1/organizations/{Escape(organizationId)}/projects");

    public static TerraScaleBoundRoute ProjectGet(string projectId) =>
        Bind(TerraScalePublicRouteCatalog.OrganizationsProjectsGet, $"/api/v1/projects/{Escape(projectId)}");

    public static TerraScaleBoundRoute ProjectUpdate(string projectId) =>
        Bind(TerraScalePublicRouteCatalog.OrganizationsProjectsUpdate, $"/api/v1/projects/{Escape(projectId)}");

    public static TerraScaleBoundRoute ProjectDelete(string projectId) =>
        Bind(TerraScalePublicRouteCatalog.OrganizationsProjectsDelete, $"/api/v1/projects/{Escape(projectId)}");

    public static TerraScaleBoundRoute ProjectRegionPlacementsList(string projectId) =>
        Bind(
            TerraScalePublicRouteCatalog.OrganizationsProjectsRegionsList,
            $"/api/v1/projects/{Escape(projectId)}/regions");

    public static TerraScaleBoundRoute ProjectTeamMembersList(string projectId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.AccessTeamMembersList,
            WithQuery($"/api/v1/projects/{Escape(projectId)}/access/team-members", ("skip", Number(skip)), ("take", Number(take))));

    public static TerraScaleBoundRoute ProjectTeamMembersCreate(string projectId) =>
        Bind(
            TerraScalePublicRouteCatalog.AccessTeamMembersAdd,
            $"/api/v1/projects/{Escape(projectId)}/access/team-members");

    public static TerraScaleBoundRoute ProjectTeamMemberUpdate(string projectId, string userId) =>
        Bind(
            TerraScalePublicRouteCatalog.AccessTeamMembersUpdate,
            $"/api/v1/projects/{Escape(projectId)}/access/team-members/{Escape(userId)}");

    public static TerraScaleBoundRoute ProjectTeamMemberDelete(string projectId, string userId) =>
        Bind(
            TerraScalePublicRouteCatalog.AccessTeamMembersRemove,
            $"/api/v1/projects/{Escape(projectId)}/access/team-members/{Escape(userId)}");

    public static TerraScaleBoundRoute ProjectDatabasesList(string projectId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.DatabasesList,
            WithQuery($"/api/v1/projects/{Escape(projectId)}/databases", ("skip", Number(skip)), ("take", Number(take))));

    public static TerraScaleBoundRoute ProjectDatabasesCreate(string projectId) =>
        Bind(TerraScalePublicRouteCatalog.DatabasesCreate, $"/api/v1/projects/{Escape(projectId)}/databases");

    public static TerraScaleBoundRoute ProjectDatabaseGet(string projectId, string databaseId) =>
        Bind(
            TerraScalePublicRouteCatalog.DatabasesGet,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}");

    public static TerraScaleBoundRoute ProjectDatabaseDelete(string projectId, string databaseId) =>
        Bind(
            TerraScalePublicRouteCatalog.DatabasesDelete,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}");

    public static TerraScaleBoundRoute DatabaseBranchesCreate(string projectId) =>
        Bind(
            TerraScalePublicRouteCatalog.DatabaseBranchesRequestClone,
            $"/api/v1/projects/{Escape(projectId)}/database-branches");

    public static TerraScaleBoundRoute DatabaseBranchesCapabilityGet(string projectId) =>
        Bind(
            TerraScalePublicRouteCatalog.DatabaseBranchesGetCapability,
            $"/api/v1/projects/{Escape(projectId)}/database-branches/capability");

    public static TerraScaleBoundRoute ProjectSavedQueriesList(string projectId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.SavedQueriesList,
            WithQuery($"/api/v1/projects/{Escape(projectId)}/saved-queries", ("skip", Number(skip)), ("take", Number(take))));

    public static TerraScaleBoundRoute ProjectSavedQueriesCreate(string projectId) =>
        Bind(TerraScalePublicRouteCatalog.SavedQueriesCreate, $"/api/v1/projects/{Escape(projectId)}/saved-queries");

    public static TerraScaleBoundRoute ProjectSavedQueryGet(string projectId, string savedQueryId) =>
        Bind(
            TerraScalePublicRouteCatalog.SavedQueriesGet,
            $"/api/v1/projects/{Escape(projectId)}/saved-queries/{Escape(savedQueryId)}");

    public static TerraScaleBoundRoute ProjectSavedQueryUpdate(string projectId, string savedQueryId) =>
        Bind(
            TerraScalePublicRouteCatalog.SavedQueriesUpdate,
            $"/api/v1/projects/{Escape(projectId)}/saved-queries/{Escape(savedQueryId)}");

    public static TerraScaleBoundRoute ProjectSavedQueryDelete(string projectId, string savedQueryId) =>
        Bind(
            TerraScalePublicRouteCatalog.SavedQueriesDelete,
            $"/api/v1/projects/{Escape(projectId)}/saved-queries/{Escape(savedQueryId)}");

    public static TerraScaleBoundRoute ProjectAuditList(string projectId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.AuditList,
            WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/audit",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute ProjectAuditGet(string projectId, string auditEventId) =>
        Bind(
            TerraScalePublicRouteCatalog.AuditGet,
            $"/api/v1/projects/{Escape(projectId)}/audit/{Escape(auditEventId)}");

    public static TerraScaleBoundRoute ProjectQueuesList(string projectId) =>
        Bind(TerraScalePublicRouteCatalog.QueuesList, $"/api/v1/projects/{Escape(projectId)}/queues");

    public static TerraScaleBoundRoute ProjectQueuesCreate(string projectId) =>
        Bind(TerraScalePublicRouteCatalog.QueuesCreate, $"/api/v1/projects/{Escape(projectId)}/queues");

    public static TerraScaleBoundRoute ProjectQueueGet(string projectId, string queueId) =>
        Bind(
            TerraScalePublicRouteCatalog.QueuesGet,
            $"/api/v1/projects/{Escape(projectId)}/queues/{Escape(queueId)}");

    public static TerraScaleBoundRoute ProjectQueueDelete(string projectId, string queueId) =>
        Bind(
            TerraScalePublicRouteCatalog.QueuesDelete,
            $"/api/v1/projects/{Escape(projectId)}/queues/{Escape(queueId)}");

    public static TerraScaleBoundRoute ProjectQueuePurge(string projectId, string queueId) =>
        Bind(
            TerraScalePublicRouteCatalog.QueuesPurge,
            $"/api/v1/projects/{Escape(projectId)}/queues/{Escape(queueId)}:purge");

    public static TerraScaleBoundRoute ProjectQueuePublish(string projectId, string queueId) =>
        Bind(
            TerraScalePublicRouteCatalog.QueuesPublish,
            $"/api/v1/projects/{Escape(projectId)}/queues/{Escape(queueId)}:publish");

    public static TerraScaleBoundRoute ProjectQueuePeek(string projectId, string queueId, int maxMessages) =>
        Bind(
            TerraScalePublicRouteCatalog.QueuesPeek,
            WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/queues/{Escape(queueId)}:peek",
                ("maxMessages", Number(maxMessages))));

    public static TerraScaleBoundRoute ProjectQueueReceive(string projectId, string queueId) =>
        Bind(
            TerraScalePublicRouteCatalog.QueuesReceive,
            $"/api/v1/projects/{Escape(projectId)}/queues/{Escape(queueId)}:receive");

    public static TerraScaleBoundRoute ProjectQueueComplete(string projectId, string queueId) =>
        Bind(
            TerraScalePublicRouteCatalog.QueuesComplete,
            $"/api/v1/projects/{Escape(projectId)}/queues/{Escape(queueId)}:complete");

    public static TerraScaleBoundRoute ProjectQueueAbandon(string projectId, string queueId) =>
        Bind(
            TerraScalePublicRouteCatalog.QueuesAbandon,
            $"/api/v1/projects/{Escape(projectId)}/queues/{Escape(queueId)}:abandon");

    public static TerraScaleBoundRoute ProjectQueueDeadLetter(string projectId, string queueId) =>
        Bind(
            TerraScalePublicRouteCatalog.QueuesDeadLetter,
            $"/api/v1/projects/{Escape(projectId)}/queues/{Escape(queueId)}:dead-letter");

    public static TerraScaleBoundRoute ProjectQueueRenewLock(string projectId, string queueId) =>
        Bind(
            TerraScalePublicRouteCatalog.QueuesRenewLock,
            $"/api/v1/projects/{Escape(projectId)}/queues/{Escape(queueId)}:renew-lock");

    public static TerraScaleBoundRoute EnvironmentsCreate(string projectId, string databaseId) =>
        Bind(
            TerraScalePublicRouteCatalog.DatabasesEnvironmentsCreate,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/environments");

    public static TerraScaleBoundRoute EnvironmentsList(string projectId, string databaseId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.DatabasesEnvironmentsList,
            WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/environments",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute EnvironmentGet(string projectId, string databaseId, string environmentId) =>
        Bind(
            TerraScalePublicRouteCatalog.DatabasesEnvironmentsGet,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/environments/{Escape(environmentId)}");

    public static TerraScaleBoundRoute EnvironmentDelete(string projectId, string databaseId, string environmentId) =>
        Bind(
            TerraScalePublicRouteCatalog.DatabasesEnvironmentsDelete,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/environments/{Escape(environmentId)}");

    public static TerraScaleBoundRoute RepositoriesList() =>
        Bind(TerraScalePublicRouteCatalog.RepositoriesList, "/api/v1/repositories");

    public static TerraScaleBoundRoute RepositoriesCreate() =>
        Bind(TerraScalePublicRouteCatalog.RepositoriesCreate, "/api/v1/repositories");

    public static TerraScaleBoundRoute RepositoryGet(string repositoryId) =>
        Bind(TerraScalePublicRouteCatalog.RepositoriesGet, $"/api/v1/repositories/{Escape(repositoryId)}");

    public static TerraScaleBoundRoute RepositoryDelete(string repositoryId, bool deleteDocuments) =>
        Bind(
            TerraScalePublicRouteCatalog.RepositoriesDelete,
            WithQuery(
                $"/api/v1/repositories/{Escape(repositoryId)}",
                ("deleteDocuments", Boolean(deleteDocuments))));

    public static TerraScaleBoundRoute DocumentsList(string repositoryId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.DocumentsList,
            WithQuery(
                $"/api/v1/repositories/{Escape(repositoryId)}/documents",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute DocumentsCreate(string repositoryId) =>
        Bind(
            TerraScalePublicRouteCatalog.DocumentsCreate,
            $"/api/v1/repositories/{Escape(repositoryId)}/documents");

    public static TerraScaleBoundRoute DocumentGet(string repositoryId, string documentId) =>
        Bind(
            TerraScalePublicRouteCatalog.DocumentsGet,
            $"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute DocumentPut(string repositoryId, string documentId) =>
        Bind(
            TerraScalePublicRouteCatalog.DocumentsPut,
            $"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute DocumentPatch(string repositoryId, string documentId) =>
        Bind(
            TerraScalePublicRouteCatalog.DocumentsPatch,
            $"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute DocumentsBatch(string repositoryId) =>
        Bind(
            TerraScalePublicRouteCatalog.DocumentsBatch,
            $"/api/v1/repositories/{Escape(repositoryId)}/documents/batch");

    public static TerraScaleBoundRoute DocumentDelete(string repositoryId, string documentId) =>
        Bind(
            TerraScalePublicRouteCatalog.DocumentsDelete,
            $"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute QueryExecute(string repositoryId) =>
        Bind(
            TerraScalePublicRouteCatalog.QueryExecute,
            $"/api/v1/repositories/{Escape(repositoryId)}/tql");

    public static TerraScaleBoundRoute SchemaGet(string repositoryId) =>
        Bind(
            TerraScalePublicRouteCatalog.SchemaGet,
            $"/api/v1/repositories/{Escape(repositoryId)}/schema");

    public static TerraScaleBoundRoute SchemaValidate(string repositoryId) =>
        Bind(
            TerraScalePublicRouteCatalog.SchemaValidate,
            $"/api/v1/repositories/{Escape(repositoryId)}/schema/validate");

    public static TerraScaleBoundRoute SchemaConfigure(string repositoryId) =>
        Bind(
            TerraScalePublicRouteCatalog.SchemaConfigure,
            $"/api/v1/repositories/{Escape(repositoryId)}/schema");

    public static TerraScaleBoundRoute ProjectUsageGet(string projectId) =>
        Bind(TerraScalePublicRouteCatalog.UsageProjectGet, $"/api/v1/projects/{Escape(projectId)}/usage");

    public static TerraScaleBoundRoute ProjectUsageWorkloadGet(string projectId, string? range) =>
        Bind(
            TerraScalePublicRouteCatalog.UsageProjectWorkload,
            WithQuery($"/api/v1/projects/{Escape(projectId)}/usage/workload", ("range", NormalizeOptional(range))));

    public static TerraScaleBoundRoute ProjectUsageHistoryGet(string projectId, string? range) =>
        Bind(
            TerraScalePublicRouteCatalog.UsageProjectHistory,
            WithQuery($"/api/v1/projects/{Escape(projectId)}/usage/history", ("range", NormalizeOptional(range))));

    public static TerraScaleBoundRoute ProjectLatencyGet(string projectId, double? latitude, double? longitude) =>
        Bind(
            TerraScalePublicRouteCatalog.UsageProjectLatency,
            WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/latency",
                ("latitude", Number(latitude)),
                ("longitude", Number(longitude))));

    public static TerraScaleBoundRoute OrganizationUsageGet(string organizationId) =>
        Bind(
            TerraScalePublicRouteCatalog.UsageOrganizationGet,
            $"/api/v1/organizations/{Escape(organizationId)}/usage");

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
        Bind(
            TerraScalePublicRouteCatalog.NotificationsList,
            WithQuery(
                "/api/v1/users/me/notifications",
                ("skip", Number(skip)),
                ("take", Number(take)),
                ("unreadOnly", Boolean(unreadOnly)),
                ("organizationId", NormalizeOptional(organizationId)),
                ("projectId", NormalizeOptional(projectId)),
                ("includeOrganizationNotifications", Boolean(includeOrganizationNotifications))));

    public static TerraScaleBoundRoute NotificationMarkRead(string notificationId) =>
        Bind(
            TerraScalePublicRouteCatalog.NotificationsMarkRead,
            $"/api/v1/users/me/notifications/{Escape(notificationId)}:mark-read");

    public static TerraScaleBoundRoute NotificationsMarkAllRead(string? organizationId = null) =>
        Bind(
            TerraScalePublicRouteCatalog.NotificationsMarkAllRead,
            WithQuery(
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
        Bind(
            TerraScalePublicRouteCatalog.BackupsPolicyConfigure,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups/policy");

    public static TerraScaleBoundRoute BackupsPlan(string projectId, string databaseId) =>
        Bind(
            TerraScalePublicRouteCatalog.BackupsPlan,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups:plan");

    public static TerraScaleBoundRoute BackupsExecute(string projectId, string databaseId) =>
        Bind(
            TerraScalePublicRouteCatalog.BackupsExecute,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups:execute");

    public static TerraScaleBoundRoute BackupsList(string projectId, string databaseId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.BackupsList,
            WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute BackupGet(string projectId, string databaseId, string backupId) =>
        Bind(
            TerraScalePublicRouteCatalog.BackupsGet,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups/{Escape(backupId)}");

    public static TerraScaleBoundRoute RestorePointsList(string projectId, string databaseId) =>
        Bind(
            TerraScalePublicRouteCatalog.BackupsRestorePointsList,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restore-points");

    public static TerraScaleBoundRoute RestoresCreate(string projectId, string databaseId) =>
        Bind(
            TerraScalePublicRouteCatalog.BackupsRestoresStart,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores");

    public static TerraScaleBoundRoute RestoresList(string projectId, string databaseId, int skip, int take) =>
        Bind(
            TerraScalePublicRouteCatalog.BackupsRestoresList,
            WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute RestoreGet(string projectId, string databaseId, string restoreId) =>
        Bind(
            TerraScalePublicRouteCatalog.BackupsRestoresGet,
            $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores/{Escape(restoreId)}");

    private static TerraScaleBoundRoute Bind(TerraScalePublicRoute definition, string requestUri) =>
        definition.Bind(requestUri);

    private static TerraScaleBoundRoute OrganizationInvitationAction(
        TerraScalePublicRoute definition,
        string organizationId,
        string invitationId,
        string action) =>
        Bind(
            definition,
            $"/api/v1/organizations/{Escape(organizationId)}/invitations/{Escape(invitationId)}:{action}");

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
