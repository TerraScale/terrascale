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
