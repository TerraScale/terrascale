using System.Net;
using Grpc.Net.Client;

namespace TerraBase.Testing;

/// <summary>
/// Clients of a test server's internal listener: HTTP/2 without TLS, as members reach each other over Fly's private network.
/// It owns nothing but its own lifetime: use it only before it is disposed.
/// </summary>
public sealed class PeerTls : IDisposable
{
    private bool _disposed;

    /// <summary>Nothing to set: every server serves its internal listener.</summary>
    public void Apply(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>An HTTP/2 handler for the internal listener.</summary>
    public SocketsHttpHandler CreateHandler()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new() { AllowAutoRedirect = false, UseCookies = false };
    }

    /// <summary>A gRPC channel to the internal listener.</summary>
    public GrpcChannel CreateChannel(Uri peerAddress) =>
        GrpcChannel.ForAddress(peerAddress, new GrpcChannelOptions { HttpHandler = CreateHandler(), DisposeHttpClient = true });

    /// <summary>
    /// An HTTP client of the internal listener, which speaks HTTP/2 only: every request goes out as HTTP/2 with prior
    /// knowledge, including requests built by hand.
    /// </summary>
    public static HttpClient CreateClient(Uri internalAddress, HttpMessageHandler? handler = null) =>
        new(new Http2Only { InnerHandler = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false } })
        {
            BaseAddress = internalAddress,
            // As in ClientTls: the test timeout bounds a request.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private sealed class Http2Only : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}
