using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.Tests.TerraBase;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthDiagnosticsTests
{
    private const string RequestId = "01JA0000000000000000000001";
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

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.customer.example"),
        InstanceSlug = "acme",
        ApiKey = Secret
    });

    private static HttpClient Http(string body, HttpStatusCode status) => new(new Handler(body, status));

    private sealed class Handler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                ReasonPhrase = Secret,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                Headers = { { "X-Request-ID", RequestId } }
            });
    }

    private sealed class StreamHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = content, Headers = { { "X-Request-ID", RequestId } } });
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
}
