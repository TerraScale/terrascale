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

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
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

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
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
            await Assert.That(missingDeleted!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
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
    public async Task TypedRepository_CreateGetQueryPatchListAndDelete_RoundTripsAgainstRealRepository()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var repository = TerraScaleApiE2E.Repository;
        var orders = client.Repository<E2EDocument>(repository);
        var marker = $"sdk-e2e-typed-{Guid.NewGuid():N}";

        var created = await orders.CreateAsync(new E2EDocument(marker, Quantity: 7));

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

            var deleted = await orders.DeleteAsync(created.Id);
            await Assert.That(deleted.Deleted).IsTrue();
        }
        finally
        {
            await DeleteIfPresentAsync(client, repository, created.Id);
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

    private sealed record E2EDocument(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("quantity")] int Quantity);
}
