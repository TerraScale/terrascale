using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleTenantsClientTests
{
    [Test]
    public async Task TenantOperations_SendTypedRoutesBodiesAndStableIdempotency()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var root = CreateClient(httpClient);
        var tenants = new TerraScaleTenantsClient(root);

        var created = await tenants.CreateAsync(
            new TerraScaleCreateTenantRequest("acme", "saas", [TerraScaleRegions.Gru]));
        var loaded = await tenants.GetAsync("acme");
        var page = await tenants.ListAsync(25, "cursor one");
        var deleted = await tenants.DeleteAsync("acme");
        var key = await tenants.CreateKeyAsync("acme");
        var branch = await tenants.CreateSupportBranchAsync("acme");

        await Assert.That(handler.Requests).Count().IsEqualTo(6);
        await Assert.That(handler.Requests[0]).IsEqualTo(new RecordedRequest(
            HttpMethod.Post,
            "https://api.example.invalid/api/v1/tenants",
            "{\"key\":\"acme\",\"template\":\"saas\",\"regions\":[\"GRU\"]}",
            "tenant-operation-key",
            null));
        await Assert.That(handler.Requests[1]).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v1/tenants/acme",
            null,
            null,
            null));
        await Assert.That(handler.Requests[2]).IsEqualTo(new RecordedRequest(
            HttpMethod.Get,
            "https://api.example.invalid/api/v1/tenants?limit=25&cursor=cursor%20one",
            null,
            null,
            null));
        await Assert.That(handler.Requests[3]).IsEqualTo(new RecordedRequest(
            HttpMethod.Delete,
            "https://api.example.invalid/api/v1/tenants/acme",
            null,
            "tenant-operation-key",
            "acme"));
        await Assert.That(handler.Requests[4]).IsEqualTo(new RecordedRequest(
            HttpMethod.Post,
            "https://api.example.invalid/api/v1/tenants/acme/keys",
            "{}",
            "tenant-operation-key",
            null));
        await Assert.That(handler.Requests[5]).IsEqualTo(new RecordedRequest(
            HttpMethod.Post,
            "https://api.example.invalid/api/v1/tenants/acme/branches",
            "{}",
            "tenant-operation-key",
            null));
        await Assert.That(created.DatabaseId).IsEqualTo("db-acme");
        await Assert.That(loaded.Key).IsEqualTo("acme");
        await Assert.That(page.Tenants).Count().IsEqualTo(1);
        await Assert.That(page.NextCursor).IsEqualTo("cursor-next");
        await Assert.That(deleted.Deleted).IsTrue();
        await Assert.That(key.Token).IsEqualTo("tenant-secret");
        await Assert.That(branch.Status).IsEqualTo("ready");
    }

    [Test]
    public async Task CreateAsync_RejectsInvalidPlacementBeforeSending()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var tenants = new TerraScaleTenantsClient(CreateClient(httpClient));

        var empty = await Assert.ThrowsAsync<ArgumentException>(() => tenants.CreateAsync(
            new TerraScaleCreateTenantRequest("acme", Regions: [])));
        var duplicate = await Assert.ThrowsAsync<ArgumentException>(() => tenants.CreateAsync(
            new TerraScaleCreateTenantRequest("acme", Regions: [TerraScaleRegions.Gru, TerraScaleRegions.Gru])));
        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => tenants.CreateAsync(
            new TerraScaleCreateTenantRequest("acme", Regions: ["SJC"])));

        await Assert.That(empty).IsNotNull();
        await Assert.That(duplicate).IsNotNull();
        await Assert.That(unknown).IsNotNull();
        await Assert.That(handler.Requests).Count().IsEqualTo(0);
    }

    [Test]
    public async Task ListAsync_ValidatesPaginationAndOmitsBlankCursor()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var tenants = new TerraScaleTenantsClient(CreateClient(httpClient));

        await tenants.ListAsync(cursor: " ");
        var invalidLimit = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tenants.ListAsync(0));

        await Assert.That(handler.Requests[0].Uri).IsEqualTo(
            "https://api.example.invalid/api/v1/tenants?limit=100");
        await Assert.That(invalidLimit).IsNotNull();
    }

    [Test]
    public async Task TenantOperations_PropagateCancellation()
    {
        using var handler = new CancellationHandler();
        using var httpClient = new HttpClient(handler);
        var tenants = new TerraScaleTenantsClient(CreateClient(httpClient));
        using var cancellation = new CancellationTokenSource();

        var request = tenants.GetAsync("acme", cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => request);
        await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task CreateAsync_ReusesOneIdempotencyKeyAcrossRetries()
    {
        using var handler = new RetryHandler();
        using var httpClient = new HttpClient(handler);
        var root = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
            IdempotencyKeyFactory = () => "same-tenant-key",
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.Zero
        });
        var tenants = new TerraScaleTenantsClient(root);

        await tenants.CreateAsync("acme");

        await Assert.That(handler.IdempotencyKeys).Count().IsEqualTo(2);
        await Assert.That(handler.IdempotencyKeys[0]).IsEqualTo("same-tenant-key");
        await Assert.That(handler.IdempotencyKeys[1]).IsEqualTo("same-tenant-key");
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(httpClient, new TerraScaleClientOptions
    {
        BaseAddress = new Uri("https://api.example.invalid/"),
        ApiKey = "test-key",
        IdempotencyKeyFactory = () => "tenant-operation-key"
    });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            Requests.Add(new RecordedRequest(
                request.Method,
                uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues("X-TerraScale-Delete-Confirmation", out var confirmations)
                    ? confirmations.Single()
                    : null));

            var body = uri.AbsolutePath switch
            {
                "/api/v1/tenants" when request.Method == HttpMethod.Post => "{\"key\":\"acme\",\"databaseId\":\"db-acme\",\"regions\":[\"GRU\"],\"createdAt\":\"2026-09-04T00:00:00Z\"}",
                "/api/v1/tenants" => "{\"tenants\":[{\"key\":\"acme\",\"databaseId\":\"db-acme\",\"regions\":[\"GRU\"]}],\"nextCursor\":\"cursor-next\"}",
                "/api/v1/tenants/acme/keys" => "{\"keyId\":\"key-acme\",\"token\":\"tenant-secret\",\"tenantKey\":\"acme\"}",
                "/api/v1/tenants/acme/branches" => "{\"branchId\":\"branch-acme\",\"tenantKey\":\"acme\",\"status\":\"ready\"}",
                _ when request.Method == HttpMethod.Delete => "{\"key\":\"acme\",\"databaseId\":\"db-acme\",\"deleted\":true}",
                _ => "{\"key\":\"acme\",\"databaseId\":\"db-acme\",\"regions\":[\"GRU\"]}"
            };
            return new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation fixture unexpectedly completed.");
        }
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        public List<string?> IdempotencyKeys { get; } = [];

        private int attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            IdempotencyKeys.Add(request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null);
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"busy\",\"message\":\"try again\"}}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"key\":\"acme\",\"databaseId\":\"db-acme\",\"regions\":[\"GRU\"]}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? Body,
        string? IdempotencyKey,
        string? DeleteConfirmation);
}
