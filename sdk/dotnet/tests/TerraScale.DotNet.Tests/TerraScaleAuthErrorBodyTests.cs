using System.Net;
using System.Net.Http.Headers;
using TerraScale.DotNet.Tests.TerraBase;
using TUnit.Core;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthErrorBodyTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InterruptedAuthErrorBodyPreservesStatusAndRetryMetadata(bool acquisitionFailure)
    {
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = acquisitionFailure ? new BrokenContent() : new StreamContent(new BrokenStream())
            };
            response.Headers.TryAddWithoutValidation("X-Request-ID", "01JA0000000000000000000001");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.example.test"),
            ApiKey = "auth-test-key",
            InstanceSlug = "test",
        });
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());
        await Assert.That(error!.RequestId).IsEqualTo("01JA0000000000000000000001");
        await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(error.ErrorCode).IsNull();
    }

    [Test]
    public async Task UndeclaredSerializingAuthErrorStopsAtTheBodyLimit()
    {
        using var content = new OversizedContent();
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = content,
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)) }
        }));
        using var http = new HttpClient(handler);
        var client = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.example.test"),
            ApiKey = "auth-test-key",
            InstanceSlug = "test",
        });
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(error.ErrorCode).IsNull();
        await Assert.That(content.BytesWritten).IsEqualTo(64 * 1024);
        await Assert.That(content.AttemptedBytes).IsEqualTo(72 * 1024);
    }

    private sealed class OversizedContent : HttpContent
    {
        internal int BytesWritten { get; private set; }
        internal int AttemptedBytes { get; private set; }
        protected override async Task SerializeToStreamAsync(Stream destination, TransportContext? context)
        {
            var chunk = new byte[8192];
            for (var index = 0; index < 1024; index++)
            {
                AttemptedBytes += chunk.Length;
                await destination.WriteAsync(chunk);
                BytesWritten += chunk.Length;
            }
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new HttpRequestException("Body acquisition interrupted.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Body interrupted.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Body interrupted."));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
