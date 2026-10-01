using System.Net;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthDiagnosticsTests
{
    private const string Secret = "sk_plain_secret_canary_73d9";

    [Test]
    public async Task OptionsFormattingNeverExposesTheManagementKey()
    {
        var options = new TerraScaleAuthClientOptions { ApiKey = Secret };
        await Assert.That(options.ToString()).DoesNotContain(Secret);
        await Assert.That((options with { InstanceSlug = Secret }).ToString()).DoesNotContain(Secret);
        using var http = Http("{}", HttpStatusCode.OK);
        http.BaseAddress = new Uri("https://" + Secret + ":password@auth.customer.example");
        var failure = await Assert.ThrowsAsync<ArgumentException>(() => { Client(http); return Task.CompletedTask; });
        await Assert.That(failure!.ToString()).DoesNotContain(Secret).And.DoesNotContain("password");
    }

    [Test]
    public async Task KnownManagementCodePreservesStatusWithoutProducerTextOrBody()
    {
        var body = JsonSerializer.Serialize(new { error = new { code = "unauthorized", message = Secret }, extra = Secret });
        using var http = Http(body, HttpStatusCode.Unauthorized);
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Users.ListAsync());
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(error.ErrorCode).IsEqualTo("unauthorized");
        await Assert.That(error.Message).IsEqualTo("ts-auth returned HTTP 401.");
        await Assert.That(error.ToString()).DoesNotContain(Secret);
        await Assert.That(error.InnerException).IsNull();
    }

    [Test]
    public async Task LabeledAndPlainUnknownCodeEchoesAreOmitted()
    {
        foreach (var code in new[] { "token=" + Secret, Secret })
        {
            var body = JsonSerializer.Serialize(new { error = new { code, message = "Authorization: Bearer " + Secret } });
            using var http = Http(body, HttpStatusCode.Forbidden);
            var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Users.ListAsync());
            await Assert.That(error!.ErrorCode).IsNull();
            await Assert.That(error.Message).IsEqualTo("ts-auth returned HTTP 403.");
            await Assert.That(error.ToString()).DoesNotContain(Secret);
        }
    }

    [Test]
    public async Task OAuthDescriptionsAndUntrustedReasonPhrasesAreNeverDiagnostics()
    {
        var body = JsonSerializer.Serialize(new { error = "invalid_client", error_description = Secret });
        using var http = Http(body, HttpStatusCode.Unauthorized);
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Oidc.RequestClientCredentialsTokenAsync("client", Secret));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_client");
        await Assert.That(error.Message).IsEqualTo("ts-auth returned HTTP 401.");
        await Assert.That(error.ToString()).DoesNotContain(Secret);
    }

    [Test]
    public async Task InvalidSuccessPayloadDoesNotExposeSerializerDetails()
    {
        using var http = Http(JsonSerializer.Serialize(new { data = Secret }), HttpStatusCode.OK);
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Users.ListAsync());
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
        await Assert.That(error.Message).IsEqualTo("ts-auth returned an invalid response body.");
        await Assert.That(error.ToString()).DoesNotContain(Secret);
    }

    [Test]
    public async Task LargeErrorPayloadKeepsTypedStatusWithoutCapturingContent()
    {
        var body = JsonSerializer.Serialize(new { error = "invalid_client", extra = new string('x', 65_536) + Secret });
        using var http = Http(body, HttpStatusCode.Unauthorized);
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Users.ListAsync());
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(error.ErrorCode).IsNull();
        await Assert.That(error.ToString()).DoesNotContain(Secret);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnknownLengthAuthErrorsRespectTheStreamingByteBoundary(bool overflow)
    {
        const string prefix = "{\"error\":\"invalid_client\",\"extra\":\"";
        const string suffix = "\"}";
        var length = overflow ? 128 * 1024 : 64 * 1024;
        var body = Encoding.UTF8.GetBytes(prefix + new string('x', length - prefix.Length - suffix.Length) + suffix);
        using var stream = new CountingBodyStream(body);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var http = new HttpClient(new StreamHandler(content));
        await Assert.That(content.Headers.ContentLength).IsNull();

        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Users.ListAsync());

        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(error.ErrorCode).IsEqualTo(overflow ? null : "invalid_client");
        await Assert.That(stream.BytesRead).IsEqualTo(overflow ? 65_537 : 65_536);
        await Assert.That(stream.Disposed).IsTrue();
    }

    [Test]
    public async Task AuthDiscoveryAcceptsOwnerValidRegionsBeyondLaunchFallbacks()
    {
        var hosts = new List<string>();
        using var http = new HttpClient(new DiscoveryHandler(hosts));
        var client = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.terrascale.tech"), InstanceSlug = "acme", ApiKey = Secret
        });
        await client.Users.ListAsync();
        await Assert.That(hosts.SequenceEqual(["atlas.terrascale.tech", "ams.auth.terrascale.tech"])).IsTrue();
    }

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.customer.example"), InstanceSlug = "acme", ApiKey = Secret
    });

    private static HttpClient Http(string body, HttpStatusCode status) => new(new Handler(body, status));

    private sealed class Handler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                ReasonPhrase = Secret,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class StreamHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = content });
    }

    private sealed class CountingBodyStream(byte[] bytes) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(bytes.Length - BytesRead, buffer.Length);
            bytes.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DiscoveryHandler(List<string> hosts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            hosts.Add(request.RequestUri!.Host);
            var body = request.RequestUri.AbsolutePath == "/api/v1/auth/discovery"
                ? "{\"v\":1,\"refreshSeconds\":30,\"endpoints\":[{\"region\":\"AMS\",\"url\":\"https://ams.auth.terrascale.tech\"}]}"
                : "{\"data\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
