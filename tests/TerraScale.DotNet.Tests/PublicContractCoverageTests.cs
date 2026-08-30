using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class PublicContractCoverageTests
{
    [Test]
    public async Task CompiledRouteCatalog_MatchesEveryDeclaredBinding()
    {
        var repositoryRoot = FindRepositoryRoot();
        await using var bindingsStream = File.OpenRead(Path.Combine(
            repositoryRoot,
            "tests",
            "TerraScale.DotNet.Tests",
            "contracts",
            "dotnet-public-route-bindings.v1.json"));
        using var bindings = await JsonDocument.ParseAsync(bindingsStream);
        var declaredRoutes = bindings.RootElement.GetProperty("routes").EnumerateArray().ToArray();
        var compiledRoutes = CompiledRoutes();
        var compiledByName = compiledRoutes.ToDictionary(
            static route => route.Definition.RouteName,
            StringComparer.Ordinal);
        var catalogByName = TerraScalePublicRouteCatalog.All.ToDictionary(
            static route => route.RouteName,
            StringComparer.Ordinal);

        await Assert.That(compiledRoutes.Count).IsEqualTo(91);
        await Assert.That(compiledByName.Count).IsEqualTo(compiledRoutes.Count);
        await Assert.That(catalogByName.Count).IsEqualTo(compiledRoutes.Count);
        await Assert.That(declaredRoutes.Length).IsEqualTo(compiledRoutes.Count);
        foreach (var declared in declaredRoutes)
        {
            var routeName = declared.GetProperty("routeName").GetString()
                ?? throw new InvalidDataException("A declared route name is null.");
            await Assert.That(compiledByName.ContainsKey(routeName)).IsTrue();
            var compiled = compiledByName[routeName];
            await Assert.That(catalogByName[routeName]).IsEqualTo(compiled.Definition);

            await Assert.That(compiled.Method.Method)
                .IsEqualTo(declared.GetProperty("method").GetString());
            await Assert.That(compiled.Definition.PathTemplate)
                .IsEqualTo(declared.GetProperty("pathTemplate").GetString());
            await Assert.That(compiled.Definition.OperationId)
                .IsEqualTo(declared.GetProperty("operationId").GetString());
            await Assert.That(compiled.RequestUri).StartsWith("/api/v1/");
        }
    }

    [Test]
    public async Task CallablePublicOperations_MatchEveryDeclaredBinding()
    {
        var repositoryRoot = FindRepositoryRoot();
        await using var bindingsStream = File.OpenRead(Path.Combine(
            repositoryRoot,
            "tests",
            "TerraScale.DotNet.Tests",
            "contracts",
            "dotnet-public-route-bindings.v1.json"));
        using var bindings = await JsonDocument.ParseAsync(bindingsStream);
        var declaredRoutes = bindings.RootElement.GetProperty("routes").EnumerateArray().ToArray();
        using var handler = new RouteCaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                BearerToken = "caller-token"
            });
        var operations = CallableOperations();
        var captured = new Dictionary<string, CapturedRoute>(StringComparer.Ordinal);

        await Assert.That(operations.Count).IsEqualTo(91);
        await Assert.That(operations.Select(static operation => operation.RouteName).Distinct(StringComparer.Ordinal).Count())
            .IsEqualTo(operations.Count);
        foreach (var operation in operations)
        {
            var requestCount = handler.Requests.Count;
            try
            {
                await operation.Invoke(client);
                throw new InvalidOperationException(
                    $"Callable route '{operation.RouteName}' unexpectedly accepted the capture response.");
            }
            catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.ExpectationFailed)
            {
            }

            if (handler.Requests.Count != requestCount + 1)
            {
                throw new InvalidOperationException(
                    $"Callable route '{operation.RouteName}' did not send exactly one request.");
            }

            captured.Add(operation.RouteName, handler.Requests[^1]);
        }

        await Assert.That(captured.Count).IsEqualTo(declaredRoutes.Length);
        foreach (var declared in declaredRoutes)
        {
            var routeName = declared.GetProperty("routeName").GetString()
                ?? throw new InvalidDataException("A declared route name is null.");
            await Assert.That(captured.ContainsKey(routeName)).IsTrue();
            var actual = captured[routeName];
            await Assert.That(actual.Method)
                .IsEqualTo(declared.GetProperty("method").GetString());
            await Assert.That(actual.Path)
                .IsEqualTo(BindSamplePath(declared.GetProperty("pathTemplate").GetString()
                    ?? throw new InvalidDataException($"Route '{routeName}' has a null path template.")));
        }
    }

    [Test]
    public async Task CurrentSnapshot_ReportsTheExactCanonicalCoverageAndMissingRoutes()
    {
        var result = await RunVerifierAsync();

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.StandardOutput).Contains(".NET public route coverage: 91/91 bound; 0 missing");
        foreach (var routeName in MissingRouteNames)
        {
            await Assert.That(result.StandardOutput).Contains($"  {routeName}\n");
        }
    }

    [Test]
    public async Task StrictMode_RejectsAnUnboundOrUnidentifiedOwnerContract()
    {
        var result = await RunVerifierAsync("--strict");

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.StandardError)
            .Contains("strict public contract coverage requires the exact external route manifest, contract descriptor, and owner OpenAPI fixture");
    }

    [Test]
    public async Task PublicSdkOmitsUnpublishedSavedQueryVersionRoutes()
    {
        var exportedTypeNames = typeof(TerraScaleClient).Assembly
            .GetExportedTypes()
            .Select(static type => type.FullName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var forbiddenType in new[]
                 {
                     "TerraScale.DotNet.TerraScaleSavedQueriesV2Client",
                     "TerraScale.DotNet.TerraScaleSavedQueriesV3Client",
                     "TerraScale.DotNet.TerraScaleUpdateSavedQueryV2Request",
                     "TerraScale.DotNet.TerraScaleSavedQueryV2",
                     "TerraScale.DotNet.TerraScaleSavedQuerySnapshotPage"
                 })
        {
            await Assert.That(exportedTypeNames.Contains(forbiddenType)).IsFalse();
        }

        var clientProperties = typeof(TerraScaleClient)
            .GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        await Assert.That(clientProperties.Contains("SavedQueriesV2")).IsFalse();
        await Assert.That(clientProperties.Contains("SavedQueriesV3")).IsFalse();

        var sdkSource = Path.Combine(FindRepositoryRoot(), "src", "TerraScale.DotNet");
        foreach (var sourcePath in Directory.EnumerateFiles(sdkSource, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var source = await File.ReadAllTextAsync(sourcePath);
            await Assert.That(source.Contains("/api/v2/", StringComparison.Ordinal)).IsFalse();
            await Assert.That(source.Contains("/api/v3/", StringComparison.Ordinal)).IsFalse();
        }
    }

    private static readonly string[] MissingRouteNames = [];

    private static IReadOnlyList<TerraScaleBoundRoute> CompiledRoutes() =>
    [
        TerraScaleApiRoutes.OnboardingGetState(),
        TerraScaleApiRoutes.OnboardingOrganizationCreate(),
        TerraScaleApiRoutes.OnboardingProjectCreate(),
        TerraScaleApiRoutes.OrganizationsList(0, 100),
        TerraScaleApiRoutes.OrganizationsCreate(),
        TerraScaleApiRoutes.OrganizationGet("organization-a"),
        TerraScaleApiRoutes.OrganizationUpdate("organization-a"),
        TerraScaleApiRoutes.OrganizationDeletionRequest("organization-a"),
        TerraScaleApiRoutes.OrganizationLeave("organization-a"),
        TerraScaleApiRoutes.OrganizationMembersList("organization-a", 0, 100),
        TerraScaleApiRoutes.OrganizationMemberUpdate("organization-a", "user-a"),
        TerraScaleApiRoutes.OrganizationMemberRemove("organization-a", "user-a"),
        TerraScaleApiRoutes.OrganizationInvitationsList("organization-a", 0, 100),
        TerraScaleApiRoutes.OrganizationInvitationCreate("organization-a"),
        TerraScaleApiRoutes.OrganizationInvitationAccept("organization-a", "invitation-a"),
        TerraScaleApiRoutes.OrganizationInvitationDecline("organization-a", "invitation-a"),
        TerraScaleApiRoutes.OrganizationInvitationResend("organization-a", "invitation-a"),
        TerraScaleApiRoutes.OrganizationInvitationCancel("organization-a", "invitation-a"),
        TerraScaleApiRoutes.ProjectsList("organization-a", 0, 100),
        TerraScaleApiRoutes.ProjectsCreate("organization-a"),
        TerraScaleApiRoutes.ProjectGet("project-a"),
        TerraScaleApiRoutes.ProjectRegionPlacementsList("project-a"),
        TerraScaleApiRoutes.ProjectUpdate("project-a"),
        TerraScaleApiRoutes.ProjectDelete("project-a"),
        TerraScaleApiRoutes.RegionsList(),
        TerraScaleApiRoutes.RegionGet("GRU"),
        TerraScaleApiRoutes.ProjectSavedQueriesList("project-a", 0, 100),
        TerraScaleApiRoutes.ProjectSavedQueriesCreate("project-a"),
        TerraScaleApiRoutes.ProjectSavedQueryGet("project-a", "saved-query-a"),
        TerraScaleApiRoutes.ProjectSavedQueryUpdate("project-a", "saved-query-a"),
        TerraScaleApiRoutes.ProjectSavedQueryDelete("project-a", "saved-query-a"),
        TerraScaleApiRoutes.ProjectDatabasesList("project-a", 0, 100),
        TerraScaleApiRoutes.ProjectDatabasesCreate("project-a"),
        TerraScaleApiRoutes.ProjectDatabaseGet("project-a", "database-a"),
        TerraScaleApiRoutes.ProjectDatabaseDelete("project-a", "database-a"),
        TerraScaleApiRoutes.DatabaseBranchesCapabilityGet("project-a"),
        TerraScaleApiRoutes.DatabaseBranchesCreate("project-a"),
        TerraScaleApiRoutes.ProjectQueuesList("project-a"),
        TerraScaleApiRoutes.ProjectQueuesCreate("project-a"),
        TerraScaleApiRoutes.ProjectQueueGet("project-a", "queue-a"),
        TerraScaleApiRoutes.ProjectQueueDelete("project-a", "queue-a"),
        TerraScaleApiRoutes.ProjectQueuePurge("project-a", "queue-a"),
        TerraScaleApiRoutes.ProjectQueuePublish("project-a", "queue-a"),
        TerraScaleApiRoutes.ProjectQueuePeek("project-a", "queue-a", 10),
        TerraScaleApiRoutes.ProjectQueueReceive("project-a", "queue-a"),
        TerraScaleApiRoutes.ProjectQueueComplete("project-a", "queue-a"),
        TerraScaleApiRoutes.ProjectQueueAbandon("project-a", "queue-a"),
        TerraScaleApiRoutes.ProjectQueueDeadLetter("project-a", "queue-a"),
        TerraScaleApiRoutes.ProjectQueueRenewLock("project-a", "queue-a"),
        TerraScaleApiRoutes.EnvironmentsList("project-a", "database-a", 0, 100),
        TerraScaleApiRoutes.EnvironmentsCreate("project-a", "database-a"),
        TerraScaleApiRoutes.EnvironmentGet("project-a", "database-a", "environment-a"),
        TerraScaleApiRoutes.EnvironmentDelete("project-a", "database-a", "environment-a"),
        TerraScaleApiRoutes.RepositoriesList(),
        TerraScaleApiRoutes.RepositoriesCreate(),
        TerraScaleApiRoutes.RepositoryGet("repository-a"),
        TerraScaleApiRoutes.RepositoryDelete("repository-a", deleteDocuments: false),
        TerraScaleApiRoutes.BackupPolicyConfigure("project-a", "database-a"),
        TerraScaleApiRoutes.BackupsPlan("project-a", "database-a"),
        TerraScaleApiRoutes.BackupsExecute("project-a", "database-a"),
        TerraScaleApiRoutes.BackupsList("project-a", "database-a", 0, 100),
        TerraScaleApiRoutes.BackupGet("project-a", "database-a", "backup-a"),
        TerraScaleApiRoutes.RestorePointsList("project-a", "database-a"),
        TerraScaleApiRoutes.RestoresCreate("project-a", "database-a"),
        TerraScaleApiRoutes.RestoreGet("project-a", "database-a", "restore-a"),
        TerraScaleApiRoutes.RestoresList("project-a", "database-a", 0, 100),
        TerraScaleApiRoutes.DocumentsList("repository-a", 0, 100),
        TerraScaleApiRoutes.DocumentsCreate("repository-a"),
        TerraScaleApiRoutes.DocumentGet("repository-a", "document-a"),
        TerraScaleApiRoutes.DocumentPut("repository-a", "document-a"),
        TerraScaleApiRoutes.DocumentPatch("repository-a", "document-a"),
        TerraScaleApiRoutes.DocumentsBatch("repository-a"),
        TerraScaleApiRoutes.DocumentDelete("repository-a", "document-a"),
        TerraScaleApiRoutes.QueryExecute("repository-a"),
        TerraScaleApiRoutes.SchemaGet("repository-a"),
        TerraScaleApiRoutes.SchemaValidate("repository-a"),
        TerraScaleApiRoutes.SchemaConfigure("repository-a"),
        TerraScaleApiRoutes.ProjectTeamMembersList("project-a", 0, 100),
        TerraScaleApiRoutes.ProjectTeamMembersCreate("project-a"),
        TerraScaleApiRoutes.ProjectTeamMemberUpdate("project-a", "user-a"),
        TerraScaleApiRoutes.ProjectTeamMemberDelete("project-a", "user-a"),
        TerraScaleApiRoutes.ProjectUsageGet("project-a"),
        TerraScaleApiRoutes.OrganizationUsageGet("organization-a"),
        TerraScaleApiRoutes.ProjectUsageWorkloadGet("project-a", "24h"),
        TerraScaleApiRoutes.ProjectLatencyGet("project-a", -23.5505, -46.6333),
        TerraScaleApiRoutes.ProjectUsageHistoryGet("project-a", "24h"),
        TerraScaleApiRoutes.ProjectAuditList("project-a", 0, 100),
        TerraScaleApiRoutes.ProjectAuditGet("project-a", "audit-a"),
        TerraScaleApiRoutes.NotificationsList(0, 100, unreadOnly: false),
        TerraScaleApiRoutes.NotificationMarkRead("notification-a"),
        TerraScaleApiRoutes.NotificationsMarkAllRead()
    ];

    private static IReadOnlyList<CallableOperation> CallableOperations() =>
    [
        new("onboarding.getState", static client => client.Onboarding.GetStateAsync()),
        new("onboarding.organization.create", static client => client.Onboarding.CreateOrganizationAsync(
            new TerraScaleCreateOnboardingOrganizationRequest("Analytical Engines"))),
        new("onboarding.project.create", static client => client.Onboarding.CreateProjectAsync(
            new TerraScaleCreateOnboardingProjectRequest("Primary", [TerraScaleRegions.Gru], TerraScaleRegions.Gru))),
        new("organizations.list", static client => client.Organizations.ListAsync()),
        new("organizations.create", static client => client.Organizations.CreateAsync(
            new TerraScaleCreateOrganizationRequest("Analytical Engines"))),
        new("organizations.get", static client => client.Organizations.GetAsync("organization-a")),
        new("organizations.update", static client => client.Organizations.UpdateAsync(
            "organization-a",
            new TerraScaleUpdateOrganizationRequest("Analytical Engines"))),
        new("organizations.deletion.request", static client => client.Organizations.RequestDeletionAsync(
            "organization-a",
            new TerraScaleRequestOrganizationDeletionRequest(true, "Analytical Engines"))),
        new("organizations.leave", static client => client.Organizations.LeaveAsync("organization-a")),
        new("organizations.projects.list", static client => client.Projects.ListAsync("organization-a")),
        new("organizations.members.list", static client => client.Organizations.ListMembersAsync("organization-a")),
        new("organizations.members.updateRole", static client => client.Organizations.UpdateMemberRoleAsync(
            "organization-a",
            "user-a",
            new TerraScaleUpdateOrganizationMemberRoleRequest(TerraScaleOrganizationMemberRoles.Owner))),
        new("organizations.members.remove", static client => client.Organizations.RemoveMemberAsync(
            "organization-a",
            "user-a")),
        new("organizations.projects.create", static client => client.Projects.CreateAsync(
            "organization-a",
            new TerraScaleCreateProjectRequest(
                "Primary",
                Regions: [TerraScaleRegions.Gru],
                BackupRegion: TerraScaleRegions.Gru))),
        new("organizations.projects.get", static client => client.Projects.GetAsync("project-a")),
        new("organizations.projects.regions.list", static client => client.Projects.ListRegionPlacementsAsync("project-a")),
        new("organizations.projects.update", static client => client.Projects.UpdateAsync(
            "project-a",
            new TerraScaleUpdateProjectRequest("Primary", [TerraScaleRegions.Gru], 1, TerraScaleRegions.Gru))),
        new("organizations.projects.delete", static client => client.Projects.DeleteAsync("project-a", "Primary")),
        new("organizations.invitations.list", static client => client.Organizations.ListInvitationsAsync("organization-a")),
        new("organizations.invitations.create", static client => client.Organizations.CreateInvitationAsync(
            "organization-a",
            new TerraScaleCreateOrganizationInvitationRequest(
                "grace@example.test",
                "https://app.example.test/invitations/{invitationId}",
                "project-a"))),
        new("organizations.invitations.accept", static client => client.Organizations.AcceptInvitationAsync(
            "organization-a",
            "invitation-a")),
        new("organizations.invitations.decline", static client => client.Organizations.DeclineInvitationAsync(
            "organization-a",
            "invitation-a")),
        new("organizations.invitations.resend", static client => client.Organizations.ResendInvitationAsync(
            "organization-a",
            "invitation-a")),
        new("organizations.invitations.cancel", static client => client.Organizations.CancelInvitationAsync(
            "organization-a",
            "invitation-a")),
        new("regions.list", static client => client.Regions.ListAsync()),
        new("regions.get", static client => client.Regions.GetAsync(TerraScaleRegions.Gru)),
        new("savedQueries.list", static client => client.SavedQueries.ListAsync("project-a")),
        new("savedQueries.create", static client => client.SavedQueries.CreateAsync(
            "project-a",
            new TerraScaleSaveQueryRequest("Orders", "from Orders"))),
        new("savedQueries.get", static client => client.SavedQueries.GetAsync("project-a", "saved-query-a")),
        new("savedQueries.update", static client => client.SavedQueries.UpdateAsync(
            "project-a",
            "saved-query-a",
            new TerraScaleSaveQueryRequest("Orders", "from Orders"))),
        new("savedQueries.delete", static client => client.SavedQueries.DeleteAsync("project-a", "saved-query-a")),
        new("databases.list", static client => client.ProjectDatabases.ListAsync("project-a")),
        new("databases.create", static client => client.ProjectDatabases.CreateAsync("project-a", "Primary")),
        new("databases.get", static client => client.ProjectDatabases.GetAsync("project-a", "database-a")),
        new("databases.delete", static client => client.ProjectDatabases.DeleteAsync(
            "project-a",
            "database-a",
            new TerraScaleDeleteDatabaseOptions("database-a"))),
        new("databaseBranches.getCapability", static client => client.DatabaseBranches.GetCapabilityAsync("project-a")),
        new("databaseBranches.requestClone", static client => client.DatabaseBranches.RequestCloneAsync(
            "project-a",
            new TerraScaleCreateDatabaseBranchRequest("database-a"))),
        new("queues.list", static client => client.Queues.ListAsync("project-a")),
        new("queues.create", static client => client.Queues.CreateAsync("project-a", "Jobs", TerraScaleQueueKinds.Queue)),
        new("queues.get", static client => client.Queues.GetAsync("project-a", "queue-a")),
        new("queues.delete", static client => client.Queues.DeleteAsync("project-a", "queue-a")),
        new("queues.purge", static client => client.Queues.PurgeAsync("project-a", "queue-a")),
        new("queues.publish", static client => client.Queues.PublishAsync("project-a", "queue-a", new { value = 1 })),
        new("queues.peek", static client => client.Queues.PeekAsync("project-a", "queue-a")),
        new("queues.receive", static client => client.Queues.ReceiveAsync(
            "project-a",
            "queue-a",
            new TerraScaleReceiveProjectQueueMessagesRequest("worker-a"))),
        new("queues.complete", static client => client.Queues.CompleteAsync(
            "project-a",
            "queue-a",
            new TerraScaleProjectQueueReceipt("receipt-a", TerraScaleRegions.Gru))),
        new("queues.abandon", static client => client.Queues.AbandonAsync(
            "project-a",
            "queue-a",
            new TerraScaleProjectQueueReceipt("receipt-a", TerraScaleRegions.Gru))),
        new("queues.deadLetter", static client => client.Queues.DeadLetterAsync(
            "project-a",
            "queue-a",
            new TerraScaleProjectQueueReceipt("receipt-a", TerraScaleRegions.Gru))),
        new("queues.renewLock", static client => client.Queues.RenewLockAsync(
            "project-a",
            "queue-a",
            new TerraScaleProjectQueueReceipt("receipt-a", TerraScaleRegions.Gru))),
        new("databases.environments.list", static client => client.Environments.ListAsync("project-a", "database-a")),
        new("databases.environments.create", static client => client.Environments.CreateAsync(
            "project-a",
            "database-a",
            "Preview")),
        new("databases.environments.get", static client => client.Environments.GetAsync(
            "project-a",
            "database-a",
            "environment-a")),
        new("databases.environments.delete", static client => client.Environments.DeleteAsync(
            "project-a",
            "database-a",
            "environment-a")),
        new("repositories.list", static client => client.Repositories.ListAsync()),
        new("repositories.create", static client => client.Repositories.CreateAsync("Orders")),
        new("repositories.get", static client => client.Repositories.GetAsync("repository-a")),
        new("repositories.delete", static client => client.Repositories.DeleteAsync("repository-a")),
        new("backups.policy.configure", static client => client.Backups.ConfigurePolicyAsync(
            "project-a",
            "database-a",
            new TerraScaleConfigureBackupPolicyRequest("0 0 * * *"))),
        new("backups.plan", static client => client.Backups.PlanAsync(
            "project-a",
            "database-a",
            new TerraScaleBackupConfirmationPlanRequest("operation-a"))),
        new("backups.execute", static client => client.Backups.ExecuteAsync(
            "project-a",
            "database-a",
            new TerraScaleBackupConfirmationExecuteRequest("operation-a", "confirmation-a"),
            "idempotency-a")),
        new("backups.list", static client => client.Backups.ListAsync("project-a", "database-a")),
        new("backups.get", static client => client.Backups.GetAsync("project-a", "database-a", "backup-a")),
        new("backups.restorePoints.list", static client => client.Backups.ListRestorePointsAsync(
            "project-a",
            "database-a")),
        new("backups.restores.start", static client => client.Backups.StartRestoreAsync(
            "project-a",
            "database-a",
            "backup-a")),
        new("backups.restores.get", static client => client.Backups.GetRestoreAsync(
            "project-a",
            "database-a",
            "restore-a")),
        new("backups.restores.list", static client => client.Backups.ListRestoresAsync("project-a", "database-a")),
        new("documents.list", static client => client.Documents.ListAsync("repository-a")),
        new("documents.create", static client => client.Documents.CreateAsync("repository-a", new { value = 1 })),
        new("documents.get", static client => client.Documents.GetAsync("repository-a", "document-a")),
        new("documents.put", static client => client.Documents.UpsertAsync("repository-a", "document-a", new { value = 2 })),
        new("documents.patch", static client => client.Documents.PatchAsync("repository-a", "document-a", new { value = 3 })),
        new("documents.batch", static client => client.Documents.BatchAsync(
            "repository-a",
            [TerraScaleDocumentBatchOperation.Create(new { value = 1 })])),
        new("documents.delete", static client => client.Documents.DeleteAsync("repository-a", "document-a")),
        new("query.execute", static client => client.Tql.QueryAsync("repository-a", "from Orders")),
        new("schema.get", static client => client.Schemas.GetAsync("repository-a")),
        new("schema.validate", static client => client.Schemas.ValidateAsync("repository-a")),
        new("schema.configure", static client => client.Schemas.ConfigureAsync("repository-a", enabled: true, schema: null)),
        new("access.teamMembers.list", static client => client.Access.ListTeamMembersAsync("project-a")),
        new("access.teamMembers.add", static client => client.Access.AddTeamMemberAsync(
            "project-a",
            new TerraScaleAddProjectTeamMemberRequest("user-a", "member"))),
        new("access.teamMembers.update", static client => client.Access.UpdateTeamMemberAsync(
            "project-a",
            "user-a",
            new TerraScaleUpdateProjectTeamMemberRequest("owner"))),
        new("access.teamMembers.remove", static client => client.Access.RemoveTeamMemberAsync("project-a", "user-a")),
        new("usage.project.get", static client => client.ProjectUsage.GetAsync("project-a")),
        new("usage.organization.get", static client => client.ProjectUsage.GetOrganizationAsync("organization-a")),
        new("usage.project.workload", static client => client.ProjectUsage.GetWorkloadAsync("project-a", "24h")),
        new("usage.project.latency", static client => client.ProjectUsage.GetLatencyAsync("project-a", -23.55, -46.63)),
        new("usage.project.history", static client => client.ProjectUsage.GetHistoryAsync("project-a", "24h")),
        new("audit.list", static client => client.Audit.ListAsync("project-a")),
        new("audit.get", static client => client.Audit.GetAsync("project-a", "audit-a")),
        new("notifications.list", static client => client.Notifications.ListAsync()),
        new("notifications.markRead", static client => client.Notifications.MarkReadAsync("notification-a")),
        new("notifications.markAllRead", static client => client.Notifications.MarkAllReadAsync())
    ];

    private static string BindSamplePath(string pathTemplate) =>
        pathTemplate
            .Replace("{organizationId}", "organization-a", StringComparison.Ordinal)
            .Replace("{projectId}", "project-a", StringComparison.Ordinal)
            .Replace("{userId}", "user-a", StringComparison.Ordinal)
            .Replace("{invitationId}", "invitation-a", StringComparison.Ordinal)
            .Replace("{regionCode}", TerraScaleRegions.Gru, StringComparison.Ordinal)
            .Replace("{savedQueryId}", "saved-query-a", StringComparison.Ordinal)
            .Replace("{databaseId}", "database-a", StringComparison.Ordinal)
            .Replace("{queueId}", "queue-a", StringComparison.Ordinal)
            .Replace("{environmentId}", "environment-a", StringComparison.Ordinal)
            .Replace("{repositoryId}", "repository-a", StringComparison.Ordinal)
            .Replace("{backupId}", "backup-a", StringComparison.Ordinal)
            .Replace("{restoreId}", "restore-a", StringComparison.Ordinal)
            .Replace("{documentId}", "document-a", StringComparison.Ordinal)
            .Replace("{auditEventId}", "audit-a", StringComparison.Ordinal)
            .Replace("{notificationId}", "notification-a", StringComparison.Ordinal);

    private static async Task<VerifierResult> RunVerifierAsync(params string[] arguments)
    {
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(repositoryRoot, "scripts", "verify-public-contract-coverage.sh"),
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The public contract verifier process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TerraScale.DotNet.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The TerraScale.DotNet repository root was not found.");
    }

    private sealed record VerifierResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record CallableOperation(
        string RouteName,
        Func<TerraScaleClient, Task> Invoke);

    private sealed record CapturedRoute(string Method, string Path);

    private sealed class RouteCaptureHandler : HttpMessageHandler
    {
        public List<CapturedRoute> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri
                ?? throw new InvalidOperationException("A callable public operation sent no request URI.");
            Requests.Add(new CapturedRoute(request.Method.Method, requestUri.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ExpectationFailed)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":\"route_capture\",\"message\":\"Captured\",\"category\":\"test\"}}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
