using System.Net;
using Grpc.Core;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Features.Replication;
using TerraScale.TerraBase.Features.Replication.V1;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Replication;

public sealed class PeerTransportTests
{
    private static readonly Metadata Lz4Request = new() { { "grpc-internal-encoding-request", Lz4CompressionProvider.Name } };

    [Test]
    public async Task Sync_decodes_lz4_requests_and_requires_Hello_first()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var channel = tls.CreateChannel(server.PeerAddress!);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);

        using var call = client.Sync(Lz4Request);
        await call.RequestStream.WriteAsync(new SyncRequest { KeepAlive = new KeepAlive { Nonce = 7 } });
        await call.RequestStream.CompleteAsync();
        var error = await Assert.That(async () => await call.ResponseStream.MoveNext(CancellationToken.None))
            .Throws<RpcException>();

        // An unregistered encoding fails with Unimplemented; InvalidArgument proves the frame was decoded.
        await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task Sync_accepts_Hello_and_reports_negotiation_as_unimplemented()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var channel = tls.CreateChannel(server.PeerAddress!);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);

        using var call = client.Sync(Lz4Request);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = new Hello() });
        var error = await Assert.That(async () => await call.ResponseStream.MoveNext(CancellationToken.None))
            .Throws<RpcException>();

        await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.Unimplemented);
        await Assert.That(error.Status.Detail).Contains("negotiation");
    }

    [Test]
    public async Task Peer_listener_rejects_clients_without_a_certificate()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var channel = tls.CreateChannel(server.PeerAddress!, presentClientCertificate: false);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);

        using var call = client.Sync();
        // The TLS handshake fails, so the call never reaches the service.
        await Assert.That(async () =>
        {
            await call.RequestStream.WriteAsync(new SyncRequest { Hello = new Hello() });
            await call.ResponseStream.MoveNext(CancellationToken.None);
        }).ThrowsException();
    }

    [Test]
    public async Task Listeners_stay_separate()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var peerHttp = new HttpClient(tls.CreateHandler())
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        using var adminOnPeer = await peerHttp.GetAsync(new Uri(server.PeerAddress!, "/healthz"));
        using var grpcOnAdmin = await server.Client.PostAsync(
            new Uri("/terrabase.v1.Replication/Sync", UriKind.Relative), new ByteArrayContent([]));

        await Assert.That(adminOnPeer.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(grpcOnAdmin.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("peer-port")]
    [Arguments("peer-client-ca")]
    public async Task Peer_settings_must_be_complete(string only)
    {
        var error = await Assert.That(() => TerraBaseOptions.FromValues(new Dictionary<string, string> { [only] = "0" }))
            .Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("peer-port");
    }

    [Test]
    public async Task Unreadable_peer_certificates_are_rejected_without_echoing_paths()
    {
        var settings = new Dictionary<string, string>
        {
            ["peer-port"] = "0",
            ["peer-certificate"] = "/nonexistent/secret-server.pem",
            ["peer-private-key"] = "/nonexistent/secret-server.key",
            ["peer-client-ca"] = "/nonexistent/secret-ca.pem",
        };

        var error = await Assert.That(() => TerraBaseOptions.FromValues(settings)).Throws<TerraBaseConfigurationException>();

        await Assert.That(error!.Message).Contains("peer-certificate");
        await Assert.That(error.Message).DoesNotContain("secret");
    }
}
