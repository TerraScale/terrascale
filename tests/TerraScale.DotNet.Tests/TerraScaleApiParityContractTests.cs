using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleApiParityContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string OrganizationId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string ProjectId = "01BX5ZZKBKACTAV9WEVGEMMVS0";
    private const string DatabaseId = "01BX5ZZKBKACTAV9WEVGEMMVS1";
    private const string EnvironmentId = "01BX5ZZKBKACTAV9WEVGEMMVS2";
    private const string RepositoryId = "01BX5ZZKBKACTAV9WEVGEMMVS3";
    private const string DocumentId = "01BX5ZZKBKACTAV9WEVGEMMVS4";
    private const string ApiKeyId = "01BX5ZZKBKACTAV9WEVGEMMVS5";
    private const string BackupId = "01BX5ZZKBKACTAV9WEVGEMMVS6";
    private const string RestoreId = "01BX5ZZKBKACTAV9WEVGEMMVS7";
    private const string ClientId = "m2m_client_01BX5ZZKBKACTAV9WEVGEMMVS8";
    private const string ClientSecret = "m2m_secret_01BX5ZZKBKACTAV9WEVGEMMVS9";

    [Test]
    public async Task ApiParityRoutes_MatchVerifiedEndpointPrefixesAndPaths()
    {
        await Assert.That(TerraScaleApiRoutes.ProjectsList(OrganizationId, 5, 10))
            .IsEqualTo($"/api/v1/organizations/{OrganizationId}/projects?skip=5&take=10");
        await Assert.That(TerraScaleApiRoutes.ProjectsCreate(OrganizationId))
            .IsEqualTo($"/api/v1/organizations/{OrganizationId}/projects");
        await Assert.That(TerraScaleApiRoutes.ProjectGet(ProjectId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}");
        await Assert.That(TerraScaleApiRoutes.ProjectUpdate(ProjectId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}");
        await Assert.That(TerraScaleApiRoutes.ProjectDatabasesCreate(ProjectId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases");
        await Assert.That(TerraScaleApiRoutes.ProjectDatabasesList(ProjectId, 0, 20))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases?skip=0&take=20");
        await Assert.That(TerraScaleApiRoutes.ProjectDatabaseGet(ProjectId, DatabaseId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}");
        await Assert.That(TerraScaleApiRoutes.EnvironmentsCreate(ProjectId, DatabaseId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/environments");
        await Assert.That(TerraScaleApiRoutes.EnvironmentsList(ProjectId, DatabaseId, 2, 30))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/environments?skip=2&take=30");
        await Assert.That(TerraScaleApiRoutes.EnvironmentGet(ProjectId, DatabaseId, EnvironmentId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/environments/{EnvironmentId}");
        await Assert.That(TerraScaleApiRoutes.EnvironmentDelete(ProjectId, DatabaseId, EnvironmentId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/environments/{EnvironmentId}");
        await Assert.That(TerraScaleApiRoutes.ProjectUsageGet(ProjectId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/usage");
        await Assert.That(TerraScaleApiRoutes.ProjectUsageWorkloadGet(ProjectId, "7d"))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/usage/workload?range=7d");

        await Assert.That(TerraScaleApiRoutes.ApiKeysList(ProjectId, "cursor 1", 25))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/auth/api-keys?cursor=cursor%201&limit=25");
        await Assert.That(TerraScaleApiRoutes.ApiKeysCreate(ProjectId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/auth/api-keys");
        await Assert.That(TerraScaleApiRoutes.ApiKeyRotate(ProjectId, ApiKeyId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/auth/api-keys/{ApiKeyId}/rotate");
        await Assert.That(TerraScaleApiRoutes.ApiKeyRevoke(ProjectId, ApiKeyId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/auth/api-keys/{ApiKeyId}");

        await Assert.That(TerraScaleApiRoutes.M2MApplicationsList(ProjectId, "cursor 2", 50))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/auth/m2m-applications?cursor=cursor%202&limit=50");
        await Assert.That(TerraScaleApiRoutes.M2MApplicationsCreate(ProjectId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/auth/m2m-applications");
        await Assert.That(TerraScaleApiRoutes.M2MApplicationRotate(ProjectId, ApiKeyId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/auth/m2m-applications/{ApiKeyId}/rotate");
        await Assert.That(TerraScaleApiRoutes.M2MApplicationDelete(ProjectId, ApiKeyId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/auth/m2m-applications/{ApiKeyId}");
        await Assert.That(TerraScaleApiRoutes.M2MTokenCreate(ProjectId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/auth/m2m/token");

        await Assert.That(TerraScaleApiRoutes.NotificationsList(1, 2, unreadOnly: true))
            .IsEqualTo("/api/v1/dashboard/notifications?skip=1&take=2&unreadOnly=true");
        await Assert.That(TerraScaleApiRoutes.NotificationMarkRead(ApiKeyId))
            .IsEqualTo($"/api/v1/dashboard/notifications/{ApiKeyId}:mark-read");
        await Assert.That(TerraScaleApiRoutes.NotificationsMarkAllRead())
            .IsEqualTo("/api/v1/dashboard/notifications:mark-all-read");

        await Assert.That(TerraScaleApiRoutes.BackupPolicyConfigure(ProjectId, DatabaseId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/backups/policy");
        await Assert.That(TerraScaleApiRoutes.BackupsList(ProjectId, DatabaseId, 0, 100))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/backups?skip=0&take=100");
        await Assert.That(TerraScaleApiRoutes.BackupGet(ProjectId, DatabaseId, BackupId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/backups/{BackupId}");
        await Assert.That(TerraScaleApiRoutes.RestorePointsList(ProjectId, DatabaseId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/restore-points");
        await Assert.That(TerraScaleApiRoutes.RestoresList(ProjectId, DatabaseId, 3, 4))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/restores?skip=3&take=4");
        await Assert.That(TerraScaleApiRoutes.RestoreGet(ProjectId, DatabaseId, RestoreId))
            .IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/restores/{RestoreId}");
    }

    [Test]
    public async Task ProjectClientSendPath_MatchesCreateAndUpdateEndpointContracts()
    {
        var create = await CaptureAuthenticatedRequestAsync(
            client => client.Projects.CreateAsync(
                OrganizationId,
                new TerraScaleCreateProjectRequest("Orders", ["auth:login"])),
            ProjectJson("Orders"));
        await Assert.That(create.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(create.PathAndQuery).IsEqualTo($"/api/v1/organizations/{OrganizationId}/projects");
        await Assert.That(create.ContentMediaType).IsEqualTo("application/json");
        using (var createJson = JsonDocument.Parse(create.Body!))
        {
            await Assert.That(createJson.RootElement.GetProperty("name").GetString()).IsEqualTo("Orders");
            await Assert.That(createJson.RootElement.GetProperty("defaultPermissionKeys")[0].GetString()).IsEqualTo("auth:login");
        }

        var update = await CaptureAuthenticatedRequestAsync(
            client => client.Projects.UpdateAsync(ProjectId, new TerraScaleUpdateProjectRequest("Orders Production")),
            ProjectJson("Orders Production"));
        await Assert.That(update.Method).IsEqualTo(HttpMethod.Patch);
        await Assert.That(update.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}");
        await Assert.That(update.ContentMediaType).IsEqualTo("application/json");
        using var updateJson = JsonDocument.Parse(update.Body!);
        await Assert.That(updateJson.RootElement.GetProperty("name").GetString()).IsEqualTo("Orders Production");
    }

    [Test]
    public async Task EnvironmentClientSendPath_MatchesListGetCreateAndDeleteEndpointContracts()
    {
        var list = await CaptureAuthenticatedRequestAsync(
            client => client.Environments.ListAsync(ProjectId, DatabaseId, skip: 3, take: 25),
            $$"""[{{EnvironmentJson("Preview")}}]""");
        await Assert.That(list.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(list.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/environments?skip=3&take=25");
        await AssertNoBodyAsync(list);

        var get = await CaptureAuthenticatedRequestAsync(
            client => client.Environments.GetAsync(ProjectId, DatabaseId, EnvironmentId),
            EnvironmentJson("Preview"));
        await Assert.That(get.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(get.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/environments/{EnvironmentId}");
        await AssertNoBodyAsync(get);

        var create = await CaptureAuthenticatedRequestAsync(
            client => client.Environments.CreateAsync(ProjectId, DatabaseId, "Preview", "Preview"),
            EnvironmentJson("Preview"));
        await Assert.That(create.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(create.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/environments");
        using (var createJson = JsonDocument.Parse(create.Body!))
        {
            await Assert.That(createJson.RootElement.GetProperty("name").GetString()).IsEqualTo("Preview");
            await Assert.That(createJson.RootElement.GetProperty("kind").GetString()).IsEqualTo("Preview");
        }

        var delete = await CaptureAuthenticatedRequestAsync(
            client => client.Environments.DeleteAsync(ProjectId, DatabaseId, EnvironmentId),
            $$"""
            {
              "environmentId": "{{EnvironmentId}}",
              "projectId": "{{ProjectId}}",
              "databaseId": "{{DatabaseId}}",
              "name": "Preview",
              "deleted": true,
              "physicalDatabaseDeleted": true,
              "repositoriesDeleted": 2
            }
            """);
        await Assert.That(delete.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(delete.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/environments/{EnvironmentId}");
        await AssertNoBodyAsync(delete);
    }

    [Test]
    public async Task UsageWorkloadClientSendPath_MatchesWorkloadEndpointContract()
    {
        var captured = await CaptureAuthenticatedRequestAsync(
            client => client.ProjectUsage.GetWorkloadAsync(ProjectId, range: "24h"),
            $$"""
            {
              "projectId": "{{ProjectId}}",
              "range": "24h",
              "periodStart": "2026-07-05T00:00:00Z",
              "periodEnd": "2026-07-06T00:00:00Z",
              "bucketSize": "1h",
              "points": [
                {
                  "start": "2026-07-05T00:00:00Z",
                  "end": "2026-07-05T01:00:00Z",
                  "label": "00:00",
                  "value": 42
                }
              ]
            }
            """);

        await Assert.That(captured.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(captured.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/usage/workload?range=24h");
        await AssertNoBodyAsync(captured);
    }

    [Test]
    public async Task M2MTokenClientSendPath_UsesAnonymousSnakeCaseJsonAndNoSdkAuthHeaders()
    {
        var captured = await CaptureAnonymousRequestAsync(
            client => client.M2MApplications.CreateTokenAsync(
                ProjectId,
                new TerraScaleCreateM2MTokenRequest("client_credentials", ClientId, ClientSecret, "documents:read")),
            M2MTokenJson());

        await Assert.That(captured.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(captured.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/auth/m2m/token");
        await Assert.That(captured.ContentMediaType).IsEqualTo("application/json");
        await AssertNoSdkAuthHeadersAsync(captured);
        using var json = JsonDocument.Parse(captured.Body!);
        await Assert.That(json.RootElement.GetProperty("grant_type").GetString()).IsEqualTo("client_credentials");
        await Assert.That(json.RootElement.GetProperty("client_id").GetString()).IsEqualTo(ClientId);
        await Assert.That(json.RootElement.GetProperty("client_secret").GetString()).IsEqualTo(ClientSecret);
        await Assert.That(json.RootElement.GetProperty("scope").GetString()).IsEqualTo("documents:read");
    }

    [Test]
    public async Task M2MTokenClientSendPath_UsesAnonymousOAuthFormAndBasicCredentialShapes()
    {
        var form = await CaptureAnonymousRequestAsync(
            client => client.M2MApplications.CreateTokenFormAsync(ProjectId, ClientId, ClientSecret, "documents:read documents:write"),
            M2MTokenJson());
        await Assert.That(form.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(form.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/auth/m2m/token");
        await Assert.That(form.ContentMediaType).IsEqualTo("application/x-www-form-urlencoded");
        await Assert.That(form.Body).IsEqualTo("grant_type=client_credentials&client_id=m2m_client_01BX5ZZKBKACTAV9WEVGEMMVS8&client_secret=m2m_secret_01BX5ZZKBKACTAV9WEVGEMMVS9&scope=documents%3Aread+documents%3Awrite");
        await AssertNoSdkAuthHeadersAsync(form);

        var basic = await CaptureAnonymousRequestAsync(
            client => client.M2MApplications.CreateTokenWithBasicCredentialsAsync(ProjectId, ClientId, ClientSecret, "documents:read"),
            M2MTokenJson());
        await Assert.That(basic.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(basic.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/auth/m2m/token");
        await Assert.That(basic.ContentMediaType).IsEqualTo("application/x-www-form-urlencoded");
        await Assert.That(basic.Body).IsEqualTo("grant_type=client_credentials&scope=documents%3Aread");
        await Assert.That(basic.Headers["Authorization"].Single()).IsEqualTo($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(ClientId)}:{Uri.EscapeDataString(ClientSecret)}"))}");
        await Assert.That(basic.Headers.ContainsKey("X-TerraScale-Auth-Key")).IsFalse();
        await Assert.That(basic.Headers.ContainsKey("X-TerraScale-Project-Id")).IsFalse();
    }

    [Test]
    public async Task BackupAndRestoreClientSendPath_MatchesRequestBodyContracts()
    {
        var policy = await CaptureAuthenticatedRequestAsync(
            client => client.Backups.ConfigurePolicyAsync(
                ProjectId,
                DatabaseId,
                new TerraScaleConfigureBackupPolicyRequest("0 3 * * *", "*/15 * * * *", 14, Disabled: false)),
            $$"""
            {
              "backupPolicyId": "01BX5ZZKBKACTAV9WEVGEMMVS9",
              "projectId": "{{ProjectId}}",
              "databaseId": "{{DatabaseId}}",
              "databaseName": "orders",
              "fullBackupCron": "0 3 * * *",
              "incrementalBackupCron": "*/15 * * * *",
              "retentionDays": 14,
              "disabled": false,
              "destinationType": "oci",
              "status": "enabled",
              "createdAt": "2026-07-05T00:00:00Z",
              "updatedAt": "2026-07-05T00:00:00Z"
            }
            """);
        await Assert.That(policy.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(policy.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/backups/policy");
        using (var policyJson = JsonDocument.Parse(policy.Body!))
        {
            await Assert.That(policyJson.RootElement.GetProperty("fullBackupCron").GetString()).IsEqualTo("0 3 * * *");
            await Assert.That(policyJson.RootElement.GetProperty("incrementalBackupCron").GetString()).IsEqualTo("*/15 * * * *");
            await Assert.That(policyJson.RootElement.GetProperty("retentionDays").GetInt32()).IsEqualTo(14);
            await Assert.That(policyJson.RootElement.GetProperty("disabled").GetBoolean()).IsFalse();
        }

        var restore = await CaptureAuthenticatedRequestAsync(
            client => client.Backups.StartRestoreAsync(ProjectId, DatabaseId, BackupId, "restore-once"),
            RestoreJson());
        await Assert.That(restore.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(restore.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/databases/{DatabaseId}/restores");
        using var restoreJson = JsonDocument.Parse(restore.Body!);
        await Assert.That(restoreJson.RootElement.GetProperty("backupId").GetString()).IsEqualTo(BackupId);
        await Assert.That(restoreJson.RootElement.GetProperty("idempotencyKey").GetString()).IsEqualTo("restore-once");
    }

    [Test]
    public async Task CredentialManagementClientSendPath_MatchesApiKeyAndM2MCreateContracts()
    {
        var apiKey = await CaptureAuthenticatedRequestAsync(
            client => client.ApiKeys.CreateAsync(
                ProjectId,
                new TerraScaleCreateApiKeyRequest
                {
                    Name = "Orders worker",
                    Scopes = ["documents:read", "documents:write"],
                    DatabaseId = DatabaseId,
                    EnvironmentId = EnvironmentId
                }),
            CreatedApiKeyJson());
        await Assert.That(apiKey.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(apiKey.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/auth/api-keys");
        using (var apiKeyJson = JsonDocument.Parse(apiKey.Body!))
        {
            await Assert.That(apiKeyJson.RootElement.GetProperty("name").GetString()).IsEqualTo("Orders worker");
            await Assert.That(apiKeyJson.RootElement.GetProperty("scopes")[1].GetString()).IsEqualTo("documents:write");
            await Assert.That(apiKeyJson.RootElement.GetProperty("databaseId").GetString()).IsEqualTo(DatabaseId);
            await Assert.That(apiKeyJson.RootElement.GetProperty("environmentId").GetString()).IsEqualTo(EnvironmentId);
        }

        var m2m = await CaptureAuthenticatedRequestAsync(
            client => client.M2MApplications.CreateAsync(
                ProjectId,
                new TerraScaleCreateM2MApplicationRequest
                {
                    Name = "Orders sync",
                    Scopes = ["documents:read"],
                    DatabaseId = DatabaseId,
                    EnvironmentId = EnvironmentId
                }),
            CreatedM2MApplicationJson());
        await Assert.That(m2m.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(m2m.PathAndQuery).IsEqualTo($"/api/v1/projects/{ProjectId}/auth/m2m-applications");
        using var m2mJson = JsonDocument.Parse(m2m.Body!);
        await Assert.That(m2mJson.RootElement.GetProperty("name").GetString()).IsEqualTo("Orders sync");
        await Assert.That(m2mJson.RootElement.GetProperty("scopes")[0].GetString()).IsEqualTo("documents:read");
        await Assert.That(m2mJson.RootElement.GetProperty("databaseId").GetString()).IsEqualTo(DatabaseId);
        await Assert.That(m2mJson.RootElement.GetProperty("environmentId").GetString()).IsEqualTo(EnvironmentId);
    }

    [Test]
    public async Task DocumentDtos_IncludeRepositoryIdOnAllVerifiedDocumentResponses()
    {
        var document = JsonSerializer.Deserialize<TerraScaleDocument>(
            $$"""
            {
              "repositoryId": "{{RepositoryId}}",
              "repository": "orders",
              "id": "{{DocumentId}}",
              "data": { "status": "created" },
              "lastModified": "2026-07-05T00:00:00Z",
              "changeToken": "01BX5ZZKBKACTAV9WEVGEMMVS8"
            }
            """,
            JsonOptions);

        await Assert.That(document!.RepositoryId).IsEqualTo(RepositoryId);
        await Assert.That(document.Repository).IsEqualTo("orders");
        await Assert.That(document.Data.GetProperty("status").GetString()).IsEqualTo("created");

        var page = JsonSerializer.Deserialize<TerraScaleDocumentPage>(
            $$"""
            {
              "repositoryId": "{{RepositoryId}}",
              "repository": "orders",
              "skip": 0,
              "take": 50,
              "total": 1,
              "items": [
                {
                  "repositoryId": "{{RepositoryId}}",
                  "repository": "orders",
                  "id": "{{DocumentId}}",
                  "data": { "status": "listed" },
                  "lastModified": null,
                  "changeToken": null
                }
              ]
            }
            """,
            JsonOptions);

        await Assert.That(page!.RepositoryId).IsEqualTo(RepositoryId);
        await Assert.That(page.Items.Single().RepositoryId).IsEqualTo(RepositoryId);

        var batch = JsonSerializer.Deserialize<TerraScaleDocumentBatch>(
            $$"""
            {
              "repositoryId": "{{RepositoryId}}",
              "repository": "orders",
              "results": [
                {
                  "index": 0,
                  "operation": "create",
                  "id": "{{DocumentId}}",
                  "document": {
                    "repositoryId": "{{RepositoryId}}",
                    "repository": "orders",
                    "id": "{{DocumentId}}",
                    "data": { "status": "batched" },
                    "lastModified": null,
                    "changeToken": null
                  },
                  "deleted": null
                },
                {
                  "index": 1,
                  "operation": "delete",
                  "id": "{{DocumentId}}",
                  "document": null,
                  "deleted": {
                    "repositoryId": "{{RepositoryId}}",
                    "repository": "orders",
                    "id": "{{DocumentId}}",
                    "deleted": true
                  }
                }
              ]
            }
            """,
            JsonOptions);

        await Assert.That(batch!.RepositoryId).IsEqualTo(RepositoryId);
        await Assert.That(batch.Results[0].Document!.RepositoryId).IsEqualTo(RepositoryId);
        await Assert.That(batch.Results[1].Deleted!.RepositoryId).IsEqualTo(RepositoryId);
    }

    [Test]
    public async Task NewFamilyDtos_DeserializeVerifiedEndpointShapes()
    {
        var apiKeys = JsonSerializer.Deserialize<TerraScaleApiKeyPage>(
            $$"""
            {
              "apiKeys": [
                {
                  "keyId": "{{ApiKeyId}}",
                  "projectId": "{{ProjectId}}",
                  "name": "SDK",
                  "prefix": "tsauth_abc",
                  "lastFour": "1234",
                  "scopes": ["documents:read"],
                  "databaseId": "{{DatabaseId}}",
                  "environmentId": null,
                  "createdByUserId": null,
                  "createdByEmail": "owner@terrascale.tech",
                  "createdAt": "2026-07-05T00:00:00Z",
                  "expiresAt": null,
                  "lastUsedAt": null,
                  "revokedAt": null,
                  "isActive": true
                }
              ],
              "nextCursor": ""
            }
            """,
            JsonOptions);
        await Assert.That(apiKeys!.ApiKeys.Single().KeyId).IsEqualTo(ApiKeyId);

        var token = JsonSerializer.Deserialize<TerraScaleM2MToken>(
            $$"""
            {
              "access_token": "eyJhbGciOi",
              "token_type": "Bearer",
              "expires_in": 3600,
              "scope": "documents:read",
              "project_id": "{{ProjectId}}",
              "client_id": "{{ApiKeyId}}",
              "database_id": "{{DatabaseId}}",
              "environment_id": "{{EnvironmentId}}"
            }
            """,
            JsonOptions);
        await Assert.That(token!.AccessToken).IsEqualTo("eyJhbGciOi");
        await Assert.That(token.ClientId).IsEqualTo(ApiKeyId);
        await Assert.That(token.ExpiresIn).IsEqualTo(3600);

        var workload = JsonSerializer.Deserialize<TerraScaleProjectUsageWorkload>(
            $$"""
            {
              "projectId": "{{ProjectId}}",
              "range": "7d",
              "periodStart": "2026-07-05T00:00:00Z",
              "periodEnd": "2026-07-12T00:00:00Z",
              "bucketSize": "1d",
              "points": [
                {
                  "start": "2026-07-05T00:00:00Z",
                  "end": "2026-07-06T00:00:00Z",
                  "label": "Jul 5",
                  "value": 9
                }
              ]
            }
            """,
            JsonOptions);
        await Assert.That(workload!.ProjectId).IsEqualTo(ProjectId);
        await Assert.That(workload.Points.Single().Value).IsEqualTo(9);

        var notifications = JsonSerializer.Deserialize<IReadOnlyList<TerraScaleNotification>>(
            $$"""
            [
              {
                "notificationId": "{{ApiKeyId}}",
                "title": "Backup completed",
                "body": "The backup finished.",
                "severity": "info",
                "readAt": null,
                "createdAt": "2026-07-05T00:00:00Z"
              }
            ]
            """,
            JsonOptions);
        await Assert.That(notifications!.Single().NotificationId).IsEqualTo(ApiKeyId);

        var marked = JsonSerializer.Deserialize<TerraScaleMarkedNotificationsRead>(
            """{ "markedRead": 2 }""",
            JsonOptions);
        await Assert.That(marked!.MarkedRead).IsEqualTo(2);

        var restore = JsonSerializer.Deserialize<TerraScaleRestoreJob>(
            $$"""
            {
              "restoreId": "{{RestoreId}}",
              "projectId": "{{ProjectId}}",
              "databaseId": "{{DatabaseId}}",
              "backupId": "{{BackupId}}",
              "restoredDatabaseId": "01BX5ZZKBKACTAV9WEVGEMMVS8",
              "restoredDatabaseName": "orders-restored",
              "status": "completed",
              "requestedBy": "owner@terrascale.tech",
              "requestedAt": "2026-07-05T00:00:00Z",
              "updatedAt": "2026-07-05T00:01:00Z",
              "completedAt": "2026-07-05T00:01:00Z",
              "errorMessage": null
            }
            """,
            JsonOptions);
        await Assert.That(restore!.RestoredDatabaseId).IsEqualTo("01BX5ZZKBKACTAV9WEVGEMMVS8");
        await Assert.That(restore.RestoredDatabaseName).IsEqualTo("orders-restored");
    }

    private static async Task<CapturedRequest> CaptureAuthenticatedRequestAsync(
        Func<TerraScaleClient, Task> sendAsync,
        string responseJson) =>
        await CaptureRequestAsync(sendAsync, responseJson, authenticated: true);

    private static async Task<CapturedRequest> CaptureAnonymousRequestAsync(
        Func<TerraScaleClient, Task> sendAsync,
        string responseJson) =>
        await CaptureRequestAsync(sendAsync, responseJson, authenticated: false);

    private static async Task<CapturedRequest> CaptureRequestAsync(
        Func<TerraScaleClient, Task> sendAsync,
        string responseJson,
        bool authenticated)
    {
        var baseAddress = new Uri("https://api.test.terrascale.local");
        var handler = new CapturingHandler(responseJson);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = baseAddress
        };

        var options = new TerraScaleClientOptions
        {
            BaseAddress = baseAddress,
            RequestIdFactory = null
        };
        if (authenticated)
        {
            options.ApiKey = "tsauth_test_secret";
            options.ProjectId = ProjectId;
        }

        var client = new TerraScaleClient(httpClient, options);
        await sendAsync(client);
        return handler.Requests.Single();
    }

    private static async Task AssertNoBodyAsync(CapturedRequest request)
    {
        await Assert.That(request.Body).IsNull();
        await Assert.That(request.ContentMediaType).IsNull();
    }

    private static async Task AssertNoSdkAuthHeadersAsync(CapturedRequest request)
    {
        await Assert.That(request.Headers.ContainsKey("Authorization")).IsFalse();
        await Assert.That(request.Headers.ContainsKey("X-TerraScale-Auth-Key")).IsFalse();
        await Assert.That(request.Headers.ContainsKey("X-TerraScale-Project-Id")).IsFalse();
    }

    private static string ProjectJson(string name) =>
        $$"""
        {
          "projectId": "{{ProjectId}}",
          "organizationId": "{{OrganizationId}}",
          "name": "{{name}}",
          "ownerUserId": null,
          "createdByUserId": null,
          "ownershipAssignedAt": null,
          "defaultPermissionKeys": ["auth:login"],
          "pk": "ORG#{{OrganizationId}}",
          "sk": "PROJECT#{{ProjectId}}",
          "entityType": "project",
          "createdAt": "2026-07-05T00:00:00Z",
          "version": 1
        }
        """;

    private static string EnvironmentJson(string name) =>
        $$"""
        {
          "environmentId": "{{EnvironmentId}}",
          "projectId": "{{ProjectId}}",
          "databaseId": "{{DatabaseId}}",
          "name": "{{name}}",
          "kind": "Preview",
          "status": "Ready",
          "createdAt": "2026-07-05T00:00:00Z",
          "version": 1
        }
        """;

    private static string M2MTokenJson() =>
        $$"""
        {
          "access_token": "eyJhbGciOi",
          "token_type": "Bearer",
          "expires_in": 3600,
          "scope": "documents:read",
          "project_id": "{{ProjectId}}",
          "client_id": "{{ClientId}}",
          "database_id": "{{DatabaseId}}",
          "environment_id": "{{EnvironmentId}}"
        }
        """;

    private static string RestoreJson() =>
        $$"""
        {
          "restoreId": "{{RestoreId}}",
          "projectId": "{{ProjectId}}",
          "databaseId": "{{DatabaseId}}",
          "backupId": "{{BackupId}}",
          "restoredDatabaseId": "01BX5ZZKBKACTAV9WEVGEMMVS8",
          "restoredDatabaseName": "orders-restored",
          "status": "queued",
          "requestedBy": "owner@terrascale.tech",
          "requestedAt": "2026-07-05T00:00:00Z",
          "updatedAt": "2026-07-05T00:00:00Z",
          "completedAt": null,
          "errorMessage": null
        }
        """;

    private static string CreatedApiKeyJson() =>
        $$"""
        {
          "apiKey": {
            "keyId": "{{ApiKeyId}}",
            "projectId": "{{ProjectId}}",
            "name": "Orders worker",
            "prefix": "tsauth_abc",
            "lastFour": "1234",
            "scopes": ["documents:read"],
            "databaseId": "{{DatabaseId}}",
            "environmentId": "{{EnvironmentId}}",
            "createdByUserId": null,
            "createdByEmail": "owner@terrascale.tech",
            "createdAt": "2026-07-05T00:00:00Z",
            "expiresAt": null,
            "lastUsedAt": null,
            "revokedAt": null,
            "isActive": true
          },
          "secret": "returned-once"
        }
        """;

    private static string CreatedM2MApplicationJson() =>
        $$"""
        {
          "application": {
            "clientId": "{{ClientId}}",
            "projectId": "{{ProjectId}}",
            "name": "Orders sync",
            "prefix": "m2m_abc",
            "lastFour": "5678",
            "scopes": ["documents:read"],
            "databaseId": "{{DatabaseId}}",
            "environmentId": "{{EnvironmentId}}",
            "createdByUserId": null,
            "createdByEmail": "owner@terrascale.tech",
            "createdAt": "2026-07-05T00:00:00Z",
            "expiresAt": null,
            "lastUsedAt": null,
            "revokedAt": null,
            "isActive": true
          },
          "clientSecret": "returned-once"
        }
        """;

    private sealed record CapturedRequest(
        HttpMethod Method,
        string PathAndQuery,
        string? Body,
        string? ContentMediaType,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Headers);

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers
                .ToDictionary(
                    static header => header.Key,
                    static header => (IReadOnlyList<string>)header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var pathAndQuery = request.RequestUri?.PathAndQuery
                ?? throw new InvalidOperationException("Captured request did not include a request URI.");
            Requests.Add(new CapturedRequest(
                request.Method,
                pathAndQuery,
                body,
                request.Content?.Headers.ContentType?.MediaType,
                headers));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
