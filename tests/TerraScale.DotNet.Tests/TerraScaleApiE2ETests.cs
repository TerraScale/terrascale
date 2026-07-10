using System.Net;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleApiE2ETests
{
    [Test]
    [RequiresTerraScaleBaseAddress]
    public async Task Regions_ListAsync_ReturnsCanonicalCatalogAgainstRealApi()
    {
        var client = TerraScaleApiE2E.CreateAnonymousClient();
        string[] expectedRegions = [TerraScaleRegions.Gru, TerraScaleRegions.Sfo, TerraScaleRegions.Fra];

        var regions = await client.Regions.ListAsync();

        await Assert.That(regions.Count).IsEqualTo(3);
        await Assert.That(regions.Select(region => region.Code).SequenceEqual(expectedRegions)).IsTrue();
        await Assert.That(regions.Any(region => region.Code is "ashburn" or "ord")).IsFalse();

        var gru = regions.Single(region => region.Code == TerraScaleRegions.Gru);
        await Assert.That(gru.Name).IsEqualTo("Sao Paulo");
        await Assert.That(gru.Country).IsEqualTo("BR");
        await Assert.That(gru.Latitude).IsEqualTo(-23.5475);
        await Assert.That(gru.Longitude).IsEqualTo(-46.6361);
        await Assert.That(gru.Available).IsTrue();

        var sfo = regions.Single(region => region.Code == TerraScaleRegions.Sfo);
        await Assert.That(sfo.Name).IsEqualTo("San Francisco");
        await Assert.That(sfo.Country).IsEqualTo("US");
        await Assert.That(sfo.Latitude).IsEqualTo(37.7749);
        await Assert.That(sfo.Longitude).IsEqualTo(-122.4194);
        await Assert.That(sfo.Available).IsTrue();

        var fra = regions.Single(region => region.Code == TerraScaleRegions.Fra);
        await Assert.That(fra.Name).IsEqualTo("Frankfurt");
        await Assert.That(fra.Country).IsEqualTo("DE");
        await Assert.That(fra.Latitude).IsEqualTo(50.1109);
        await Assert.That(fra.Longitude).IsEqualTo(8.6821);
        await Assert.That(fra.Available).IsTrue();
    }

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
            await Assert.That(created.Regions.SequenceEqual(currentProject.Regions)).IsTrue();

            var loaded = await client.ProjectDatabases.GetAsync(currentProject.ProjectId, created.DatabaseId);
            await Assert.That(loaded.DatabaseId).IsEqualTo(created.DatabaseId);
            await Assert.That(loaded.ProjectId).IsEqualTo(created.ProjectId);
            await Assert.That(loaded.Name).IsEqualTo(databaseName);
            await Assert.That(loaded.Regions.SequenceEqual(currentProject.Regions)).IsTrue();

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
    public async Task ProjectQueues_CreatePublishReceiveAcknowledgePurgeAndDelete_RoundTripAgainstRealApi()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var currentProject = await GetCurrentProjectAsync(client);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var queueName = $"sdk-e2e-queue-{suffix}";
        var marker = $"sdk-e2e-message-{suffix}";
        TerraScaleProjectQueue? created = null;

        try
        {
            created = await client.Queues.CreateAsync(
                currentProject.ProjectId,
                new TerraScaleCreateProjectQueueRequest(
                    queueName,
                    Description: "TerraScale .NET SDK queue integration test",
                    RetentionSeconds: 3600,
                    MaxMessageSizeBytes: 64 * 1024,
                    PartitionCount: 1,
                    DuplicateDetectionSeconds: 120));

            await Assert.That(created.QueueId.Length).IsEqualTo(26);
            await Assert.That(created.ProjectId).IsEqualTo(currentProject.ProjectId);
            await Assert.That(created.Name).IsEqualTo(queueName);
            await Assert.That(created.Kind).IsEqualTo("Queue");
            await Assert.That(created.Replicas).IsEqualTo(3);
            await Assert.That(created.PartitionCount).IsEqualTo(1);
            await Assert.That(created.RetentionSeconds).IsEqualTo(3600);

            var listed = await client.Queues.ListAsync(currentProject.ProjectId);
            await Assert.That(listed.Any(queue => queue.QueueId == created.QueueId)).IsTrue();

            var loaded = await client.Queues.GetAsync(currentProject.ProjectId, created.QueueId);
            await Assert.That(loaded.QueueId).IsEqualTo(created.QueueId);
            await Assert.That(loaded.Name).IsEqualTo(queueName);

            var publishRequest = new TerraScalePublishProjectQueueMessageRequest<object>(
                new { task = marker, attempt = 1 },
                IdempotencyKey: $"sdk-e2e-{suffix}",
                PartitionKey: "orders",
                TimeToLiveSeconds: 300);
            var published = await client.Queues.PublishAsync(
                currentProject.ProjectId,
                created.QueueId,
                publishRequest);
            await Assert.That(published.MessageId).IsNotEmpty();
            await Assert.That(published.Sequence > 0).IsTrue();
            await Assert.That(published.Duplicate).IsFalse();

            var duplicate = await client.Queues.PublishAsync(
                currentProject.ProjectId,
                created.QueueId,
                publishRequest);
            await Assert.That(duplicate.MessageId).IsNotEmpty();
            await Assert.That(duplicate.Sequence).IsEqualTo(published.Sequence);
            await Assert.That(duplicate.Duplicate).IsTrue();

            var peeked = await client.Queues.PeekAsync(currentProject.ProjectId, created.QueueId, maxMessages: 10);
            var message = peeked.Single(candidate => candidate.MessageId == published.MessageId);
            await Assert.That(message.ContentType).IsEqualTo("application/json");
            await Assert.That(message.Data.GetProperty("task").GetString()).IsEqualTo(marker);
            await Assert.That(message.Data.GetProperty("attempt").GetInt32()).IsEqualTo(1);

            var received = (await client.Queues.ReceiveAsync(
                currentProject.ProjectId,
                created.QueueId,
                new TerraScaleReceiveProjectQueueMessagesRequest(
                    $"sdk-worker-{suffix}",
                    MaxMessages: 1,
                    WaitSeconds: 2,
                    LockSeconds: 10))).Single();
            await Assert.That(received.MessageId).IsEqualTo(published.MessageId);
            await Assert.That(received.DeliveryGuarantee)
                .IsEqualTo(TerraScaleQueueDeliveryGuarantees.AtLeastOnce);

            var renewed = await client.Queues.RenewLockAsync(
                currentProject.ProjectId,
                created.QueueId,
                received.DeliveryReceipt);
            var abandoned = await client.Queues.AbandonAsync(
                currentProject.ProjectId,
                created.QueueId,
                renewed.DeliveryReceipt,
                delaySeconds: 0.1);
            await Assert.That(abandoned.Operation).IsEqualTo("abandon");
            await Assert.That(abandoned.SafeToRetry).IsTrue();

            var redelivered = (await client.Queues.ReceiveAsync(
                currentProject.ProjectId,
                created.QueueId,
                new TerraScaleReceiveProjectQueueMessagesRequest(
                    $"sdk-worker-{suffix}",
                    MaxMessages: 1,
                    WaitSeconds: 3,
                    LockSeconds: 10))).Single();
            await Assert.That(redelivered.MessageId).IsEqualTo(published.MessageId);
            await Assert.That(redelivered.DeliveryAttempt >= 2).IsTrue();
            var completed = await client.Queues.CompleteAsync(
                currentProject.ProjectId,
                created.QueueId,
                redelivered.DeliveryReceipt);
            await Assert.That(completed.Accepted).IsTrue();
            await Assert.That(completed.Operation).IsEqualTo("complete");

            await client.Queues.PublishAsync(
                currentProject.ProjectId,
                created.QueueId,
                new TerraScalePublishProjectQueueMessageRequest<object>(
                    new { task = $"purge-{suffix}" },
                    IdempotencyKey: $"sdk-e2e-purge-{suffix}"));

            var purged = await client.Queues.PurgeAsync(currentProject.ProjectId, created.QueueId);
            await Assert.That(purged.PurgedMessages >= 1).IsTrue();

            var deleted = await client.Queues.DeleteAsync(currentProject.ProjectId, created.QueueId);
            await Assert.That(deleted).IsTrue();
            var deletedQueueId = created.QueueId;
            created = null;

            var missing = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
                client.Queues.GetAsync(currentProject.ProjectId, deletedQueueId));
            await Assert.That(missing!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        finally
        {
            if (created is not null)
            {
                await DeleteProjectQueueIfPresentAsync(client, currentProject.ProjectId, created.QueueId);
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

    private static async Task DeleteProjectQueueIfPresentAsync(
        TerraScaleClient client,
        string projectId,
        string queueId)
    {
        try
        {
            await client.Queues.DeleteAsync(projectId, queueId);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }
}
