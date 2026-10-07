using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Features.Replication;
using TerraScale.TerraBase.Features.Replication.V1;
using TerraScale.TerraBase.Tenancy;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Replication;

public sealed class StartupCredentialsTests
{
    [Test]
    public async Task A_peer_certificate_bundle_loads_at_listener_startup()
    {
        using var peer = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            peer.Apply(settings);
            File.AppendAllText(settings["peer-certificate"], "\n" + File.ReadAllText(settings["peer-client-ca"]));
        });
        var database = await server.RegisterAsync(new TenantContext(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
        await WelcomeAsync(peer, server.PeerAddress!, ReplicationProtocol.CreateHello(database, RandomNumberGenerator.GetBytes(16)));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Unconsumed_startup_certificates_are_released_when_host_build_or_directory_validation_fails(bool failBuild)
    {
        using var peer = new PeerTls();
        using var client = new ClientTls();
        var directory = Directory.CreateTempSubdirectory("terrabase-refused-credentials-").FullName;
        TerraBaseOptions? options = null;
        X509Certificate2? leaf = null;
        try
        {
            await DocumentFiles.WriteAsync(Path.Combine(directory, "shop.db"), ("items", "a", "{}"));
            await Assert.That(async () =>
            {
                await using var refused = await TerraBaseTestServer.StartAsync(settings =>
                {
                    peer.Apply(settings);
                    client.Apply(settings);
                    settings["data-directory"] = directory;
                }, services =>
                {
                    options = (TerraBaseOptions)services.Single(service => service.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
                    leaf = options.PeerCredentialSnapshot!.Certificate;
                    if (failBuild)
                    {
                        throw new InvalidOperationException("simulated host build failure");
                    }
                });
            }).Throws<InvalidOperationException>();

            await Assert.That(options).IsNotNull();
            await Assert.That(options!.PeerCredentialSnapshot).IsNull();
            await Assert.That(leaf!.Handle).IsEqualTo(IntPtr.Zero);
            await Assert.That(options.ClientCertificate!.Handle).IsEqualTo(IntPtr.Zero);
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("peer-certificate")]
    [Arguments("peer-private-key")]
    [Arguments("peer-client-ca")]
    [Arguments("mismatched-authority")]
    [Arguments("mismatched-key")]
    [Arguments("certificate-bundle")]
    public async Task Validated_credentials_survive_an_incomplete_rotation_before_listener_startup(string incompleteRotation)
    {
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        var clock = new PeerCredentialClock();
        await using var server = await TerraBaseTestServer.StartAsync(original.Apply, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            // FromValues has returned, but the service provider and peer credentials do not exist yet.
            var options = (TerraBaseOptions)services.Single(service => service.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
            var files = options.PeerTls!;
            switch (incompleteRotation)
            {
                case "peer-certificate":
                    File.WriteAllText(files.Certificate, "incomplete PEM certificate");
                    break;
                case "peer-private-key":
                    File.WriteAllText(files.PrivateKey, "incomplete PEM private key");
                    break;
                case "peer-client-ca":
                    File.WriteAllText(files.Authority, "incomplete PEM authority");
                    break;
                case "mismatched-authority":
                    original.OverwriteAuthorityWith(rotated);
                    break;
                case "mismatched-key":
                    using (var certificate = rotated.LoadNodeCertificate())
                    using (var key = certificate.GetECDsaPrivateKey()!)
                    {
                        File.WriteAllText(files.PrivateKey, key.ExportPkcs8PrivateKeyPem());
                    }

                    break;
                case "certificate-bundle":
                    File.AppendAllText(files.Certificate, "\n" + File.ReadAllText(files.Authority));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(incompleteRotation));
            }
        });
        var fake = new Faker();
        var database = await server.RegisterAsync(new TenantContext(fake.Random.Guid().ToString("D"), PlanTier.Hobby, fake.Random.Guid().ToString("D"), fake.Random.Guid().ToString("D")));
        var hello = ReplicationProtocol.CreateHello(database, RandomNumberGenerator.GetBytes(16));

        // The files remain incomplete through startup and retries on two fresh TLS connections.
        clock.Advance();
        await WelcomeAsync(original, server.PeerAddress!, hello);
        clock.Advance();
        await WelcomeAsync(original, server.PeerAddress!, hello);

        original.OverwriteWith(rotated, preserveTimestamps: true);
        clock.Advance();
        await WelcomeAsync(rotated, server.PeerAddress!, hello);
    }

    private static async Task WelcomeAsync(PeerTls tls, Uri address, Hello hello)
    {
        using var channel = tls.CreateChannel(address);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(deadline: DateTime.UtcNow.AddSeconds(30));
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
        await call.RequestStream.CompleteAsync();
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsFalse();
    }
}
