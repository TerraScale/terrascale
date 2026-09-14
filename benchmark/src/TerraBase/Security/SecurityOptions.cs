using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace TerraBase.Security;

/// <summary>Bounds used by the security contracts.</summary>
public static class SecurityLimits
{
    /// <summary>The longest customer-operation authorization lease.</summary>
    public static readonly TimeSpan MaximumAuthorizationLease = TimeSpan.FromSeconds(30);

    /// <summary>The longest data-key authorization lease.</summary>
    public static readonly TimeSpan MaximumKeyLease = TimeSpan.FromSeconds(1_800);
}

/// <summary>Authenticated HTTPS settings for an outbound TerraBase connection.</summary>
public sealed record AuthenticatedTlsOptions
{
    /// <summary>Creates settings for an HTTPS endpoint.</summary>
    /// <param name="endpoint">The HTTPS endpoint to connect to.</param>
    /// <param name="clientCertificate">The optional client certificate for mTLS.</param>
    /// <param name="requireClientCertificate">Whether a client certificate is required.</param>
    public AuthenticatedTlsOptions(
        Uri endpoint,
        X509Certificate2? clientCertificate = null,
        bool requireClientCertificate = false)
    {
        if (endpoint is null
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ArgumentException("Authenticated TLS requires an HTTPS endpoint.", nameof(endpoint));
        }

        if (requireClientCertificate && clientCertificate is null)
        {
            throw new ArgumentException(
                "A client certificate is required when mutual TLS is enabled.",
                nameof(clientCertificate));
        }

        Endpoint = endpoint;
        ClientCertificate = clientCertificate;
        RequireClientCertificate = requireClientCertificate;
    }

    /// <summary>The endpoint secured by this configuration.</summary>
    public Uri Endpoint { get; }

    /// <summary>The client certificate sent during mutual TLS, when configured.</summary>
    public X509Certificate2? ClientCertificate { get; }

    /// <summary>Whether the server must authenticate a client certificate.</summary>
    public bool RequireClientCertificate { get; }

    /// <summary>Whether the operating system certificate revocation list is checked.</summary>
    public bool CheckCertificateRevocationList { get; init; } = true;

    /// <summary>The TLS protocol versions allowed for the connection.</summary>
    public SslProtocols EnabledProtocols { get; init; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>Creates an HTTP handler that uses the authenticated TLS settings.</summary>
    public SocketsHttpHandler CreateHandler()
    {
        if (EnabledProtocols == SslProtocols.None
            || (EnabledProtocols & ~(SslProtocols.Tls12 | SslProtocols.Tls13)) != 0)
        {
            throw new InvalidOperationException("Authenticated TLS must enable TLS 1.2 or TLS 1.3 only.");
        }

        var clientCertificates = ClientCertificate is null
            ? null
            : new X509CertificateCollection { ClientCertificate };

        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = EnabledProtocols,
                CertificateRevocationCheckMode = CheckCertificateRevocationList
                    ? X509RevocationMode.Online
                    : X509RevocationMode.NoCheck,
                ClientCertificates = clientCertificates,
                RemoteCertificateValidationCallback = ValidateServerCertificate,
            },
        };
    }

    /// <summary>Creates an HTTP client using the authenticated TLS settings.</summary>
    public HttpClient CreateClient() => new(CreateHandler(), disposeHandler: true)
    {
        BaseAddress = Endpoint,
    };

    private static bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors) =>
        certificate is not null && sslPolicyErrors == SslPolicyErrors.None;
}

