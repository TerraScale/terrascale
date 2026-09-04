using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleClientTests
{
    [Test]
    public async Task RootExposesComposedPublicSurfaces()
    {
        using var httpClient = new HttpClient(new RecordingHandler("[]"));
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                BearerToken = "caller-token",
            });

        await Assert.That(client.Management).IsNotNull();
        await Assert.That(client.Tenants).IsNotNull();
        await Assert.That(client.Sql).IsNotNull();
        await Assert.That(client.Reactive).IsNotNull();
        await Assert.That(ReferenceEquals(client.Management.Databases, client.Databases)).IsTrue();
    }

    [Test]
    public async Task ApiKeyAuthenticationUsesCanonicalBearerHeader()
    {
        var handler = new RecordingHandler("{\"matched\":0,\"rows\":[]}");
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                ApiKey = "project-key",
            });

        _ = await client.Sql.ExecuteAsync("database-a", TerraScaleSqlQuery.Raw("SELECT * FROM docs"));

        await Assert.That(handler.Authorization).IsEqualTo("Bearer project-key");
        await Assert.That(handler.ApiKeyHeader).IsNull();
    }

    [Test]
    public async Task RootSqlSurfaceReusesCallerIdempotencyAcrossRetries()
    {
        var handler = new RetryHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.invalid/"),
                BearerToken = "caller-token",
                IdempotencyKeyFactory = () => "sql-operation-key",
                MaxRetryAttempts = 1,
                RetryDelay = TimeSpan.Zero,
            });

        var result = await client.Sql.ExecuteAsync("database-a", TerraScaleSqlQuery.Raw("SELECT * FROM docs"));

        await Assert.That(result.Matched).IsEqualTo(1L);
        await Assert.That(handler.IdempotencyKeys.SequenceEqual(["sql-operation-key", "sql-operation-key"])).IsTrue();
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        public string? ApiKeyHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            ApiKeyHeader = request.Headers.TryGetValues("X-TerraScale-Auth-Key", out var values)
                ? values.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        private int attempts;

        public List<string?> IdempotencyKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            IdempotencyKeys.Add(request.Headers.TryGetValues("Idempotency-Key", out var values)
                ? values.Single()
                : null);
            var statusCode = Interlocked.Increment(ref attempts) == 1
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{\"matched\":1,\"rows\":[]}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
