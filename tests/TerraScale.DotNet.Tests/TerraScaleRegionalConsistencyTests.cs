using System.Diagnostics;
using System.Net;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleRegionalConsistencyTests
{
    [Test]
    public async Task InvalidPreferredRegionsFailBeforeAnyRequest()
    {
        string[] rejectedCodes =
        [
            string.Empty,
            " ",
            "gru",
            "sfo",
            "fra",
            " GRU",
            "GRU ",
            "SJC",
            "GR",
            "GRUU",
            "ĞRU"
        ];

        foreach (var rejectedCode in rejectedCodes)
        {
            using var httpClient = new HttpClient();
            var exception = Assert.Throws<ArgumentException>(() => new TerraScaleClient(
                httpClient,
                new TerraScaleClientOptions
                {
                    BaseAddress = new Uri("https://api.example.invalid/"),
                    BearerToken = "caller-token",
                    PreferredRegion = rejectedCode
                }));

            await Assert.That(exception).IsNotNull();
            var argumentException = exception
                ?? throw new InvalidOperationException("The invalid preferred region did not throw an argument exception.");
            await Assert.That(argumentException.ParamName).IsEqualTo("PreferredRegion");
        }
    }

    [Test]
    public async Task FailoverClientPreservesCanonicalPreferredRegionHeader()
    {
        using var handler = new PreferredRegionHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                BearerToken = "caller-token",
                PreferredRegion = TerraScaleRegions.Gru
            });

        _ = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync("repository-a", "missing-document"));

        await Assert.That(handler.PreferredRegion).IsEqualTo(TerraScaleRegions.Gru);
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

        await Assert.That(exception).IsNotNull();
        var argumentException = exception
            ?? throw new InvalidOperationException("The unknown region affinity did not throw an argument exception.");
        await Assert.That(argumentException.ParamName).IsEqualTo("RegionAffinity");
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

        await Assert.That(attemptsException).IsNotNull();
        await Assert.That(delayException).IsNotNull();
        var retryAttemptsException = attemptsException
            ?? throw new InvalidOperationException("The unbounded retry attempts did not throw an argument exception.");
        var retryDelayException = delayException
            ?? throw new InvalidOperationException("The excessive retry delay did not throw an argument exception.");
        await Assert.That(retryAttemptsException.ParamName).IsEqualTo("MaxRetryAttempts");
        await Assert.That(retryDelayException.ParamName).IsEqualTo("RetryDelay");
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
        using var httpClient = new HttpClient();
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = TerraScaleApiE2E.BaseAddress
                    ?? throw new InvalidOperationException(TerraScaleApiE2E.SkipReason),
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

            var originalChangeToken = created.ChangeToken
                ?? throw new InvalidOperationException("The created document did not include a change token.");
            var originalSessionToken = created.SessionConsistencyToken
                ?? throw new InvalidOperationException("The created document did not include a session consistency token.");
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

            await Assert.That(conflict).IsNotNull();
            var conflictException = conflict
                ?? throw new InvalidOperationException("The stale mutation did not return an API exception.");
            await Assert.That(conflictException.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(conflictException.ErrorCode).IsEqualTo("TERRASCALE-1401");

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
    [RequiresTerraScaleApi]
    public async Task BatchSessionTokensRemainScopedToEachDocumentAgainstRealApi()
    {
        using var httpClient = new HttpClient();
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = TerraScaleApiE2E.BaseAddress
                    ?? throw new InvalidOperationException(TerraScaleApiE2E.SkipReason),
                ApiKey = TerraScaleApiE2E.ApiKey,
                ProjectId = TerraScaleApiE2E.ProjectId,
                SessionConsistency = TerraScaleSessionConsistency.Automatic
            });
        var repository = TerraScaleApiE2E.Repository;
        TerraScaleDocument? first = null;
        TerraScaleDocument? second = null;

        try
        {
            first = await client.Documents.CreateAsync(
                repository,
                new { status = $"sdk-e2e-batch-consistency-a-{Guid.NewGuid():N}", quantity = 1 });
            second = await client.Documents.CreateAsync(
                repository,
                new { status = $"sdk-e2e-batch-consistency-b-{Guid.NewGuid():N}", quantity = 2 });

            var batch = await client.Documents.BatchAsync(
                repository,
                [
                    TerraScaleDocumentBatchOperation.Upsert(
                        first.Id,
                        new { status = "batch-consistency-a", quantity = 11 }),
                    TerraScaleDocumentBatchOperation.Upsert(
                        second.Id,
                        new { status = "batch-consistency-b", quantity = 22 })
                ]);

            var firstResult = batch.Results.Single(result => result.Id == first.Id).Document;
            var secondResult = batch.Results.Single(result => result.Id == second.Id).Document;
            await Assert.That(firstResult?.SessionConsistencyToken).IsNotNull();
            await Assert.That(secondResult?.SessionConsistencyToken).IsNotNull();
            await Assert.That(firstResult?.SessionConsistencyToken)
                .IsNotEqualTo(secondResult?.SessionConsistencyToken);

            var firstRead = await client.Documents.GetAsync(repository, first.Id);
            var secondRead = await client.Documents.GetAsync(repository, second.Id);
            await Assert.That(firstRead.Data.GetProperty("quantity").GetInt32()).IsEqualTo(11);
            await Assert.That(secondRead.Data.GetProperty("quantity").GetInt32()).IsEqualTo(22);
        }
        finally
        {
            if (first is not null)
            {
                await DeleteIfPresentAsync(client, repository, first.Id);
            }

            if (second is not null)
            {
                await DeleteIfPresentAsync(client, repository, second.Id);
            }
        }
    }

    [Test]
    public async Task RetryInvokesBearerProviderForEachNetworkAttempt()
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var providerCalls = 0;
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                BearerTokenProvider = _ =>
                {
                    Interlocked.Increment(ref providerCalls);
                    return ValueTask.FromResult("caller-token");
                },
                MaxRetryAttempts = 1,
                RetryDelay = TimeSpan.Zero
            });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.Documents.GetAsync("repository-a", "document-a"));

        await Assert.That(providerCalls).IsEqualTo(2);
    }

    [Test]
    [RequiresTerraScaleRegionalRouting]
    public async Task RoutingMetadataIsTypedOnARealPublicApiFailure()
    {
        using var httpClient = new HttpClient();
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = TerraScaleApiE2E.BaseAddress
                    ?? throw new InvalidOperationException(TerraScaleApiE2E.RegionalRoutingSkipReason),
                ApiKey = TerraScaleApiE2E.ApiKey,
                ProjectId = TerraScaleApiE2E.ProjectId,
                PreferredRegion = TerraScaleApiE2E.RegionalRoutingRegion,
                RegionAffinity = TerraScaleRegionAffinity.Failover
            });

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync(
                TerraScaleApiE2E.Repository,
                $"sdk-e2e-routing-missing/{Guid.NewGuid():N}"));

        await Assert.That(exception).IsNotNull();
        var apiException = exception
            ?? throw new InvalidOperationException("The missing regional document did not return an API exception.");
        await Assert.That(apiException.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(apiException.Routing).IsNotNull();
        var routing = apiException.Routing
            ?? throw new InvalidOperationException("The regional API exception did not include routing metadata.");
        await Assert.That(routing.EdgeRegion).IsNotNull();
        await Assert.That(routing.ServingRegion).IsNotNull();
        await Assert.That(routing.RouteMode).IsNotNull();
    }

    [Test]
    [RequiresTerraScaleRegionalFailure]
    [NotInParallel("regional-failure")]
    public async Task StrictUnavailableRegionIsReportedAsTypedRegionalFailure()
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

        using var httpClient = new HttpClient();
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = TerraScaleApiE2E.BaseAddress
                    ?? throw new InvalidOperationException(TerraScaleApiE2E.RegionalFailureSkipReason),
                ApiKey = TerraScaleApiE2E.ApiKey,
                ProjectId = TerraScaleApiE2E.ProjectId,
                PreferredRegion = TerraScaleApiE2E.RegionalUnavailableRegion,
                RegionAffinity = TerraScaleRegionAffinity.Strict,
                MaxRetryAttempts = 1,
                RetryDelay = TimeSpan.Zero
            });

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync(
                TerraScaleApiE2E.Repository,
                $"sdk-e2e-unavailable-region/{Guid.NewGuid():N}"));

        await Assert.That(exception).IsNotNull();
        var apiException = exception
            ?? throw new InvalidOperationException("The unavailable regional request did not return an API exception.");
        await Assert.That(apiException.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(apiException.IsRegionalUnavailable).IsTrue();

        Activity[] capturedActivities;
        lock (activities)
        {
            capturedActivities = [.. activities];
        }

        await Assert.That(capturedActivities.SelectMany(activity => activity.Events)
                .Count(activityEvent => string.Equals(activityEvent.Name, "terrascale.retry", StringComparison.Ordinal)))
            .IsEqualTo(1);
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

        await Assert.That(exception).IsNotNull();
        var argumentException = exception
            ?? throw new InvalidOperationException("The invalid expected change token did not throw an argument exception.");
        await Assert.That(argumentException.ParamName).IsEqualTo("mutationOptions");
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

    private sealed class PreferredRegionHandler : HttpMessageHandler
    {
        public string? PreferredRegion { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            PreferredRegion = request.Headers.TryGetValues(
                "X-TerraScale-Preferred-Region",
                out var values)
                ? values.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":\"not_found\",\"message\":\"Missing\",\"category\":\"not_found\"}}")
            });
        }
    }
}
