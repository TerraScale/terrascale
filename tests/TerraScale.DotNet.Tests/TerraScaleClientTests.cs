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
    public async Task ProjectScopeSelectorsAreExplicitAndRequireBothIdentities()
    {
        var handler = new RecordingHandler("{\"matched\":0,\"rows\":[]}");
        using var http = new HttpClient(handler);
        var client = new TerraScaleClient(http, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "project-key",
            TenantId = "tenant-scope",
            ProjectId = "project-scope",
        });
        await client.Sql.ExecuteAsync("database-a", TerraScaleSqlQuery.Raw("SELECT * FROM docs"));
        await Assert.That(handler.TenantId).IsEqualTo("tenant-scope");
        await Assert.That(handler.ProjectId).IsEqualTo("project-scope");

        foreach (var options in new[]
        {
            new TerraScaleClientOptions { TenantId = "tenant-scope" },
            new TerraScaleClientOptions { TenantId = "tenant\r\nheader", ProjectId = "project-scope" },
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
            {
                _ = new TerraScaleClient(http, options);
                return Task.CompletedTask;
            });
        }
    }

    [Test]
    [Arguments("ApiKey", "project-key\r\nX-Injected: true")]
    [Arguments("ApiKey", "project-key\u0000")]
    [Arguments("BearerToken", "caller-token\r\nX-Injected: true")]
    [Arguments("BearerToken", "caller-token\u007f")]
    public async Task AuthenticationTokensRejectControlCharactersAtConstruction(string optionName, string token)
    {
        using var httpClient = new HttpClient(new RecordingHandler("[]"));
        var options = new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
        };

        if (optionName == nameof(TerraScaleClientOptions.ApiKey))
        {
            options.ApiKey = token;
        }
        else
        {
            options.BearerToken = token;
        }

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            _ = new TerraScaleClient(httpClient, options);
            return Task.CompletedTask;
        });

        await Assert.That(exception!.ParamName).IsEqualTo(optionName);
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LongRetryAfterReturnsGuidanceWithoutRetryingEarly(bool useDate)
    {
        var retryAfter = useDate
            ? new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(5))
            : new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
        var handler = new RetryHandler { RetryAfter = retryAfter };
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            BearerToken = "caller-token",
            IdempotencyKeyFactory = () => "sql-operation-key",
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.Zero,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Sql.ExecuteAsync("database-a", TerraScaleSqlQuery.Raw("SELECT * FROM docs"), cancellationToken: deadline.Token));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(exception.RetryAfter > TimeSpan.FromMinutes(4)).IsTrue();
        await Assert.That(handler.IdempotencyKeys.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EndpointValidationRejectsInsecureProductionAndCredentialBearingUris()
    {
        using var httpClient = new HttpClient(new RecordingHandler("[]"));
        foreach (var address in new[] { "http://api.example.invalid", "https://user:secret@api.example.invalid", "https://api.example.invalid/?token=secret" })
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            {
                _ = new TerraScaleClient(httpClient, new TerraScaleClientOptions { BaseAddress = new Uri(address) });
                return Task.CompletedTask;
            });
            await Assert.That(exception).IsNotNull();
        }
    }

    [Test]
    public async Task EndpointValidationAllowsExplicitLoopbackHttp()
    {
        using var httpClient = new HttpClient(new RecordingHandler("[]"));
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions { BaseAddress = new Uri("http://127.0.0.1:18446") });
        await Assert.That(client).IsNotNull();
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        public string? ApiKeyHeader { get; private set; }
        public string? TenantId { get; private set; }
        public string? ProjectId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            TenantId = request.Headers.TryGetValues("X-TerraScale-Tenant-Id", out var tenant) ? tenant.Single() : null;
            ProjectId = request.Headers.TryGetValues("X-TerraScale-Project-Id", out var project) ? project.Single() : null;
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

        public System.Net.Http.Headers.RetryConditionHeaderValue? RetryAfter { get; init; }

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
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{\"matched\":1,\"rows\":[]}", Encoding.UTF8, "application/json"),
            };
            response.Headers.RetryAfter = RetryAfter;
            return Task.FromResult(response);
        }
    }
}
