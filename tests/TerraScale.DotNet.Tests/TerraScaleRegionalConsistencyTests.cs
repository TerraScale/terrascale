using System.Net;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleRegionalConsistencyTests
{
    [Test]
    public async Task InvalidPreferredRegionsFailBeforeAnyRequest()
    {
        using var httpClient = new HttpClient();

        var exception = Assert.Throws<ArgumentException>(() => new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                BearerToken = "caller-token",
                PreferredRegion = "-invalid"
            }));

        await Assert.That(exception!.ParamName).IsEqualTo("PreferredRegion");
    }

    [Test]
    public async Task UnknownRegionalOptionsFailBeforeAnyRequest()
    {
        using var httpClient = new HttpClient();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                BearerToken = "caller-token",
                RegionAffinity = (TerraScaleRegionAffinity)99
            }));

        await Assert.That(exception!.ParamName).IsEqualTo("RegionAffinity");
    }

    [Test]
    public async Task RetryOptionsRejectUnboundedValues()
    {
        using var attemptsHttpClient = new HttpClient();
        using var delayHttpClient = new HttpClient();

        var attemptsException = Assert.Throws<ArgumentOutOfRangeException>(() => new TerraScaleClient(
            attemptsHttpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                MaxRetryAttempts = 9
            }));
        var delayException = Assert.Throws<ArgumentOutOfRangeException>(() => new TerraScaleClient(
            delayHttpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                RetryDelay = TimeSpan.FromSeconds(30.001)
            }));

        await Assert.That(attemptsException!.ParamName).IsEqualTo("MaxRetryAttempts");
        await Assert.That(delayException!.ParamName).IsEqualTo("RetryDelay");
    }

    [Test]
    public async Task DefaultAndStrictClientsUseTheContractedPublicHosts()
    {
        using var globalHttpClient = new HttpClient();
        _ = new TerraScaleClient(
            globalHttpClient,
            new TerraScaleClientOptions { BearerToken = "caller-token" });

        await Assert.That(globalHttpClient.BaseAddress)
            .IsEqualTo(new Uri("https://api.terrascale.tech"));

        using var strictHttpClient = new HttpClient();
        _ = new TerraScaleClient(
            strictHttpClient,
            new TerraScaleClientOptions
            {
                BearerToken = "caller-token",
                PreferredRegion = "SFO",
                RegionAffinity = TerraScaleRegionAffinity.Strict
            });

        await Assert.That(strictHttpClient.BaseAddress)
            .IsEqualTo(new Uri("https://sfo.api.terrascale.tech"));
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task ConditionalMutationConflictAndSessionTokensRoundTripAgainstRealApi()
    {
        var client = new TerraScaleClient(
            new HttpClient(),
            new TerraScaleClientOptions
            {
                BaseAddress = TerraScaleApiE2E.BaseAddress!,
                ApiKey = TerraScaleApiE2E.ApiKey,
                ProjectId = TerraScaleApiE2E.ProjectId,
                SessionConsistency = TerraScaleSessionConsistency.Automatic
            });
        var repository = TerraScaleApiE2E.Repository;
        var created = await client.Documents.CreateAsync(
            repository,
            new { status = $"sdk-e2e-consistency-{Guid.NewGuid():N}", quantity = 1 });

        try
        {
            await Assert.That(created.ChangeToken).IsNotNull();
            await Assert.That(created.SessionConsistencyToken).IsNotNull();

            var originalChangeToken = created.ChangeToken!;
            var originalSessionToken = created.SessionConsistencyToken!;
            var updated = await client.Documents.UpsertAsync(
                repository,
                created.Id,
                new { status = "conditional-update", quantity = 2 },
                new TerraScaleDocumentMutationOptions(originalChangeToken));

            await Assert.That(updated.ChangeToken).IsNotEqualTo(originalChangeToken);
            await Assert.That(updated.SessionConsistencyToken).IsNotNull();
            await Assert.That(updated.SessionConsistencyToken).IsNotEqualTo(originalSessionToken);

            var conflict = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
                client.Documents.PatchAsync(
                    repository,
                    created.Id,
                    new { quantity = 3 },
                    new TerraScaleDocumentMutationOptions(originalChangeToken)));

            await Assert.That(conflict!.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(conflict.ErrorCode).IsEqualTo("TERRASCALE-1401");

            var consistentRead = await client.Documents.GetAsync(
                repository,
                created.Id,
                new TerraScaleDocumentReadOptions(AllowStale: true));
            await Assert.That(consistentRead.Id).IsEqualTo(created.Id);
            await Assert.That(consistentRead.Data.GetProperty("quantity").GetInt32()).IsEqualTo(2);
        }
        finally
        {
            await DeleteIfPresentAsync(client, repository, created.Id);
        }
    }

    [Test]
    public async Task InvalidExpectedChangeTokensFailBeforeSending()
    {
        using var httpClient = new HttpClient();
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                BearerToken = "caller-token"
            });

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.Documents.DeleteAsync(
                "repository-a",
                "document-a",
                new TerraScaleDocumentMutationOptions("   ")));

        await Assert.That(exception!.ParamName).IsEqualTo("mutationOptions");
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
}
