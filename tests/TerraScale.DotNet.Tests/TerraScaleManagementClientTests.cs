using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleManagementClientTests
{
    [Test]
    public async Task ManagementSurface_ExposesNamedClientsAndUsesTerraBaseUsageRoute()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"writes\":12,\"storageBytes\":4096}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);
        var management = new TerraScaleManagementClient(client);

        var usage = await management.Usage.GetAsync("database-a");

        await Assert.That(ReferenceEquals(management.Databases, client.Databases)).IsTrue();
        await Assert.That(ReferenceEquals(management.Branches, client.DatabaseBranches)).IsTrue();
        await Assert.That(ReferenceEquals(management.Collections, client.Collections)).IsTrue();
        await Assert.That(ReferenceEquals(management.Schemas, client.Schemas)).IsTrue();
        await Assert.That(ReferenceEquals(management.Backups, client.Backups)).IsTrue();
        await Assert.That(ReferenceEquals(management.ApiKeys, client.ApiKeys)).IsTrue();
        await Assert.That(usage.Writes).IsEqualTo(12);
        await Assert.That(usage.StorageBytes).IsEqualTo(4096);
        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v1/databases/database-a/usage"));
    }

    [Test]
    public async Task Usage_RejectsBlankDatabaseIdBeforeSending()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        using var httpClient = new HttpClient(handler);
        var management = new TerraScaleManagementClient(CreateClient(httpClient));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => management.Usage.GetAsync(" "));

        await Assert.That(exception).IsNotNull();
        await Assert.That(handler.Request).IsNull();
    }

    [Test]
    public async Task Usage_PropagatesTypedApiErrors()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.TooManyRequests,
            "{\"error\":{\"code\":\"usage_unavailable\",\"message\":\"try later\"}}");
        using var httpClient = new HttpClient(handler);
        var management = new TerraScaleManagementClient(CreateClient(httpClient));

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() => management.Usage.GetAsync("database-a"));

        if (exception is null)
        {
            throw new InvalidOperationException("The usage error fixture did not produce an API exception.");
        }

        await Assert.That(exception.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(exception.ErrorCode).IsEqualTo("usage_unavailable");
    }

    [Test]
    public async Task Usage_HonorsCancellationToken()
    {
        using var handler = new CancellationObservingHandler();
        using var httpClient = new HttpClient(handler);
        var management = new TerraScaleManagementClient(CreateClient(httpClient));
        using var cancellation = new CancellationTokenSource();

        var operation = management.Usage.GetAsync("database-a", cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => operation);

        await Assert.That(exception).IsNotNull();
        await Assert.That(handler.CancellationObserved).IsTrue();
    }

    [Test]
    public async Task ManagementSurface_DelegatesApiKeyOperationWithSharedIdempotency()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
            IdempotencyKeyFactory = () => "stable-management-key"
        });
        var management = new TerraScaleManagementClient(client);

        _ = await management.ApiKeys.CreateAsync("project-a", new TerraScaleCreateApiKeyRequest { Name = "automation" });

        await Assert.That(handler.Request).IsEqualTo(new RecordedRequest(
            HttpMethod.Post,
            "https://api.example.invalid/api/v1/projects/project-a/auth/api-keys"));
        await Assert.That(handler.IdempotencyKey).IsEqualTo("stable-management-key");
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key"
    });

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public RecordedRequest? Request { get; private set; }

        public string? IdempotencyKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri
                ?? throw new InvalidOperationException("The request URI fixture is missing.");
            Request = new RecordedRequest(request.Method, requestUri.ToString());
            IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values)
                ? values.Single()
                : null;
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CancellationObservingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("The cancellation fixture completed unexpectedly.");
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri);
}
