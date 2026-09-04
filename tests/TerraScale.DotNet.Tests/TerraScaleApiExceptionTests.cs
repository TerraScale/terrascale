using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleApiExceptionTests
{
    [Test]
    public async Task HttpDateRetryAfterIsProjectedOnTheFinalTypedFailure()
    {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(2);
        using var handler = new ErrorHandler(_ =>
        {
            var response = ErrorResponse("{\"error\":{\"code\":\"overloaded\",\"message\":\"Try later.\"}}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var failure = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync("orders", "items", "item-a"));

        await Assert.That(failure).IsNotNull();
        var exception = failure ?? throw new InvalidOperationException("The overload response did not produce a typed failure.");
        await Assert.That(exception.ErrorCode).IsEqualTo("overloaded");
        await Assert.That(exception.RetryAfter).IsNotNull();
        await Assert.That(exception.RetryAfter!.Value).IsGreaterThan(TimeSpan.FromSeconds(90));
        await Assert.That(exception.RetryAfter!.Value).IsLessThanOrEqualTo(TimeSpan.FromMinutes(2));
        await Assert.That(handler.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task MalformedErrorJsonRemainsTypedAndDoesNotInventFields()
    {
        using var handler = new ErrorHandler(_ => ErrorResponse("{not-json"));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var failure = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync("orders", "items", "item-a"));

        await Assert.That(failure).IsNotNull();
        var exception = failure ?? throw new InvalidOperationException("The malformed response did not produce a typed failure.");
        await Assert.That(exception.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(exception.ErrorCode).IsNull();
        await Assert.That(exception.Message).Contains("HTTP 429");
        await Assert.That(exception.ResponseBody).IsEqualTo("{not-json");
    }

    [Test]
    public async Task OversizedErrorBodyIsCapturedWithinTheDiagnosticLimit()
    {
        var body = new string('x', 70 * 1024);
        using var handler = new ErrorHandler(_ => ErrorResponse(body));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var failure = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync("orders", "items", "item-a"));

        await Assert.That(failure).IsNotNull();
        var captured = failure?.ResponseBody
            ?? throw new InvalidOperationException("The oversized response did not retain bounded diagnostics.");
        await Assert.That(captured).Contains("response body truncated after 65536 characters");
        await Assert.That(captured.Length).IsLessThan(66 * 1024);
    }

    [Test]
    public async Task ErrorBodyAtTheExactDiagnosticLimitIsNotReportedAsTruncated()
    {
        var body = new string('x', 64 * 1024);
        using var handler = new ErrorHandler(_ => ErrorResponse(body));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var failure = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            client.Documents.GetAsync("orders", "items", "item-a"));

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.ResponseBody).IsEqualTo(body);
    }

    [Test]
    public async Task CancellationDuringErrorBodyReadIsNotRelabeledAsAnApiFailure()
    {
        using var handler = new ErrorHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new CancellationContent()
        });
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.Documents.GetAsync("orders", "items", "item-a", cancellationToken: cancellation.Token));
        await Assert.That(handler.Attempts).IsEqualTo(1);
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) => new(
        httpClient,
        new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            BearerToken = "caller-token",
            MaxRetryAttempts = 0
        });

    private static HttpResponseMessage ErrorResponse(string body) => new(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class ErrorHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class CancellationContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CancellationStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CancellationStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}
