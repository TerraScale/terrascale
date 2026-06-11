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
