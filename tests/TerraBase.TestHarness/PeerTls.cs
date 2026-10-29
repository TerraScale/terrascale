using System.Net;
using System.Security.Cryptography;
using TerraBase.Hosting;

namespace TerraBase.Testing;

public sealed class PeerTls : IDisposable
{
    internal const string TestSecretVariable = "TERRABASE_TEST_PEER_SECRET";
    internal static string Secret { get; } = Environment.GetEnvironmentVariable(TestSecretVariable) ?? NewSecret();
    internal static InternalCredential Credential { get; } = new(Secret);

    private bool _disposed;
    public void Apply(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public SocketsHttpHandler CreateHandler()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new() { AllowAutoRedirect = false, UseCookies = false };
    }

    internal static HttpClient CreateClient(Uri internalAddress, HttpMessageHandler? handler = null, InternalCredential? credential = null) =>
        new(new PeerRequests(credential ?? Credential) { InnerHandler = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false } })
        {
            BaseAddress = internalAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    internal static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private sealed class PeerRequests(InternalCredential credential) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            credential.Authenticate(request);
            return base.SendAsync(request, cancellationToken);
        }
    }

    public void Dispose() => _disposed = true;
}
