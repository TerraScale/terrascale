using System.Net;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleApiE2ETests
{
    [Test]
    [RequiresTerraScaleApi]
    public async Task Projects_ListAndUpdate_RoundTripAgainstRealApi()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var currentProject = await GetCurrentProjectAsync(client);

        var projects = await client.Projects.ListAsync(currentProject.OrganizationId, take: 1000);
        await Assert.That(projects.Any(project => project.ProjectId == currentProject.ProjectId)).IsTrue();

        var updated = await client.Projects.UpdateAsync(currentProject.ProjectId, currentProject.Name);
        await Assert.That(updated.ProjectId).IsEqualTo(currentProject.ProjectId);
        await Assert.That(updated.OrganizationId).IsEqualTo(currentProject.OrganizationId);
        await Assert.That(updated.Name).IsEqualTo(currentProject.Name);
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task ProjectDatabases_CreateGetListAndDelete_RoundTripAgainstRealApi()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var currentProject = await GetCurrentProjectAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var databaseName = $"sdk-e2e-db-{suffix}";
        TerraScaleProjectDatabase? created = null;

        try
        {
            created = await client.ProjectDatabases.CreateAsync(currentProject.ProjectId, databaseName);
            await Assert.That(created.ProjectId).IsEqualTo(currentProject.ProjectId);
            await Assert.That(created.Name).IsEqualTo(databaseName);

            var loaded = await client.ProjectDatabases.GetAsync(currentProject.ProjectId, created.DatabaseId);
            await Assert.That(loaded.DatabaseId).IsEqualTo(created.DatabaseId);
            await Assert.That(loaded.ProjectId).IsEqualTo(created.ProjectId);
            await Assert.That(loaded.Name).IsEqualTo(databaseName);

            var listed = await client.ProjectDatabases.ListAsync(currentProject.ProjectId, take: 100);
            await Assert.That(listed.Any(database => database.DatabaseId == created.DatabaseId)).IsTrue();

            var deleted = await client.ProjectDatabases.DeleteAsync(currentProject.ProjectId, created.DatabaseId);
            await Assert.That(deleted.DatabaseId).IsEqualTo(created.DatabaseId);
            await Assert.That(deleted.ProjectId).IsEqualTo(created.ProjectId);
            await Assert.That(deleted.Deleted).IsTrue();
            created = null;

            var missing = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
                client.ProjectDatabases.GetAsync(currentProject.ProjectId, deleted.DatabaseId));
            await Assert.That(missing!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        finally
        {
            if (created is not null)
            {
                await DeleteProjectDatabaseIfPresentAsync(client, currentProject.ProjectId, created.DatabaseId);
            }
        }
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task ProjectDatabaseEnvironments_CreateGetListAndDelete_RoundTripAgainstRealApi()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var currentProject = await GetCurrentProjectAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var databaseName = $"sdk-e2e-env-db-{suffix}";
        TerraScaleProjectDatabase? database = null;
        TerraScaleProjectDatabaseEnvironment? createdEnvironment = null;

        try
        {
            database = await client.ProjectDatabases.CreateAsync(currentProject.ProjectId, databaseName);
            var environmentName = $"sdk-e2e-env-{suffix}";
            createdEnvironment = await client.Environments.CreateAsync(
                currentProject.ProjectId,
                database.DatabaseId,
                environmentName,
                "Preview");

            await Assert.That(createdEnvironment.ProjectId).IsEqualTo(currentProject.ProjectId);
            await Assert.That(createdEnvironment.DatabaseId).IsEqualTo(database.DatabaseId);
            await Assert.That(createdEnvironment.Name).IsEqualTo(environmentName);
            await Assert.That(createdEnvironment.Kind).IsEqualTo("Preview");

            var loaded = await client.Environments.GetAsync(
                currentProject.ProjectId,
                database.DatabaseId,
                createdEnvironment.EnvironmentId);
            await Assert.That(loaded.EnvironmentId).IsEqualTo(createdEnvironment.EnvironmentId);
            await Assert.That(loaded.DatabaseId).IsEqualTo(database.DatabaseId);
            await Assert.That(loaded.Name).IsEqualTo(environmentName);

            var listed = await client.Environments.ListAsync(currentProject.ProjectId, database.DatabaseId, take: 100);
            await Assert.That(listed.Any(environment => environment.EnvironmentId == createdEnvironment.EnvironmentId))
                .IsTrue();

            var deleted = await client.Environments.DeleteAsync(
                currentProject.ProjectId,
                database.DatabaseId,
                createdEnvironment.EnvironmentId);
            await Assert.That(deleted.EnvironmentId).IsEqualTo(createdEnvironment.EnvironmentId);
            await Assert.That(deleted.ProjectId).IsEqualTo(currentProject.ProjectId);
            await Assert.That(deleted.DatabaseId).IsEqualTo(database.DatabaseId);
            await Assert.That(deleted.Deleted).IsTrue();
            createdEnvironment = null;

            var missing = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
                client.Environments.GetAsync(currentProject.ProjectId, database.DatabaseId, deleted.EnvironmentId));
            await Assert.That(missing!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        finally
        {
            if (createdEnvironment is not null)
            {
                await DeleteProjectDatabaseEnvironmentIfPresentAsync(
                    client,
                    currentProject.ProjectId,
                    createdEnvironment.DatabaseId,
                    createdEnvironment.EnvironmentId);
            }

            if (database is not null)
            {
                await DeleteProjectDatabaseIfPresentAsync(client, currentProject.ProjectId, database.DatabaseId);
            }
        }
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task ApiKeys_CreateListRotateAndRevoke_RoundTripAgainstRealApi()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var currentProject = await GetCurrentProjectAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var databaseName = $"sdk-e2e-key-db-{suffix}";
        TerraScaleProjectDatabase? database = null;
        TerraScaleProjectDatabaseEnvironment? environment = null;

        try
        {
            database = await client.ProjectDatabases.CreateAsync(currentProject.ProjectId, databaseName);
            environment = await client.Environments.CreateAsync(
                currentProject.ProjectId,
                database.DatabaseId,
                $"sdk-e2e-key-env-{suffix}",
                "Preview");

            var created = await client.ApiKeys.CreateAsync(
                currentProject.ProjectId,
                new TerraScaleCreateApiKeyRequest
                {
                    Name = $"sdk-e2e-key-{suffix}",
                    Scopes = ["auth:users", "auth:sessions"],
                    DatabaseId = database.DatabaseId,
                    EnvironmentId = environment.EnvironmentId
                });

            await Assert.That(created.Secret).StartsWith("tsauth_");
            await Assert.That(created.ApiKey.ProjectId).IsEqualTo(currentProject.ProjectId);
            await Assert.That(created.ApiKey.DatabaseId).IsEqualTo(database.DatabaseId);
            await Assert.That(created.ApiKey.EnvironmentId).IsEqualTo(environment.EnvironmentId);

            var listed = await client.ApiKeys.ListAsync(currentProject.ProjectId, limit: 100);
            var listedKey = listed.ApiKeys.SingleOrDefault(apiKey => apiKey.KeyId == created.ApiKey.KeyId);
            await Assert.That(listedKey).IsNotNull();
            await Assert.That(listedKey!.IsActive).IsTrue();

            var rotated = await client.ApiKeys.RotateAsync(currentProject.ProjectId, created.ApiKey.KeyId);
            await Assert.That(rotated.ApiKey.KeyId).IsEqualTo(created.ApiKey.KeyId);
            await Assert.That(rotated.Secret).StartsWith("tsauth_");
            await Assert.That(rotated.Rotated).IsTrue();

            var revoked = await client.ApiKeys.RevokeAsync(currentProject.ProjectId, created.ApiKey.KeyId);
            await Assert.That(revoked.KeyId).IsEqualTo(created.ApiKey.KeyId);
            await Assert.That(revoked.Revoked).IsTrue();

            var afterRevoke = await client.ApiKeys.ListAsync(currentProject.ProjectId, limit: 100);
            await Assert.That(afterRevoke.ApiKeys.Single(apiKey => apiKey.KeyId == created.ApiKey.KeyId).IsActive)
                .IsFalse();
        }
        finally
        {
            if (environment is not null)
            {
                await DeleteProjectDatabaseEnvironmentIfPresentAsync(
                    client,
                    currentProject.ProjectId,
                    environment.DatabaseId,
                    environment.EnvironmentId);
            }

            if (database is not null)
            {
                await DeleteProjectDatabaseIfPresentAsync(client, currentProject.ProjectId, database.DatabaseId);
            }
        }
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task M2MApplications_CreateRotateDeleteAndTokenRequests_RoundTripAgainstRealApi()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var currentProject = await GetCurrentProjectAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var databaseName = $"sdk-e2e-m2m-db-{suffix}";
        TerraScaleProjectDatabase? database = null;
        TerraScaleProjectDatabaseEnvironment? environment = null;

        try
        {
            database = await client.ProjectDatabases.CreateAsync(currentProject.ProjectId, databaseName);
            environment = await client.Environments.CreateAsync(
                currentProject.ProjectId,
                database.DatabaseId,
                $"sdk-e2e-m2m-env-{suffix}",
                "Preview");

            const string scope = "database:read database:write repositories:read repositories:write";
            var created = await client.M2MApplications.CreateAsync(
                currentProject.ProjectId,
                new TerraScaleCreateM2MApplicationRequest
                {
                    Name = $"sdk-e2e-m2m-{suffix}",
                    Scopes =
                    [
                        "database:read",
                        "database:write",
                        "repositories:read",
                        "repositories:write"
                    ],
                    DatabaseId = database.DatabaseId,
                    EnvironmentId = environment.EnvironmentId,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
                });

            await Assert.That(created.ClientSecret).StartsWith("tsm2m_");
            await Assert.That(created.Application.ProjectId).IsEqualTo(currentProject.ProjectId);
            await Assert.That(created.Application.DatabaseId).IsEqualTo(database.DatabaseId);
            await Assert.That(created.Application.EnvironmentId).IsEqualTo(environment.EnvironmentId);

            var listed = await client.M2MApplications.ListAsync(currentProject.ProjectId, limit: 100);
            await Assert.That(listed.Applications.Any(application => application.ClientId == created.Application.ClientId))
                .IsTrue();

            var jsonToken = await client.M2MApplications.CreateTokenAsync(
                currentProject.ProjectId,
                created.Application.ClientId,
                created.ClientSecret,
                scope);
            await Assert.That(jsonToken.TokenType).IsEqualTo("Bearer");
            await Assert.That(jsonToken.ProjectId).IsEqualTo(currentProject.ProjectId);
            await Assert.That(jsonToken.ClientId).IsEqualTo(created.Application.ClientId);
            await Assert.That(jsonToken.DatabaseId).IsEqualTo(database.DatabaseId);
            await Assert.That(jsonToken.EnvironmentId).IsEqualTo(environment.EnvironmentId);
            await Assert.That(jsonToken.Scope).IsEqualTo(scope);

            var formToken = await client.M2MApplications.CreateTokenFormAsync(
                currentProject.ProjectId,
                created.Application.ClientId,
                created.ClientSecret,
                scope);
            await Assert.That(formToken.TokenType).IsEqualTo("Bearer");
            await Assert.That(formToken.ClientId).IsEqualTo(created.Application.ClientId);

            var basicToken = await client.M2MApplications.CreateTokenWithBasicCredentialsAsync(
                currentProject.ProjectId,
                created.Application.ClientId,
                created.ClientSecret,
                scope);
            await Assert.That(basicToken.TokenType).IsEqualTo("Bearer");
            await Assert.That(basicToken.ClientId).IsEqualTo(created.Application.ClientId);

            var rotated = await client.M2MApplications.RotateAsync(currentProject.ProjectId, created.Application.ClientId);
            await Assert.That(rotated.Application.ClientId).IsEqualTo(created.Application.ClientId);
            await Assert.That(rotated.ClientSecret).StartsWith("tsm2m_");
            await Assert.That(rotated.Rotated).IsTrue();

            var rotatedToken = await client.M2MApplications.CreateTokenAsync(
                currentProject.ProjectId,
                created.Application.ClientId,
                rotated.ClientSecret,
                scope);
            await Assert.That(rotatedToken.ClientId).IsEqualTo(created.Application.ClientId);

            var deleted = await client.M2MApplications.DeleteAsync(currentProject.ProjectId, created.Application.ClientId);
            await Assert.That(deleted.ClientId).IsEqualTo(created.Application.ClientId);
            await Assert.That(deleted.Revoked).IsTrue();

            var afterDelete = await client.M2MApplications.ListAsync(currentProject.ProjectId, limit: 100);
            await Assert.That(afterDelete.Applications.Single(application => application.ClientId == created.Application.ClientId)
                .RevokedAt).IsNotNull();
        }
        finally
        {
            if (environment is not null)
            {
                await DeleteProjectDatabaseEnvironmentIfPresentAsync(
                    client,
                    currentProject.ProjectId,
                    environment.DatabaseId,
                    environment.EnvironmentId);
            }

            if (database is not null)
            {
                await DeleteProjectDatabaseIfPresentAsync(client, currentProject.ProjectId, database.DatabaseId);
            }
        }
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task BackupPolicy_ConfigureAndListRestorePoints_RoundTripAgainstRealApi()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var currentProject = await GetCurrentProjectAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var databaseName = $"sdk-e2e-backup-db-{suffix}";
        TerraScaleProjectDatabase? database = null;

        try
        {
            database = await client.ProjectDatabases.CreateAsync(currentProject.ProjectId, databaseName);

            var policy = await client.Backups.ConfigurePolicyAsync(
                currentProject.ProjectId,
                database.DatabaseId,
                new TerraScaleConfigureBackupPolicyRequest(
                    "0 3 * * *",
                    "*/15 * * * *",
                    14,
                    Disabled: false));

            await Assert.That(policy.ProjectId).IsEqualTo(currentProject.ProjectId);
            await Assert.That(policy.DatabaseId).IsEqualTo(database.DatabaseId);
            await Assert.That(policy.Disabled).IsFalse();

            var restorePoints = await client.Backups.ListRestorePointsAsync(currentProject.ProjectId, database.DatabaseId);
            await Assert.That(restorePoints).IsNotNull();
        }
        finally
        {
            if (database is not null)
            {
                await DeleteProjectDatabaseIfPresentAsync(client, currentProject.ProjectId, database.DatabaseId);
            }
        }
    }

    private static async Task<TerraScaleProject> GetCurrentProjectAsync(TerraScaleClient client) =>
        await client.Projects.GetAsync(TerraScaleApiE2E.ProjectId);

    private static async Task DeleteProjectDatabaseIfPresentAsync(
        TerraScaleClient client,
        string projectId,
        string databaseId)
    {
        try
        {
            await client.ProjectDatabases.DeleteAsync(projectId, databaseId);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static async Task DeleteProjectDatabaseEnvironmentIfPresentAsync(
        TerraScaleClient client,
        string projectId,
        string databaseId,
        string environmentId)
    {
        try
        {
            await client.Environments.DeleteAsync(projectId, databaseId, environmentId);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }
}
