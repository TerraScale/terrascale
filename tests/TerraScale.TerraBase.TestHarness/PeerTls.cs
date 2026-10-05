using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;

namespace TerraScale.TerraBase.Testing;

/// <summary>
/// A throwaway peer PKI for one test: a CA, a node certificate for localhost (served by the peer
/// listener and presented by the node's sender, so it carries both TLS usages) and a client
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
        var (serverCertificate, serverKey) = Issue("CN=localhost", ["1.3.6.1.5.5.7.3.1", "1.3.6.1.5.5.7.3.2"], names.Build());
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

    /// <summary>
    /// An HTTP/2 handler that trusts only the test CA and presents <paramref name="clientCertificate"/>
    /// (by default this PKI's client certificate) when asked.
    /// </summary>
    public SocketsHttpHandler CreateHandler(bool presentClientCertificate = true, X509Certificate2? clientCertificate = null)
    {
        var ssl = new SslClientAuthenticationOptions
        {
            // Replace only trust: a name mismatch or a missing certificate still fails.
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                && certificate is X509Certificate2 server && ChainsToAuthority(server),
        };
        if (presentClientCertificate)
        {
            ssl.ClientCertificates = [clientCertificate ?? ClientCertificate];
        }

        return new SocketsHttpHandler { SslOptions = ssl };
    }

    /// <summary>A gRPC channel to the peer listener that trusts this PKI's CA.</summary>
    public GrpcChannel CreateChannel(Uri peerAddress, bool presentClientCertificate = true, X509Certificate2? clientCertificate = null) =>
        GrpcChannel.ForAddress(peerAddress, new GrpcChannelOptions { HttpHandler = CreateHandler(presentClientCertificate, clientCertificate), DisposeHttpClient = true });

    /// <summary>Rotates the files of a node configured with this PKI to those of <paramref name="replacement"/>.</summary>
    public void OverwriteWith(PeerTls replacement, bool preserveTimestamps = false)
    {
        var paths = new[] { CaPath, CertificatePath, PrivateKeyPath };
        var timestamps = paths.Select(File.GetLastWriteTimeUtc).ToArray();
        OverwriteAuthorityWith(replacement);
        OverwriteCertificateWith(replacement);
        if (preserveTimestamps)
        {
            for (var i = 0; i < paths.Length; i++)
            {
                File.SetLastWriteTimeUtc(paths[i], timestamps[i]);
            }
        }
    }

    /// <summary>Replaces only the CA file, as the first step of a rotation.</summary>
    public void OverwriteAuthorityWith(PeerTls replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        File.WriteAllBytes(CaPath, File.ReadAllBytes(replacement.CaPath));
    }

    /// <summary>Replaces only the node certificate and its key.</summary>
    public void OverwriteCertificateWith(PeerTls replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        File.WriteAllBytes(CertificatePath, File.ReadAllBytes(replacement.CertificatePath));
        File.WriteAllBytes(PrivateKeyPath, File.ReadAllBytes(replacement.PrivateKeyPath));
    }

    /// <summary>Deletes the node's certificate files, as an unreadable rotation would leave them.</summary>
    public void DeleteNodeFiles()
    {
        File.Delete(CaPath);
        File.Delete(CertificatePath);
        File.Delete(PrivateKeyPath);
    }

    /// <summary>The node certificate with its private key, for a test server that impersonates a node.</summary>
    public X509Certificate2 LoadNodeCertificate() => X509Certificate2.CreateFromPemFile(CertificatePath, PrivateKeyPath);

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
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // TLS server authentication
        return chain.Build(certificate);
    }

    private (X509Certificate2 Certificate, ECDsa Key) Issue(string subject, string[] usages, X509Extension? names = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var oids = new OidCollection();
        foreach (var usage in usages)
        {
            oids.Add(new Oid(usage));
        }

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
        if (names is not null)
        {
            request.CertificateExtensions.Add(names);
        }

        var serial = RandomNumberGenerator.GetBytes(16);
        // Leaves reuse the authority's validity: a leaf may never outlive its issuer.
        var certificate = request.Create(_authority, _authority.NotBefore, _authority.NotAfter, serial);
        return (certificate, key);
    }

    private X509Certificate2 IssueWithKey(string subject, string usage)
    {
        var (certificate, key) = Issue(subject, [usage]);
        using (certificate)
        using (key)
        {
            return certificate.CopyWithPrivateKey(key);
        }
    }
}
