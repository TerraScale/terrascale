namespace TerraScale.DotNet;

internal sealed record TerraScalePublicRoute(
    string RouteName,
    HttpMethod Method,
    string PathTemplate,
    string OperationId)
{
    public TerraScaleBoundRoute Bind(string requestUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUri);
        var queryIndex = requestUri.IndexOf('?', StringComparison.Ordinal);
        var requestPath = queryIndex < 0 ? requestUri : requestUri[..queryIndex];
        if (!MatchesTemplate(requestPath))
        {
            throw new InvalidOperationException(
                $"Route {RouteName} produced '{requestPath}' instead of template '{PathTemplate}'.");
        }

        return new TerraScaleBoundRoute(this, requestUri);
    }

    private bool MatchesTemplate(string requestPath)
    {
        var templateIndex = 0;
        var pathIndex = 0;
        while (true)
        {
            var placeholderStart = PathTemplate.IndexOf('{', templateIndex);
            if (placeholderStart < 0)
            {
                return requestPath.AsSpan(pathIndex).SequenceEqual(PathTemplate.AsSpan(templateIndex));
            }

            var literal = PathTemplate.AsSpan(templateIndex, placeholderStart - templateIndex);
            if (!requestPath.AsSpan(pathIndex).StartsWith(literal, StringComparison.Ordinal))
            {
                return false;
            }

            pathIndex += literal.Length;
            var placeholderEnd = PathTemplate.IndexOf('}', placeholderStart + 1);
            if (placeholderEnd < 0 || placeholderEnd == placeholderStart + 1)
            {
                return false;
            }

            var nextPlaceholder = PathTemplate.IndexOf('{', placeholderEnd + 1);
            var nextLiteralEnd = nextPlaceholder < 0 ? PathTemplate.Length : nextPlaceholder;
            var nextLiteral = PathTemplate.AsSpan(placeholderEnd + 1, nextLiteralEnd - placeholderEnd - 1);
            if (nextLiteral.IsEmpty)
            {
                return nextPlaceholder < 0 && pathIndex < requestPath.Length;
            }

            var nextLiteralOffset = requestPath.AsSpan(pathIndex).IndexOf(nextLiteral, StringComparison.Ordinal);
            if (nextLiteralOffset <= 0)
            {
                return false;
            }

            pathIndex += nextLiteralOffset;
            templateIndex = placeholderEnd + 1;
        }
    }
}

internal readonly record struct TerraScaleBoundRoute(
    TerraScalePublicRoute Definition,
    string RequestUri)
{
    public HttpMethod Method => Definition.Method;

    public override string ToString() => RequestUri;
}

internal static class TerraScalePublicRouteCatalog
{
    public static readonly TerraScalePublicRoute OnboardingGetState = new(
        "onboarding.getState", HttpMethod.Get, "/api/v1/onboarding", "GetOnboardingState");
    public static readonly TerraScalePublicRoute OnboardingOrganizationCreate = new(
        "onboarding.organization.create", HttpMethod.Post, "/api/v1/onboarding/organization", "CreateOnboardingOrganization");
    public static readonly TerraScalePublicRoute OnboardingProjectCreate = new(
        "onboarding.project.create", HttpMethod.Post, "/api/v1/onboarding/project", "CreateOnboardingProject");
    public static readonly TerraScalePublicRoute OrganizationsList = new(
        "organizations.list", HttpMethod.Get, "/api/v1/organizations", "ListOrganizations");
    public static readonly TerraScalePublicRoute OrganizationsCreate = new(
        "organizations.create", HttpMethod.Post, "/api/v1/organizations", "CreateOrganization");
    public static readonly TerraScalePublicRoute OrganizationsGet = new(
        "organizations.get", HttpMethod.Get, "/api/v1/organizations/{organizationId}", "GetOrganization");
    public static readonly TerraScalePublicRoute OrganizationsUpdate = new(
        "organizations.update", HttpMethod.Patch, "/api/v1/organizations/{organizationId}", "UpdateOrganization");
    public static readonly TerraScalePublicRoute OrganizationsDeletionRequest = new(
        "organizations.deletion.request", HttpMethod.Post, "/api/v1/organizations/{organizationId}:request-deletion", "RequestOrganizationDeletion");
    public static readonly TerraScalePublicRoute OrganizationsLeave = new(
        "organizations.leave", HttpMethod.Post, "/api/v1/organizations/{organizationId}:leave", "LeaveOrganization");
    public static readonly TerraScalePublicRoute OrganizationsMembersList = new(
        "organizations.members.list", HttpMethod.Get, "/api/v1/organizations/{organizationId}/members", "ListOrganizationMembers");
    public static readonly TerraScalePublicRoute OrganizationsMembersUpdateRole = new(
        "organizations.members.updateRole", HttpMethod.Patch, "/api/v1/organizations/{organizationId}/members/{userId}", "UpdateOrganizationMemberRole");
    public static readonly TerraScalePublicRoute OrganizationsMembersRemove = new(
        "organizations.members.remove", HttpMethod.Post, "/api/v1/organizations/{organizationId}/members/{userId}:remove", "RemoveOrganizationMember");
    public static readonly TerraScalePublicRoute OrganizationsInvitationsList = new(
        "organizations.invitations.list", HttpMethod.Get, "/api/v1/organizations/{organizationId}/invitations", "ListOrganizationInvitations");
    public static readonly TerraScalePublicRoute OrganizationsInvitationsCreate = new(
        "organizations.invitations.create", HttpMethod.Post, "/api/v1/organizations/{organizationId}/invitations", "CreateOrganizationInvitation");
    public static readonly TerraScalePublicRoute OrganizationsInvitationsAccept = new(
        "organizations.invitations.accept", HttpMethod.Post, "/api/v1/organizations/{organizationId}/invitations/{invitationId}:accept", "AcceptOrganizationInvitation");
    public static readonly TerraScalePublicRoute OrganizationsInvitationsDecline = new(
        "organizations.invitations.decline", HttpMethod.Post, "/api/v1/organizations/{organizationId}/invitations/{invitationId}:decline", "DeclineOrganizationInvitation");
    public static readonly TerraScalePublicRoute OrganizationsInvitationsResend = new(
        "organizations.invitations.resend", HttpMethod.Post, "/api/v1/organizations/{organizationId}/invitations/{invitationId}:resend", "ResendOrganizationInvitation");
    public static readonly TerraScalePublicRoute OrganizationsInvitationsCancel = new(
        "organizations.invitations.cancel", HttpMethod.Post, "/api/v1/organizations/{organizationId}/invitations/{invitationId}:cancel", "CancelOrganizationInvitation");
    public static readonly TerraScalePublicRoute OrganizationsProjectsList = new(
        "organizations.projects.list", HttpMethod.Get, "/api/v1/organizations/{organizationId}/projects", "ListOrganizationProjects");
    public static readonly TerraScalePublicRoute OrganizationsProjectsCreate = new(
        "organizations.projects.create", HttpMethod.Post, "/api/v1/organizations/{organizationId}/projects", "CreateProject");
    public static readonly TerraScalePublicRoute OrganizationsProjectsGet = new(
        "organizations.projects.get", HttpMethod.Get, "/api/v1/projects/{projectId}", "GetProject");
    public static readonly TerraScalePublicRoute OrganizationsProjectsRegionsList = new(
        "organizations.projects.regions.list", HttpMethod.Get, "/api/v1/projects/{projectId}/regions", "ListProjectRegionPlacements");
    public static readonly TerraScalePublicRoute OrganizationsProjectsUpdate = new(
        "organizations.projects.update", HttpMethod.Patch, "/api/v1/projects/{projectId}", "UpdateProject");
    public static readonly TerraScalePublicRoute OrganizationsProjectsDelete = new(
        "organizations.projects.delete", HttpMethod.Delete, "/api/v1/projects/{projectId}", "DeleteProject");
    public static readonly TerraScalePublicRoute RegionsList = new(
        "regions.list", HttpMethod.Get, "/api/v1/regions", "GetRegions");
    public static readonly TerraScalePublicRoute RegionsGet = new(
        "regions.get", HttpMethod.Get, "/api/v1/regions/{regionCode}", "GetRegion");
    public static readonly TerraScalePublicRoute SavedQueriesList = new(
        "savedQueries.list", HttpMethod.Get, "/api/v1/projects/{projectId}/saved-queries", "ListProjectSavedQueries");
    public static readonly TerraScalePublicRoute SavedQueriesCreate = new(
        "savedQueries.create", HttpMethod.Post, "/api/v1/projects/{projectId}/saved-queries", "CreateProjectSavedQuery");
    public static readonly TerraScalePublicRoute SavedQueriesGet = new(
        "savedQueries.get", HttpMethod.Get, "/api/v1/projects/{projectId}/saved-queries/{savedQueryId}", "GetProjectSavedQuery");
    public static readonly TerraScalePublicRoute SavedQueriesUpdate = new(
        "savedQueries.update", HttpMethod.Put, "/api/v1/projects/{projectId}/saved-queries/{savedQueryId}", "UpdateProjectSavedQuery");
    public static readonly TerraScalePublicRoute SavedQueriesDelete = new(
        "savedQueries.delete", HttpMethod.Delete, "/api/v1/projects/{projectId}/saved-queries/{savedQueryId}", "DeleteProjectSavedQuery");
    public static readonly TerraScalePublicRoute DatabasesList = new(
        "databases.list", HttpMethod.Get, "/api/v1/projects/{projectId}/databases", "ListProjectDatabases");
    public static readonly TerraScalePublicRoute DatabasesCreate = new(
        "databases.create", HttpMethod.Post, "/api/v1/projects/{projectId}/databases", "CreateProjectDatabase");
    public static readonly TerraScalePublicRoute DatabasesGet = new(
        "databases.get", HttpMethod.Get, "/api/v1/projects/{projectId}/databases/{databaseId}", "GetProjectDatabase");
    public static readonly TerraScalePublicRoute DatabasesDelete = new(
        "databases.delete", HttpMethod.Delete, "/api/v1/projects/{projectId}/databases/{databaseId}", "DeleteProjectDatabase");
    public static readonly TerraScalePublicRoute DatabaseBranchesGetCapability = new(
        "databaseBranches.getCapability", HttpMethod.Get, "/api/v1/projects/{projectId}/database-branches/capability", "GetDatabaseBranchCapability");
    public static readonly TerraScalePublicRoute DatabaseBranchesRequestClone = new(
        "databaseBranches.requestClone", HttpMethod.Post, "/api/v1/projects/{projectId}/database-branches", "CreateDatabaseBranch");
    public static readonly TerraScalePublicRoute QueuesList = new(
        "queues.list", HttpMethod.Get, "/api/v1/projects/{projectId}/queues", "ListProjectQueues");
    public static readonly TerraScalePublicRoute QueuesCreate = new(
        "queues.create", HttpMethod.Post, "/api/v1/projects/{projectId}/queues", "CreateProjectQueue");
    public static readonly TerraScalePublicRoute QueuesGet = new(
        "queues.get", HttpMethod.Get, "/api/v1/projects/{projectId}/queues/{queueId}", "GetProjectQueue");
    public static readonly TerraScalePublicRoute QueuesDelete = new(
        "queues.delete", HttpMethod.Delete, "/api/v1/projects/{projectId}/queues/{queueId}", "DeleteProjectQueue");
    public static readonly TerraScalePublicRoute QueuesPurge = new(
        "queues.purge", HttpMethod.Post, "/api/v1/projects/{projectId}/queues/{queueId}:purge", "PurgeProjectQueue");
    public static readonly TerraScalePublicRoute QueuesPublish = new(
        "queues.publish", HttpMethod.Post, "/api/v1/projects/{projectId}/queues/{queueId}:publish", "PublishProjectQueueMessage");
    public static readonly TerraScalePublicRoute QueuesPeek = new(
        "queues.peek", HttpMethod.Get, "/api/v1/projects/{projectId}/queues/{queueId}:peek", "PeekProjectQueue");
    public static readonly TerraScalePublicRoute QueuesReceive = new(
        "queues.receive", HttpMethod.Post, "/api/v1/projects/{projectId}/queues/{queueId}:receive", "ReceiveProjectQueueMessages");
    public static readonly TerraScalePublicRoute QueuesComplete = new(
        "queues.complete", HttpMethod.Post, "/api/v1/projects/{projectId}/queues/{queueId}:complete", "CompleteProjectQueueMessage");
    public static readonly TerraScalePublicRoute QueuesAbandon = new(
        "queues.abandon", HttpMethod.Post, "/api/v1/projects/{projectId}/queues/{queueId}:abandon", "AbandonProjectQueueMessage");
    public static readonly TerraScalePublicRoute QueuesDeadLetter = new(
        "queues.deadLetter", HttpMethod.Post, "/api/v1/projects/{projectId}/queues/{queueId}:dead-letter", "DeadLetterProjectQueueMessage");
    public static readonly TerraScalePublicRoute QueuesRenewLock = new(
        "queues.renewLock", HttpMethod.Post, "/api/v1/projects/{projectId}/queues/{queueId}:renew-lock", "RenewProjectQueueMessageLock");
    public static readonly TerraScalePublicRoute DatabasesEnvironmentsList = new(
        "databases.environments.list", HttpMethod.Get, "/api/v1/projects/{projectId}/databases/{databaseId}/environments", "ListProjectDatabaseEnvironments");
    public static readonly TerraScalePublicRoute DatabasesEnvironmentsCreate = new(
        "databases.environments.create", HttpMethod.Post, "/api/v1/projects/{projectId}/databases/{databaseId}/environments", "CreateProjectDatabaseEnvironment");
    public static readonly TerraScalePublicRoute DatabasesEnvironmentsGet = new(
        "databases.environments.get", HttpMethod.Get, "/api/v1/projects/{projectId}/databases/{databaseId}/environments/{environmentId}", "GetProjectDatabaseEnvironment");
    public static readonly TerraScalePublicRoute DatabasesEnvironmentsDelete = new(
        "databases.environments.delete", HttpMethod.Delete, "/api/v1/projects/{projectId}/databases/{databaseId}/environments/{environmentId}", "DeleteProjectDatabaseEnvironment");
    public static readonly TerraScalePublicRoute RepositoriesList = new(
        "repositories.list", HttpMethod.Get, "/api/v1/repositories", "ListRepositories");
    public static readonly TerraScalePublicRoute RepositoriesCreate = new(
        "repositories.create", HttpMethod.Post, "/api/v1/repositories", "CreateRepository");
    public static readonly TerraScalePublicRoute RepositoriesGet = new(
        "repositories.get", HttpMethod.Get, "/api/v1/repositories/{repositoryId}", "GetRepository");
    public static readonly TerraScalePublicRoute RepositoriesDelete = new(
        "repositories.delete", HttpMethod.Delete, "/api/v1/repositories/{repositoryId}", "DeleteRepository");
    public static readonly TerraScalePublicRoute BackupsPolicyConfigure = new(
        "backups.policy.configure", HttpMethod.Put, "/api/v1/projects/{projectId}/databases/{databaseId}/backups/policy", "ConfigureDatabaseBackupPolicy");
    public static readonly TerraScalePublicRoute BackupsPlan = new(
        "backups.plan", HttpMethod.Post, "/api/v1/projects/{projectId}/databases/{databaseId}/backups:plan", "PlanDatabaseBackup");
    public static readonly TerraScalePublicRoute BackupsExecute = new(
        "backups.execute", HttpMethod.Post, "/api/v1/projects/{projectId}/databases/{databaseId}/backups:execute", "ExecuteDatabaseBackup");
    public static readonly TerraScalePublicRoute BackupsList = new(
        "backups.list", HttpMethod.Get, "/api/v1/projects/{projectId}/databases/{databaseId}/backups", "ListDatabaseBackups");
    public static readonly TerraScalePublicRoute BackupsGet = new(
        "backups.get", HttpMethod.Get, "/api/v1/projects/{projectId}/databases/{databaseId}/backups/{backupId}", "GetDatabaseBackup");
    public static readonly TerraScalePublicRoute BackupsRestorePointsList = new(
        "backups.restorePoints.list", HttpMethod.Get, "/api/v1/projects/{projectId}/databases/{databaseId}/restore-points", "ListDatabaseRestorePoints");
    public static readonly TerraScalePublicRoute BackupsRestoresStart = new(
        "backups.restores.start", HttpMethod.Post, "/api/v1/projects/{projectId}/databases/{databaseId}/restores", "StartDatabaseRestore");
    public static readonly TerraScalePublicRoute BackupsRestoresGet = new(
        "backups.restores.get", HttpMethod.Get, "/api/v1/projects/{projectId}/databases/{databaseId}/restores/{restoreId}", "GetDatabaseRestore");
    public static readonly TerraScalePublicRoute BackupsRestoresList = new(
        "backups.restores.list", HttpMethod.Get, "/api/v1/projects/{projectId}/databases/{databaseId}/restores", "ListDatabaseRestores");
    public static readonly TerraScalePublicRoute DocumentsList = new(
        "documents.list", HttpMethod.Get, "/api/v1/repositories/{repositoryId}/documents", "ListDocuments");
    public static readonly TerraScalePublicRoute DocumentsCreate = new(
        "documents.create", HttpMethod.Post, "/api/v1/repositories/{repositoryId}/documents", "CreateDocument");
    public static readonly TerraScalePublicRoute DocumentsGet = new(
        "documents.get", HttpMethod.Get, "/api/v1/repositories/{repositoryId}/documents/{documentId}", "GetDocument");
    public static readonly TerraScalePublicRoute DocumentsPut = new(
        "documents.put", HttpMethod.Put, "/api/v1/repositories/{repositoryId}/documents/{documentId}", "UpsertDocument");
    public static readonly TerraScalePublicRoute DocumentsPatch = new(
        "documents.patch", HttpMethod.Patch, "/api/v1/repositories/{repositoryId}/documents/{documentId}", "PatchDocument");
    public static readonly TerraScalePublicRoute DocumentsBatch = new(
        "documents.batch", HttpMethod.Post, "/api/v1/repositories/{repositoryId}/documents/batch", "BatchDocuments");
    public static readonly TerraScalePublicRoute DocumentsDelete = new(
        "documents.delete", HttpMethod.Delete, "/api/v1/repositories/{repositoryId}/documents/{documentId}", "DeleteDocument");
    public static readonly TerraScalePublicRoute QueryExecute = new(
        "query.execute", HttpMethod.Post, "/api/v1/repositories/{repositoryId}/tql", "QueryDocuments");
    public static readonly TerraScalePublicRoute SchemaGet = new(
        "schema.get", HttpMethod.Get, "/api/v1/repositories/{repositoryId}/schema", "GetRepositorySchema");
    public static readonly TerraScalePublicRoute SchemaValidate = new(
        "schema.validate", HttpMethod.Post, "/api/v1/repositories/{repositoryId}/schema/validate", "ValidateRepositorySchema");
    public static readonly TerraScalePublicRoute SchemaConfigure = new(
        "schema.configure", HttpMethod.Put, "/api/v1/repositories/{repositoryId}/schema", "ConfigureRepositorySchema");
    public static readonly TerraScalePublicRoute AccessTeamMembersList = new(
        "access.teamMembers.list", HttpMethod.Get, "/api/v1/projects/{projectId}/access/team-members", "ListProjectTeamMembers");
    public static readonly TerraScalePublicRoute AccessTeamMembersAdd = new(
        "access.teamMembers.add", HttpMethod.Post, "/api/v1/projects/{projectId}/access/team-members", "AddProjectTeamMember");
    public static readonly TerraScalePublicRoute AccessTeamMembersUpdate = new(
        "access.teamMembers.update", HttpMethod.Patch, "/api/v1/projects/{projectId}/access/team-members/{userId}", "UpdateProjectTeamMember");
    public static readonly TerraScalePublicRoute AccessTeamMembersRemove = new(
        "access.teamMembers.remove", HttpMethod.Delete, "/api/v1/projects/{projectId}/access/team-members/{userId}", "RemoveProjectTeamMember");
    public static readonly TerraScalePublicRoute UsageProjectGet = new(
        "usage.project.get", HttpMethod.Get, "/api/v1/projects/{projectId}/usage", "GetProjectUsage");
    public static readonly TerraScalePublicRoute UsageOrganizationGet = new(
        "usage.organization.get", HttpMethod.Get, "/api/v1/organizations/{organizationId}/usage", "GetOrganizationUsage");
    public static readonly TerraScalePublicRoute UsageProjectWorkload = new(
        "usage.project.workload", HttpMethod.Get, "/api/v1/projects/{projectId}/usage/workload", "GetProjectUsageWorkload");
    public static readonly TerraScalePublicRoute UsageProjectLatency = new(
        "usage.project.latency", HttpMethod.Get, "/api/v1/projects/{projectId}/latency", "GetProjectLatency");
    public static readonly TerraScalePublicRoute UsageProjectHistory = new(
        "usage.project.history", HttpMethod.Get, "/api/v1/projects/{projectId}/usage/history", "GetProjectUsageHistory");
    public static readonly TerraScalePublicRoute AuditList = new(
        "audit.list", HttpMethod.Get, "/api/v1/projects/{projectId}/audit", "ListProjectAuditEvents");
    public static readonly TerraScalePublicRoute AuditGet = new(
        "audit.get", HttpMethod.Get, "/api/v1/projects/{projectId}/audit/{auditEventId}", "GetProjectAuditEvent");
    public static readonly TerraScalePublicRoute NotificationsList = new(
        "notifications.list", HttpMethod.Get, "/api/v1/users/me/notifications", "ListUserNotifications");
    public static readonly TerraScalePublicRoute NotificationsMarkRead = new(
        "notifications.markRead", HttpMethod.Post, "/api/v1/users/me/notifications/{notificationId}:mark-read", "MarkUserNotificationRead");
    public static readonly TerraScalePublicRoute NotificationsMarkAllRead = new(
        "notifications.markAllRead", HttpMethod.Post, "/api/v1/users/me/notifications:mark-all-read", "MarkAllUserNotificationsRead");

    public static IReadOnlyList<TerraScalePublicRoute> All { get; } =
    [
        OnboardingGetState,
        OnboardingOrganizationCreate,
        OnboardingProjectCreate,
        OrganizationsList,
        OrganizationsCreate,
        OrganizationsGet,
        OrganizationsUpdate,
        OrganizationsDeletionRequest,
        OrganizationsLeave,
        OrganizationsMembersList,
        OrganizationsMembersUpdateRole,
        OrganizationsMembersRemove,
        OrganizationsInvitationsList,
        OrganizationsInvitationsCreate,
        OrganizationsInvitationsAccept,
        OrganizationsInvitationsDecline,
        OrganizationsInvitationsResend,
        OrganizationsInvitationsCancel,
        OrganizationsProjectsList,
        OrganizationsProjectsCreate,
        OrganizationsProjectsGet,
        OrganizationsProjectsRegionsList,
        OrganizationsProjectsUpdate,
        OrganizationsProjectsDelete,
        RegionsList,
        RegionsGet,
        SavedQueriesList,
        SavedQueriesCreate,
        SavedQueriesGet,
        SavedQueriesUpdate,
        SavedQueriesDelete,
        DatabasesList,
        DatabasesCreate,
        DatabasesGet,
        DatabasesDelete,
        DatabaseBranchesGetCapability,
        DatabaseBranchesRequestClone,
        QueuesList,
        QueuesCreate,
        QueuesGet,
        QueuesDelete,
        QueuesPurge,
        QueuesPublish,
        QueuesPeek,
        QueuesReceive,
        QueuesComplete,
        QueuesAbandon,
        QueuesDeadLetter,
        QueuesRenewLock,
        DatabasesEnvironmentsList,
        DatabasesEnvironmentsCreate,
        DatabasesEnvironmentsGet,
        DatabasesEnvironmentsDelete,
        RepositoriesList,
        RepositoriesCreate,
        RepositoriesGet,
        RepositoriesDelete,
        BackupsPolicyConfigure,
        BackupsPlan,
        BackupsExecute,
        BackupsList,
        BackupsGet,
        BackupsRestorePointsList,
        BackupsRestoresStart,
        BackupsRestoresGet,
        BackupsRestoresList,
        DocumentsList,
        DocumentsCreate,
        DocumentsGet,
        DocumentsPut,
        DocumentsPatch,
        DocumentsBatch,
        DocumentsDelete,
        QueryExecute,
        SchemaGet,
        SchemaValidate,
        SchemaConfigure,
        AccessTeamMembersList,
        AccessTeamMembersAdd,
        AccessTeamMembersUpdate,
        AccessTeamMembersRemove,
        UsageProjectGet,
        UsageOrganizationGet,
        UsageProjectWorkload,
        UsageProjectLatency,
        UsageProjectHistory,
        AuditList,
        AuditGet,
        NotificationsList,
        NotificationsMarkRead,
        NotificationsMarkAllRead
    ];
}
