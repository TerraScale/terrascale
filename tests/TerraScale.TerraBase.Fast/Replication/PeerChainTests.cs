using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class PeerChainTests
{
    [Test]
    public async Task Distinct_intermediate_chains_replicate_and_rotate_with_only_the_root_trusted()
    {
        using var pki = new ChainPki();
        var a = pki.Issue("a");
        var b = pki.Issue("b");
        var rotated = pki.Issue("rotated");
        using var tls = new ClientTls();
        var clock = new PeerCredentialClock();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings => { b.Apply(settings); tls.Apply(settings); });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            a.Apply(settings);
            tls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        }, services => services.AddSingleton(provider => new PeerCredentials(
            provider.GetRequiredService<TerraBaseOptions>(), provider.GetRequiredService<ILogger<PeerCredentials>>(), clock)));
        var tenant = await ReplicationTenant.CreateAsync(sender, "db");
        await tenant.BindAsync(receiver);
        using var writer = tls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        using var reader = tls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
        using var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" });
        collection.EnsureSuccessStatusCode();
        var path = $"{tenant.Path("db")}/collections/items/documents/{ReplicationDocuments.Id("chain")}";
        using var before = await writer.PutAsJsonAsync(new Uri(path, UriKind.Relative), new { value = "before" });
        before.EnsureSuccessStatusCode();
        await TerraBaseTestServer.ReplicatedAsync(sender);

        var credentials = sender.Services.GetRequiredService<PeerCredentials>();
        using var held = credentials.Capture();
        await Assert.That(held.CertificateContext.IntermediateCertificates.Single().Thumbprint).IsEqualTo(a.Intermediate.Thumbprint);
        var oldLeaf = held.Certificate;
        var oldChain = held.CertificateContext.IntermediateCertificates.ToArray();

        // The new chain with the old key is incomplete; a fresh inbound connection still uses the old pair.
        File.Copy(rotated.CertificatePath, a.CertificatePath, overwrite: true);
        clock.Advance();
        await WelcomeAsync(a, sender.PeerAddress!, tenant);
        using (var retained = credentials.Capture())
        {
            await Assert.That(retained.Certificate.Thumbprint).IsEqualTo(a.Certificate.Thumbprint);
        }

        File.Copy(rotated.KeyPath, a.KeyPath, overwrite: true);
        clock.Advance();
        using var after = await writer.PutAsJsonAsync(new Uri(path, UriKind.Relative), new { value = "after" });
        after.EnsureSuccessStatusCode();
        await TerraBaseTestServer.ReplicatedAsync(sender);
        using var document = await reader.GetAsync(new Uri(path, UriKind.Relative));
        document.EnsureSuccessStatusCode();
        await Assert.That(await document.Content.ReadAsStringAsync()).Contains("after");
        using (var current = credentials.Capture())
        {
            await Assert.That(current.Certificate.Thumbprint).IsEqualTo(rotated.Certificate.Thumbprint);
            await Assert.That(current.CertificateContext.IntermediateCertificates.Single().Thumbprint).IsEqualTo(rotated.Intermediate.Thumbprint);
        }

        await WelcomeAsync(rotated, sender.PeerAddress!, tenant);
        await Assert.That(oldLeaf.Handle).IsNotEqualTo(IntPtr.Zero);
        held.Dispose();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(oldLeaf.Handle == IntPtr.Zero && oldChain.All(cert => cert.Handle == IntPtr.Zero)));
    }

    private static async Task WelcomeAsync(Node node, Uri address, ReplicationTenant tenant)
    {
        using var channel = node.CreateChannel(address);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(tenant.Database("db"), RandomNumberGenerator.GetBytes(16)) });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
        await call.RequestStream.CompleteAsync();
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsFalse();
    }

    private sealed class ChainPki : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-chain-").FullName;
        private readonly X509Certificate2 _root;
        private readonly List<Node> _nodes = [];

        public ChainPki()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _root = Request("root", key, ca: true).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(Path.Combine(_directory, "root.pem"), _root.ExportCertificatePem());
        }

        public Node Issue(string name)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(_nodes.Count, 3);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var issued = Request(name, key, ca: true).Create(_root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
            var intermediate = issued.CopyWithPrivateKey(key);
            using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var leaf = Request("localhost", leafKey, ca: false).Create(intermediate, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(6), RandomNumberGenerator.GetBytes(16));
            var certificate = leaf.CopyWithPrivateKey(leafKey);
            var node = new Node(_directory, name, _root, certificate, intermediate);
            File.WriteAllText(node.CertificatePath, certificate.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem());
            File.WriteAllText(node.KeyPath, leafKey.ExportPkcs8PrivateKeyPem());
            _nodes.Add(node);
            return node;
        }

        private static CertificateRequest Request(string name, ECDsa key, bool ca)
        {
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
            if (!ca)
            {
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(PeerTlsFiles.ServerAuthentication), new Oid(PeerTlsFiles.ClientAuthentication)], false));
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                request.CertificateExtensions.Add(names.Build());
            }

            return request;
        }

        public void Dispose()
        {
            _nodes.ForEach(node => node.Dispose());
            _root.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Node(string directory, string name, X509Certificate2 root, X509Certificate2 certificate, X509Certificate2 intermediate) : IDisposable
    {
        private readonly SslStreamCertificateContext _context = SslStreamCertificateContext.Create(certificate, [intermediate, root], offline: true);

        public X509Certificate2 Certificate => certificate;
        public X509Certificate2 Intermediate => intermediate;
        public string CertificatePath => Path.Combine(directory, name + ".pem");
        public string KeyPath => Path.Combine(directory, name + ".key");

        public void Apply(Dictionary<string, string> settings)
        {
            settings["peer-port"] = "0";
            settings["peer-certificate"] = CertificatePath;
            settings["peer-private-key"] = KeyPath;
            settings["peer-client-ca"] = Path.Combine(directory, "root.pem");
        }

        public GrpcChannel CreateChannel(Uri address) => GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            DisposeHttpClient = true,
            HttpHandler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificateContext = _context,
                    CertificateChainPolicy = PeerTlsFiles.Policy([root], PeerTlsFiles.ServerAuthentication),
                    RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                        (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                        && cert is X509Certificate2 leaf && PeerTlsFiles.Chains(leaf, [root], PeerTlsFiles.ServerAuthentication,
                            [.. chain!.ChainElements.Cast<X509ChainElement>().Skip(1).Select(element => element.Certificate)]),
                },
            },
        });

        public void Dispose()
        {
            certificate.Dispose();
            intermediate.Dispose();
            foreach (var cert in _context.IntermediateCertificates)
            {
                cert.Dispose();
            }
        }
    }
}
