using System.Net;
using System.Security.Cryptography;
using Grpc.Net.Client;
using TerraBase.Features.Replication;

namespace TerraBase.Testing;

/// <summary>
/// Clients of a test server's internal listener: HTTP/2 without TLS, as members reach each other over Fly's private network.
/// Every request carries a peer credential, as production members do. It owns nothing but its own lifetime: use it only before it is disposed.
/// </summary>
public sealed class PeerTls : IDisposable
{
    // One secret for the whole test process: every server in a test must accept what its peers send. Immutable, so parallel tests share it.
    internal const string TestSecretVariable = "TERRABASE_TEST_PEER_SECRET";
    internal static string Secret { get; } = Environment.GetEnvironmentVariable(TestSecretVariable) ?? NewSecret();
    internal static PeerCredential Credential { get; } = new(Secret);

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

    /// <summary>A gRPC channel to the internal listener, sending <paramref name="credential"/> (default: the process credential).</summary>
    internal GrpcChannel CreateChannel(Uri peerAddress, PeerCredential? credential = null) =>
        GrpcChannel.ForAddress(peerAddress, new GrpcChannelOptions
        {
            HttpHandler = new PeerRequests(credential ?? Credential) { InnerHandler = CreateHandler() },
            DisposeHttpClient = true,
        });

    /// <summary>
    /// An HTTP client of the internal listener, which speaks HTTP/2 only: every request goes out as HTTP/2 with prior
    /// knowledge, including requests built by hand, and carries <paramref name="credential"/> (default: the process credential).
    /// </summary>
    internal static HttpClient CreateClient(Uri internalAddress, HttpMessageHandler? handler = null, PeerCredential? credential = null) =>
        new(new PeerRequests(credential ?? Credential) { InnerHandler = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false } })
        {
            BaseAddress = internalAddress,
            // As in ClientTls: the test timeout bounds a request.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// <summary>A new test peer secret: 32 random bytes as 64 lowercase hex digits.</summary>
    internal static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private sealed class PeerRequests(PeerCredential credential) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            credential.Authenticate(request);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}
