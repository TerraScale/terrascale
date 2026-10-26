using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthIntegrationSafetyTests
{
    [Test]
    [Arguments(307, "invalid_target", false)]
    [Arguments(401, "unauthorized", true)]
    [Arguments(403, "forbidden_role", true)]
    [Arguments(404, "not_found", true)]
    [Arguments(409, "name_taken", true)]
    [Arguments(429, "rate_limited", false)]
    [Arguments(503, "temporarily_unavailable", false)]
    public async Task ErrorsKeepSafeStatusAndRetryMetadataWithoutRetryOrSecretText(int status, string code, bool nested)
    {
        var body = nested ? JsonSerializer.Serialize(new { error = new { code, message = "fixture-canary" } })
            : JsonSerializer.Serialize(new { error = code, error_description = "fixture-canary" });
        using var handler = new Handler((_, _) =>
        {
            var response = Response(body, (HttpStatusCode)status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            response.Headers.Location = new Uri("https://untrusted.example.test");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.ListOrganizationsAsync("fixture-sign-in"));
        await Assert.That(failure!.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(failure.ErrorCode).IsEqualTo(code);
        await Assert.That(failure.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(failure.ToString()).DoesNotContain("fixture-canary");
        await Assert.That(failure.InnerException).IsNull();
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(429)]
    [Arguments(503)]
    public async Task ExplicitBearerReadsNeverRetryEvenWithZeroRetryAfter(int status)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Response("{\"error\":{\"code\":\"temporarily_unavailable\"}}", (HttpStatusCode)status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.ListOrganizationsAsync("fixture-sign-in"));
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExplicitBearerReadsNeverRetryTransportFailures()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("controlled transport failure"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(http).Platform.ListOrganizationsAsync("fixture-sign-in"));
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"items\":null,\"next\":null}")]
    [Arguments("{\"items\":[{\"id\":null}],\"next\":null}")]
    [Arguments("{\"items\":[{\"id\":123}],\"next\":null}")]
    public async Task MalformedPlatformResponsesAreBoundedSafeFailures(string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(body)));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.ListOrganizationsAsync("fixture-sign-in"));
        await Assert.That(failure!.ErrorCode).IsEqualTo("invalid_response");
        await Assert.That(failure.InnerException).IsNull();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task LargeSuccessResponsesRejectDeclaredAndUnknownLengths(bool declaredLength)
    {
        using var content = new OversizedContent(declaredLength);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.ListOrganizationsAsync("fixture-sign-in"));
        await Assert.That(failure!.ErrorCode).IsEqualTo("invalid_response");
        await Assert.That(content.Disposed).IsTrue();
        await Assert.That(content.Writes).IsEqualTo(declaredLength ? 0 : 257);
    }

    [Test]
    public async Task OAuthRequestsRejectOversizedBodiesBeforeDispatch()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("{}")));
        using var http = new HttpClient(handler);
        var client = Client(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.Oidc.RefreshTokenAsync("fixture-id", "fixture-secret", new string('x', 1_048_576)));
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task PlatformIdsLimitsAndHeadersRejectInvalidInputBeforeDispatch()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("{}")));
        using var http = new HttpClient(handler);
        var client = Client(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.Platform.GetOrganizationAsync("fixture-sign-in", "../foreign"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Platform.ListOrganizationsAsync("fixture-sign-in", 101));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Platform.ListOrganizationsAsync("fixture-sign-in", after: ""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Platform.ListOrganizationsAsync("fixture\r\nInjected: secret"));
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task OriginMutationCannotForwardCredentialsToAnotherHost()
    {
        Uri? sent = null;
        using var handler = new Handler((request, _) => { sent = request.RequestUri; return Task.FromResult(Response("{\"items\":[],\"next\":null}")); });
        using var http = new HttpClient(handler);
        var client = Client(http);
        http.BaseAddress = new Uri("https://untrusted.example.test");
        await client.Platform.ListOrganizationsAsync("fixture-sign-in");
        await Assert.That(sent!.Host).IsEqualTo("auth.example.test");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OAuthFormRefusesInheritedDefaultAuthorization(bool raw)
    {
        const string marker = "=synthetic-marker";
        using var handler = new Handler((_, _) => Task.FromResult(Response("{}")));
        using var http = new HttpClient(handler);
        if (raw)
        {
            await Assert.That(http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", marker)).IsTrue();
            await Assert.That(http.DefaultRequestHeaders.Authorization).IsNull();
        }
        else http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-other-credential");
        var original = http.DefaultRequestHeaders.NonValidated["Authorization"].ToArray();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http).Oidc.RefreshTokenAsync("fixture-id", "fixture-secret", "fixture-refresh"));
        await Assert.That(error!.Message).IsEqualTo("Unauthenticated ts-auth requests require an HttpClient without a default Authorization header.");
        await Assert.That(error.ToString()).DoesNotContain(marker);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Client(http).Oidc.RefreshTokenAsync("fixture-id", "fixture-secret", "fixture-refresh", cancellationToken: cancellation.Token));
        await Assert.That(http.DefaultRequestHeaders.NonValidated["Authorization"].SequenceEqual(original)).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task CancellationBeforeDispatchAndDuringBodyReadPreservesCallerCancellationAndDisposal()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var content = new CancelledContent();
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var client = Client(http);
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.Platform.ListOrganizationsAsync("fixture-sign-in", cancellationToken: cancelled.Token));
        await Assert.That(handler.Calls).IsEqualTo(0);
        using var inFlight = new CancellationTokenSource();
        var operation = client.Platform.ListOrganizationsAsync("fixture-sign-in", cancellationToken: inFlight.Token);
        await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inFlight.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        await Assert.That(content.Disposed).IsTrue();
        // The caller-owned HttpClient remains usable after cancellation/response disposal.
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentPlatformCallsKeepTokensIndependent()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var handler = new Handler((request, _) =>
        {
            seen.Add(request.Headers.Authorization!.Parameter!);
            return Task.FromResult(Response("{\"items\":[],\"next\":null}"));
        });
        using var http = new HttpClient(handler); var client = Client(http);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => client.Platform.ListOrganizationsAsync("fixture-token-" + index)));
        await Assert.That(seen.Order(StringComparer.Ordinal).ToArray()).IsEquivalentTo(Enumerable.Range(0, 20).Select(index => "fixture-token-" + index).ToArray());
    }

    [Test]
    public async Task PlatformApiKeySecretsRedactFormatting()
    {
        await Assert.That(new TerraScaleAuthPlatformApiKeySecret(null!, "fixture-canary").ToString()).DoesNotContain("fixture-canary");
    }

    [Test]
    [Arguments("text/html")]
    [Arguments("application/json; charset=iso-8859-1")]
    public async Task UnexpectedResponseMediaTypeIsRejected(string mediaType)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Response("{\"items\":[],\"next\":null}");
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.ListOrganizationsAsync("fixture-sign-in"));
        await Assert.That(failure!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task MissingRequiredTokenFieldsAreRejected()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("{}")));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Oidc.RequestClientCredentialsTokenAsync("fixture-client", "fixture-secret"));
        await Assert.That(failure!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task OAuthRedirectDoesNotReplayTheRefreshRequest()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Response("", HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://untrusted.example.test");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Oidc.RefreshTokenAsync("fixture-id", "fixture-secret", "fixture-refresh"));
        await Assert.That(failure!.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task BodyReadFailureDoesNotRetainCredentialCanaries()
    {
        using var content = new BrokenContent();
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.ListOrganizationsAsync("fixture-sign-in"));
        await Assert.That(failure!.ErrorCode).IsEqualTo("invalid_response");
        await Assert.That(failure.ToString()).DoesNotContain("fixture-canary");
        await Assert.That(failure.InnerException).IsNull();
    }

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.example.test"),
        InstanceSlug = "terrascale",
        MaxRetryAttempts = 2,
        RetryDelay = TimeSpan.Zero
    });
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return respond(request, cancellationToken); }
    }

    private sealed class BrokenContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("fixture-canary");
    }

    private sealed class OversizedContent(bool declaredLength) : HttpContent
    {
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 2 * 1_048_576; return declaredLength; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteBodyAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => WriteBodyAsync(stream, cancellationToken);
        private async Task WriteBodyAsync(Stream stream, CancellationToken cancellationToken)
        {
            var block = new byte[4096];
            for (var index = 0; index < 512; index++) { Writes++; await stream.WriteAsync(block, cancellationToken); }
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class CancelledContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Cancellation-aware overload required.");
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
