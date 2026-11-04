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

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.example.test"),
        InstanceSlug = "terrascale",
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
