using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using TerraScale.TerraBase.Features.Replication;

namespace TerraScale.TerraBase.Testing;

/// <summary>
/// A throwaway peer PKI for one test: a CA, a server certificate for localhost and a client
/// certificate, written as PEM files to a temporary directory that is deleted on dispose.
/// </summary>
public sealed class PeerTls : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-peer-tls-").FullName;
    private readonly X509Certificate2 _authority;

    /// <summary>Creates the CA and both leaf certificates.</summary>
    public PeerTls()
    {
        using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityRequest = new CertificateRequest("CN=TerraBase test peer CA", authorityKey, HashAlgorithmName.SHA256);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        _authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        var (serverCertificate, serverKey) = Issue("CN=localhost", "1.3.6.1.5.5.7.3.1", names.Build());
        ClientCertificate = IssueWithKey("CN=terrabase-test-peer", "1.3.6.1.5.5.7.3.2");

        File.WriteAllText(CaPath, _authority.ExportCertificatePem());
        File.WriteAllText(CertificatePath, serverCertificate.ExportCertificatePem());
        File.WriteAllText(PrivateKeyPath, serverKey.ExportPkcs8PrivateKeyPem());
        serverCertificate.Dispose();
        serverKey.Dispose();
    }

    /// <summary>A client certificate, with its private key, issued by the test CA.</summary>
    public X509Certificate2 ClientCertificate { get; }

    private string CaPath => Path.Combine(_directory, "ca.pem");

    private string CertificatePath => Path.Combine(_directory, "server.pem");

    private string PrivateKeyPath => Path.Combine(_directory, "server.key");

    /// <summary>Enables the peer listener on an ephemeral port with these certificates.</summary>
    public void Apply(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["peer-port"] = "0";
        settings["peer-certificate"] = CertificatePath;
        settings["peer-private-key"] = PrivateKeyPath;
        settings["peer-client-ca"] = CaPath;
    }

    /// <summary>An HTTP/2 handler that trusts only the test CA and presents the client certificate when asked.</summary>
    public SocketsHttpHandler CreateHandler(bool presentClientCertificate = true)
    {
        var ssl = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 server && ChainsToAuthority(server),
        };
        if (presentClientCertificate)
        {
            ssl.ClientCertificates = [ClientCertificate];
        }

        return new SocketsHttpHandler { SslOptions = ssl };
    }

    /// <summary>A gRPC channel to the peer listener that speaks the <c>lz4</c> encoding.</summary>
    public GrpcChannel CreateChannel(Uri peerAddress, bool presentClientCertificate = true) =>
        GrpcChannel.ForAddress(peerAddress, new GrpcChannelOptions
        {
            HttpHandler = CreateHandler(presentClientCertificate),
            CompressionProviders = [new Lz4CompressionProvider(ReplicationExtensions.MaxMessageBytes)],
        });

    /// <inheritdoc />
    public void Dispose()
    {
        ClientCertificate.Dispose();
        _authority.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private bool ChainsToAuthority(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    private (X509Certificate2 Certificate, ECDsa Key) Issue(string subject, string usage, X509Extension? names = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        if (names is not null)
        {
            request.CertificateExtensions.Add(names);
        }

        var serial = RandomNumberGenerator.GetBytes(16);
        var certificate = request.Create(_authority, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), serial);
        return (certificate, key);
    }

    private X509Certificate2 IssueWithKey(string subject, string usage)
    {
        var (certificate, key) = Issue(subject, usage);
        using (certificate)
        using (key)
        {
            return certificate.CopyWithPrivateKey(key);
        }
    }
}
