using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TerraBase.Testing;

namespace TerraBase.Fast.Cluster;

// TS-633: ts-orchestrator gives each member a client certificate issued by an intermediate (its host issuer), in one file
// with that intermediate, and a CA file with only the root; its broker bridge is TLS first and verifies the client. A
// member that sent the bare leaf was dropped right after the handshake on gru-1, so no member ever held its lease.
public sealed class NatsTlsTests
{
    [Test]
    public async Task A_member_presents_its_whole_client_certificate_chain_to_nats()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-nats-tls-").FullName;
        var now = DateTimeOffset.UtcNow;
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var root = Authority("CN=test controller CA", rootKey).CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));
        using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var issuerPublic = Authority("CN=test host issuer", issuerKey).Create(root, now.AddMinutes(-5), now.AddHours(1), [1]);
        using var issuer = issuerPublic.CopyWithPrivateKey(issuerKey);
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = Leaf("CN=terrabase.test-member", leafKey, "1.3.6.1.5.5.7.3.2").Create(issuer, now.AddMinutes(-5), now.AddHours(1), [2]);
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var serverPublic = Leaf("CN=nats.test", serverKey, "1.3.6.1.5.5.7.3.1", IPAddress.Loopback).Create(root, now.AddMinutes(-5), now.AddHours(1), [3]);
        using var server = serverPublic.CopyWithPrivateKey(serverKey);

        await File.WriteAllTextAsync(Path.Combine(directory, "nats.pem"), leaf.ExportCertificatePem() + "\n" + issuer.ExportCertificatePem() + "\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "nats.key"), leafKey.ExportPkcs8PrivateKeyPem());
        await File.WriteAllTextAsync(Path.Combine(directory, "nats-ca.pem"), root.ExportCertificatePem());
        await File.WriteAllTextAsync(Path.Combine(directory, "nats.creds"),
            "-----BEGIN NATS USER JWT-----\nunused\n------END NATS USER JWT------\n\n-----BEGIN USER NKEY SEED-----\nunused\n------END USER NKEY SEED------\n");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var verified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = ServeAsync(listener, server, root, verified, stop.Token);
        try
        {
            await using (await TerraBaseTestServer.StartAsync(settings =>
            {
                settings["nats-url"] = $"tls://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
                settings["nats-credentials-file"] = Path.Combine(directory, "nats.creds");
                settings["nats-certificate"] = Path.Combine(directory, "nats.pem");
                settings["nats-private-key"] = Path.Combine(directory, "nats.key");
                settings["nats-ca"] = Path.Combine(directory, "nats-ca.pem");
            }))
            {
                // The server trusts only the root: the handshake verifies only when the member sent the intermediate too.
                await Assert.That(await verified.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo("CN=terrabase.test-member");
            }
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            Directory.Delete(directory, recursive: true);
        }
    }

    // A TLS-first server like ts-orchestrator's broker bridge: mutual TLS before any NATS byte, client chain checked
    // against the root alone. It records the first client it verified and hangs up on every connection.
    private static async Task ServeAsync(TcpListener listener, X509Certificate2 certificate, X509Certificate2 root, TaskCompletionSource<string> verified,
        CancellationToken cancellationToken)
    {
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        policy.CustomTrustStore.Add(root);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                await using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = true,
                    CertificateChainPolicy = policy,
                    RemoteCertificateValidationCallback = (_, _, _, errors) => errors == SslPolicyErrors.None,
                }, cancellationToken);
                verified.TrySetResult(tls.RemoteCertificate!.Subject);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error) when (error is AuthenticationException or IOException)
            {
                // Refused: the member retries at its next heartbeat.
            }
        }
    }

    private static CertificateRequest Authority(string name, ECDsa key)
    {
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request;
    }

    private static CertificateRequest Leaf(string name, ECDsa key, string usage, IPAddress? address = null)
    {
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        if (address is not null)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(address);
            request.CertificateExtensions.Add(names.Build());
        }

        return request;
    }
}
