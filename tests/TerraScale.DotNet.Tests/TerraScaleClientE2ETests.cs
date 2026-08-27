using System.Diagnostics;
using System.Net;
using System.Text.Json.Serialization;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleClientE2ETests
{
    [Test]
    [RequiresTerraScaleApi]
    public async Task Databases_ListAsync_ReturnsRealApiResponse()
    {
        var client = TerraScaleApiE2E.CreateClient();

        var databases = await client.Databases.ListAsync(take: 10);

        await Assert.That(databases).IsNotNull();
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task Databases_CreateGetAndDelete_RoundTripsAgainstRealApi()
    {
        using var httpClient = new HttpClient();
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = TerraScaleApiE2E.BaseAddress
                    ?? throw new InvalidOperationException(TerraScaleApiE2E.SkipReason),
                ApiKey = TerraScaleApiE2E.ApiKey,
                ProjectId = TerraScaleApiE2E.ProjectId
            });
        var databaseName = $"sdk-e2e-db-{Guid.NewGuid():N}"[..32];
        TerraScaleDatabaseDetails? created = null;

        try
        {
            created = await client.Databases.CreateAsync(databaseName);
            await Assert.That(created.DatabaseId.Length).IsEqualTo(26);
            await Assert.That(created.Name).IsEqualTo(databaseName);

            var loaded = await client.Databases.GetAsync(created.DatabaseId);
            await Assert.That(loaded.DatabaseId).IsEqualTo(created.DatabaseId);
            await Assert.That(loaded.Name).IsEqualTo(databaseName);

            var deleted = await client.Databases.DeleteAsync(
                created.DatabaseId,
                new TerraScaleDeleteDatabaseOptions(created.DatabaseId));
            await Assert.That(httpClient.DefaultRequestHeaders.Contains("X-TerraScale-Delete-Confirmation")).IsFalse();
            await Assert.That(deleted.DatabaseId).IsEqualTo(created.DatabaseId);
            await Assert.That(deleted.Name).IsEqualTo(databaseName);
            await Assert.That(deleted.Deleted).IsTrue();
            await Assert.That(deleted.HardDelete).IsFalse();
            created = null;

            var missing = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
                client.Databases.GetAsync(deleted.DatabaseId));
            await Assert.That(missing).IsNotNull();
            var missingException = missing
                ?? throw new InvalidOperationException("The missing database request did not return an API exception.");
            await Assert.That(missingException.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        finally
        {
            if (created is not null)
            {
                await DeleteDatabaseIfPresentAsync(client, created.DatabaseId);
            }
        }
    }

    [Test]
    public async Task Databases_DeleteAsync_WithMismatchedConfirmation_ThrowsBeforeSending()
    {
        using var httpClient = new HttpClient();
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions { BaseAddress = new Uri("https://api.example.invalid/") });

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.Databases.DeleteAsync(
                "database-a",
                new TerraScaleDeleteDatabaseOptions("database-b")));

        await Assert.That(exception).IsNotNull();
        var argumentException = exception
            ?? throw new InvalidOperationException("The mismatched database confirmation did not throw an argument exception.");
        await Assert.That(argumentException.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task ProjectDatabases_DeleteAsync_WithMismatchedConfirmation_ThrowsBeforeSending()
    {
        using var httpClient = new HttpClient();
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions { BaseAddress = new Uri("https://api.example.invalid/") });

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ProjectDatabases.DeleteAsync(
                "project-a",
                "database-a",
                new TerraScaleDeleteDatabaseOptions("database-b")));

        await Assert.That(exception).IsNotNull();
        var argumentException = exception
            ?? throw new InvalidOperationException("The mismatched project database confirmation did not throw an argument exception.");
        await Assert.That(argumentException.ParamName).IsEqualTo("options");
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task Collections_ListAsync_ReturnsRealDatabaseCollections()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var databaseName = $"sdk-e2e-col-{suffix}";
        var repositoryName = $"sdk-e2e-col-repo-{suffix}";
        var collectionName = $"sdke2ecol{suffix}";
        TerraScaleDatabaseDetails? database = null;
        TerraScaleRepository? repository = null;
        TerraScaleDocument? document = null;

        try
        {
            database = await client.Databases.CreateAsync(databaseName);
            var databaseClient = client.ForDatabase(database.DatabaseId);
            repository = await databaseClient.Repositories.CreateAsync(
                repositoryName,
                collection: collectionName,
                schemaValidationEnabled: false);
            document = await databaseClient.Documents.CreateAsync(
                repository.RepositoryId,
                new E2EDocument($"sdk-e2e-col-{suffix}", Quantity: 1));

            var collections = await client.Collections.ListAsync(database.DatabaseId);

            await Assert.That(collections.Any(collection =>
                    string.Equals(collection.Name, collectionName, StringComparison.Ordinal) &&
                    collection.Documents >= 1))
                .IsTrue();
        }
        finally
        {
            if (document is not null && repository is not null)
            {
                await DeleteIfPresentAsync(client, repository.RepositoryId, document.Id);
            }

            if (repository is not null)
            {
                await DeleteRepositoryIfPresentAsync(client, repository.RepositoryId);
            }

            if (database is not null)
            {
                await DeleteDatabaseIfPresentAsync(client, database.DatabaseId);
            }
        }
    }

    [Test]
    [RequiresTerraScaleBearerToken]
    public async Task Databases_ListAsync_UsesCallerProvidedBearerTokenProvider()
    {
        var providerCalls = 0;
        var client = TerraScaleApiE2E.CreateBearerProviderClient(cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref providerCalls);
            return ValueTask.FromResult(TerraScaleApiE2E.BearerToken);
        });

        var databases = await client.Databases.ListAsync(take: 10);

        await Assert.That(databases).IsNotNull();
        await Assert.That(providerCalls > 0).IsTrue();
    }

    [Test]
    [RequiresTerraScaleBaseAddress]
    public async Task Databases_ListAsync_WithInvalidBearerTokenProvider_ThrowsTypedApiException()
    {
        var client = TerraScaleApiE2E.CreateInvalidBearerProviderClient();

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Databases.ListAsync(take: 1));

        await Assert.That(exception).IsNotNull();
        var apiException = exception
            ?? throw new InvalidOperationException("The invalid bearer token request did not return an API exception.");
        await Assert.That(apiException.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task Documents_CreateGetQueryAndDelete_RoundTripsAgainstRealRepository()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var repository = TerraScaleApiE2E.Repository;
        var marker = $"sdk-e2e-{Guid.NewGuid():N}";

        var created = await client.Documents.CreateAsync(
            repository,
            new E2EDocument(marker, Quantity: 2));

        try
        {
            var loaded = await client.Documents.GetAsync(repository, created.Id);
            await Assert.That(loaded.Id).IsEqualTo(created.Id);
            await Assert.That(loaded.Data.GetProperty("status").GetString()).IsEqualTo(marker);

            var page = await client.Tql.QueryAsync(repository, $"from '{repository}'", take: 100);
            await Assert.That(page.Items.Any(item => item.Id == created.Id)).IsTrue();
        }
        finally
        {
            await DeleteIfPresentAsync(client, repository, created.Id);
        }
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task Documents_GetMissingDocument_ThrowsTypedApiException()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var missingId = $"sdk-e2e-missing/{Guid.NewGuid():N}";

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync(TerraScaleApiE2E.Repository, missingId));

        await Assert.That(exception).IsNotNull();
        var apiException = exception
            ?? throw new InvalidOperationException("The missing document request did not return an API exception.");
        await Assert.That(apiException.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task TransportMetadata_EmitsOpenTelemetryAndPreservesRequestIdsAgainstRealApi()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(
                source.Name,
                TerraScaleTelemetry.ActivitySourceName,
                StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                {
                    activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var requestIdPrefix = $"sdk-e2e-request-{Guid.NewGuid():N}-";
        var idempotencyPrefix = $"sdk-e2e-idempotency-{Guid.NewGuid():N}-";
        var requestIdCalls = 0;
        var idempotencyKeyCalls = 0;
        var client = new TerraScaleClient(
            new HttpClient(),
            new TerraScaleClientOptions
            {
                BaseAddress = TerraScaleApiE2E.BaseAddress ?? throw new InvalidOperationException(TerraScaleApiE2E.SkipReason),
                ApiKey = TerraScaleApiE2E.ApiKey,
                ProjectId = TerraScaleApiE2E.ProjectId,
                RequestIdFactory = () => $"{requestIdPrefix}{Interlocked.Increment(ref requestIdCalls)}",
                IdempotencyKeyFactory = () => $"{idempotencyPrefix}{Interlocked.Increment(ref idempotencyKeyCalls)}"
            });
        var repository = TerraScaleApiE2E.Repository;
        var marker = $"sdk-e2e-transport-{Guid.NewGuid():N}";
        TerraScaleDocument? created = null;

        try
        {
            await client.Databases.ListAsync(take: 1);
            created = await client.Documents.CreateAsync(repository, new E2EDocument(marker, Quantity: 1));

            var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
                client.Documents.GetAsync(repository, $"sdk-e2e-missing/{Guid.NewGuid():N}"));

            await Assert.That(exception).IsNotNull();
            var apiException = exception
                ?? throw new InvalidOperationException("The missing document request did not return an API exception.");
            await Assert.That(apiException.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(apiException.RequestId).IsNotNull();
            var requestId = apiException.RequestId
                ?? throw new InvalidOperationException("The API exception did not include a request ID.");
            await Assert.That(requestId.StartsWith(requestIdPrefix, StringComparison.Ordinal)).IsTrue();
        }
        finally
        {
            if (created is not null)
            {
                await DeleteIfPresentAsync(client, repository, created.Id);
            }
        }

        Activity[] capturedActivities;
        lock (activities)
        {
            capturedActivities = [.. activities];
        }

        await Assert.That(requestIdCalls >= 3).IsTrue();
        await Assert.That(idempotencyKeyCalls >= 1).IsTrue();
        await Assert.That(capturedActivities.Length >= 3).IsTrue();
        await Assert.That(capturedActivities.Any(activity =>
            string.Equals(activity.Source.Name, TerraScaleTelemetry.ActivitySourceName, StringComparison.Ordinal) &&
            string.Equals(activity.OperationName, "TerraScale GET", StringComparison.Ordinal) &&
            string.Equals(ReadTag(activity, "url.path"), "/api/v1/databases?start=0&take=1", StringComparison.Ordinal) &&
            string.Equals(ReadTag(activity, "http.response.status_code"), "200", StringComparison.Ordinal) &&
            ReadTag(activity, "terrascale.request_id")?.StartsWith(requestIdPrefix, StringComparison.Ordinal) == true))
            .IsTrue();
        await Assert.That(capturedActivities.Any(activity =>
            string.Equals(activity.OperationName, "TerraScale GET", StringComparison.Ordinal) &&
            string.Equals(ReadTag(activity, "http.response.status_code"), "404", StringComparison.Ordinal) &&
            ReadTag(activity, "terrascale.request_id")?.StartsWith(requestIdPrefix, StringComparison.Ordinal) == true))
            .IsTrue();
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task Documents_PatchAndBatch_RoundTripsAgainstRealRepository()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var repository = TerraScaleApiE2E.Repository;
        var marker = $"sdk-e2e-batch-{Guid.NewGuid():N}";
        var documentIds = new List<string>();

        var created = await client.Documents.CreateAsync(
            repository,
            new E2EDocument(marker, Quantity: 1));
        documentIds.Add(created.Id);

        var deleteCandidate = await client.Documents.CreateAsync(
            repository,
            new E2EDocument($"{marker}-delete", Quantity: 1));
        documentIds.Add(deleteCandidate.Id);

        try
        {
            var patched = await client.Documents.PatchAsync(
                repository,
                created.Id,
                new
                {
                    status = $"{marker}-patched",
                    quantity = 2
                });
            await Assert.That(patched.Id).IsEqualTo(created.Id);
            await Assert.That(patched.Data.GetProperty("status").GetString()).IsEqualTo($"{marker}-patched");
            await Assert.That(patched.Data.GetProperty("quantity").GetInt32()).IsEqualTo(2);

            var batch = await client.Documents.BatchAsync(
                repository,
                [
                    TerraScaleDocumentBatchOperation.Create(new E2EDocument($"{marker}-created", Quantity: 3)),
                    TerraScaleDocumentBatchOperation.Upsert(created.Id, new E2EDocument($"{marker}-upserted", Quantity: 4)),
                    TerraScaleDocumentBatchOperation.Patch(created.Id, new { quantity = 5 }),
                    TerraScaleDocumentBatchOperation.Delete(deleteCandidate.Id)
                ]);

            await Assert.That(batch.Results.Count).IsEqualTo(4);
            var batchCreated = batch.Results.Single(result => result.Index == 0);
            await Assert.That(batchCreated.Id.Length).IsEqualTo(26);
            documentIds.Add(batchCreated.Id);

            var batchUpserted = batch.Results.Single(result => result.Index == 1);
            await Assert.That(batchUpserted.Id).IsEqualTo(created.Id);

            var batchPatched = batch.Results.Single(result => result.Index == 2);
            await Assert.That(batchPatched.Document?.Data.GetProperty("quantity").GetInt32()).IsEqualTo(5);

            var batchDeleted = batch.Results.Single(result => result.Index == 3);
            await Assert.That(batchDeleted.Deleted?.Deleted).IsTrue();

            var missingDeleted = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
                client.Documents.GetAsync(repository, deleteCandidate.Id));
            await Assert.That(missingDeleted).IsNotNull();
            var missingException = missingDeleted
                ?? throw new InvalidOperationException("The deleted document request did not return an API exception.");
            await Assert.That(missingException.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        finally
        {
            foreach (var documentId in documentIds.Distinct(StringComparer.Ordinal))
            {
                await DeleteIfPresentAsync(client, repository, documentId);
            }
        }
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task TypedRepository_CreateGetQueryPatchBatchListAndDelete_RoundTripsAgainstRealRepository()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var repository = TerraScaleApiE2E.Repository;
        var orders = client.Repository<E2EDocument>(repository);
        var marker = $"sdk-e2e-typed-{Guid.NewGuid():N}";
        var documentIds = new HashSet<string>(StringComparer.Ordinal);

        var created = await orders.CreateAsync(new E2EDocument(marker, Quantity: 7));
        documentIds.Add(created.Id);

        try
        {
            await Assert.That(created.Data.Status).IsEqualTo(marker);
            await Assert.That(created.Data.Quantity).IsEqualTo(7);

            var loaded = await orders.GetAsync(created.Id);
            await Assert.That(loaded.Id).IsEqualTo(created.Id);
            await Assert.That(loaded.Data.Status).IsEqualTo(marker);

            var patched = await orders.PatchAsync(
                created.Id,
                new
                {
                    status = $"{marker}-patched",
                    quantity = 8
                });
            await Assert.That(patched.Data.Status).IsEqualTo($"{marker}-patched");
            await Assert.That(patched.Data.Quantity).IsEqualTo(8);

            var queried = await orders.QueryAsync(
                TerraScaleQuery.From(repository).Take(100));
            await Assert.That(queried.Items.Any(item =>
                    string.Equals(item.Id, created.Id, StringComparison.Ordinal) &&
                    string.Equals(item.Data.Status, $"{marker}-patched", StringComparison.Ordinal)))
                .IsTrue();

            var listed = await orders.ListAsync(take: 100);
            await Assert.That(listed.Items.Any(item => string.Equals(item.Id, created.Id, StringComparison.Ordinal)))
                .IsTrue();

            var deleteCandidate = await orders.CreateAsync(new E2EDocument($"{marker}-delete", Quantity: 1));
            documentIds.Add(deleteCandidate.Id);
            var patchCandidate = await orders.CreateAsync(new E2EDocument($"{marker}-patch", Quantity: 2));
            documentIds.Add(patchCandidate.Id);

            var batch = await orders.BatchAsync(
            [
                TerraScaleTypedDocumentBatchOperation.Create(new E2EDocument($"{marker}-batch-created", Quantity: 9)),
                TerraScaleTypedDocumentBatchOperation.Upsert(created.Id, new E2EDocument($"{marker}-batch-upserted", Quantity: 10)),
                TerraScaleTypedDocumentBatchOperation.Patch(
                    patchCandidate.Id,
                    new
                    {
                        quantity = 11
                    }),
                TerraScaleTypedDocumentBatchOperation.Delete(deleteCandidate.Id)
            ]);

            await Assert.That(batch.Repository).IsEqualTo(repository);
            await Assert.That(batch.Results.Count).IsEqualTo(4);

            var batchCreated = batch.Results.Single(result => result.Index == 0);
            await Assert.That(batchCreated.Document?.Data.Status).IsEqualTo($"{marker}-batch-created");
            await Assert.That(batchCreated.Document?.Data.Quantity).IsEqualTo(9);
            documentIds.Add(batchCreated.Id);

            var batchUpserted = batch.Results.Single(result => result.Index == 1);
            await Assert.That(batchUpserted.Id).IsEqualTo(created.Id);
            await Assert.That(batchUpserted.Document?.Data.Status).IsEqualTo($"{marker}-batch-upserted");

            var batchPatched = batch.Results.Single(result => result.Index == 2);
            await Assert.That(batchPatched.Id).IsEqualTo(patchCandidate.Id);
            await Assert.That(batchPatched.Document?.Data.Quantity).IsEqualTo(11);

            var batchDeleted = batch.Results.Single(result => result.Index == 3);
            await Assert.That(batchDeleted.Id).IsEqualTo(deleteCandidate.Id);
            await Assert.That(batchDeleted.Deleted?.Deleted).IsTrue();
            documentIds.Remove(deleteCandidate.Id);

            var deleted = await orders.DeleteAsync(created.Id);
            await Assert.That(deleted.Deleted).IsTrue();
            documentIds.Remove(created.Id);
        }
        finally
        {
            foreach (var documentId in documentIds)
            {
                await DeleteIfPresentAsync(client, repository, documentId);
            }
        }
    }

    private static async Task DeleteIfPresentAsync(
        TerraScaleClient client,
        string repository,
        string documentId)
    {
        try
        {
            await client.Documents.DeleteAsync(repository, documentId);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static async Task DeleteDatabaseIfPresentAsync(
        TerraScaleClient client,
        string databaseId)
    {
        try
        {
            await client.Databases.DeleteAsync(
                databaseId,
                new TerraScaleDeleteDatabaseOptions(databaseId));
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static async Task DeleteRepositoryIfPresentAsync(
        TerraScaleClient client,
        string repositoryId)
    {
        try
        {
            await client.Repositories.DeleteAsync(repositoryId, deleteDocuments: true);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static string? ReadTag(Activity activity, string key)
    {
        foreach (var tag in activity.TagObjects)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private sealed record E2EDocument(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("quantity")] int Quantity);
}
