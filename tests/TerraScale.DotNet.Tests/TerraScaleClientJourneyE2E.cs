using System.Net;
using System.Text.Json.Serialization;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleClientJourneyE2E
{
    [Test]
    [RequiresTerraScaleClientJourney]
    public async Task Journey_CreateRepositoryAndRoundTripDocument_AgainstProvisionedDatabase()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var repositoryName = $"sdk-journey-repo-{suffix}";
        var collectionName = $"sdkjourney{suffix}";
        var documentName = $"sdk-journey-{suffix}";
        TerraScaleRepository? repository = null;
        TerraScaleDocument? created = null;

        try
        {
            repository = await client.Repositories.CreateAsync(
                repositoryName,
                collection: collectionName,
                schemaValidationEnabled: false,
                databaseId: TerraScaleApiE2E.DatabaseId,
                environmentId: TerraScaleApiE2E.EnvironmentId);
            await Assert.That(repository.RepositoryId.Length).IsEqualTo(26);
            await Assert.That(repository.Name).IsEqualTo(repositoryName);
            await Assert.That(repository.DatabaseId).IsEqualTo(TerraScaleApiE2E.DatabaseId);
            await Assert.That(repository.EnvironmentId).IsEqualTo(TerraScaleApiE2E.EnvironmentId);
            await Assert.That(repository.SchemaValidationEnabled).IsFalse();

            created = await client.Documents.CreateAsync(
                repository.RepositoryId,
                new JourneyDocument(documentName, Visits: 1));
            await Assert.That(created.Id.Length).IsEqualTo(26);
            await Assert.That(created.Data.GetProperty("name").GetString()).IsEqualTo(documentName);
            await Assert.That(created.Data.GetProperty("visits").GetInt32()).IsEqualTo(1);

            var loaded = await client.Documents.GetAsync(repository.RepositoryId, created.Id);
            await Assert.That(loaded.Id).IsEqualTo(created.Id);
            await Assert.That(loaded.Data.GetProperty("name").GetString()).IsEqualTo(documentName);
            await Assert.That(loaded.Data.GetProperty("visits").GetInt32()).IsEqualTo(1);

            var page = await client.Documents.ListAsync(repository.RepositoryId, skip: 0, take: 100);
            await Assert.That(page.Items.Any(item => item.Id == created.Id)).IsTrue();
            var listed = page.Items.Single(item => item.Id == created.Id);
            await Assert.That(listed.Data.GetProperty("name").GetString()).IsEqualTo(documentName);
            await Assert.That(listed.Data.GetProperty("visits").GetInt32()).IsEqualTo(1);
        }
        finally
        {
            if (created is not null && repository is not null)
            {
                await DeleteDocumentIfPresentAsync(client, repository.RepositoryId, created.Id);
            }

            if (repository is not null)
            {
                await DeleteRepositoryIfPresentAsync(client, repository.RepositoryId);
            }
        }
    }

    private static async Task DeleteDocumentIfPresentAsync(
        TerraScaleClient client,
        string repositoryId,
        string documentId)
    {
        try
        {
            await client.Documents.DeleteAsync(repositoryId, documentId);
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

    private sealed record JourneyDocument(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("visits")] int Visits);
}
