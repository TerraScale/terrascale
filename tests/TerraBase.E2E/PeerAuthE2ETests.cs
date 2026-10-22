using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.E2E;

/// <summary>
/// The peer credential across separate region processes: regions with the shared credential replicate, and a request on
/// the internal listener without it, or with another one, is refused before any endpoint runs, so it changes nothing.
/// </summary>
public sealed class PeerAuthE2ETests
{
    [Test]
    [Scenario(Nodes.Processes)]
    public async Task Peer_grpc_calls_without_the_credential_or_with_another_are_unauthenticated(CancellationToken cancellationToken)
    {
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc);
        foreach (var region in regions.Regions)
        {
            var address = regions[region].BaseAddress!;
            // A database nobody created: an authenticated Digest answers Held=false, so the refusals below come from the
            // credential, not from the request.
            var database = ForgedDatabase();
            using var peer = PeerChannel.Create(address, PeerTls.Credential);
            var reply = await new Replication.ReplicationClient(peer).DigestAsync(new DigestRequest { Database = database }, cancellationToken: cancellationToken);
            await Assert.That(reply.Held).IsFalse().Because($"{region} answers an authenticated Digest, so the refusals are the credential's");

            // No harness credential on this channel: each call carries only the metadata it is given.
            using var plain = PlainChannel(address);
            var client = new Replication.ReplicationClient(plain);
            foreach (var headers in (Metadata[])[[], Credential(PeerTls.NewSecret())])
            {
                await Assert.That(await StatusOfAsync(() => PushAsync(client, headers, database, cancellationToken))).IsEqualTo(StatusCode.Unauthenticated)
                    .Because($"{region} refuses Push");
                await Assert.That(await StatusOfAsync(() => DigestAsync(client, headers, database, cancellationToken))).IsEqualTo(StatusCode.Unauthenticated)
                    .Because($"{region} refuses Digest");
                await Assert.That(await StatusOfAsync(() => ReadBucketAsync(client, headers, database, cancellationToken))).IsEqualTo(StatusCode.Unauthenticated)
                    .Because($"{region} refuses ReadBucket");
            }
        }
    }

    [Test]
    [Scenario(Nodes.Processes)]
    public async Task Refused_forged_writes_change_nothing_and_replication_still_works(CancellationToken cancellationToken)
    {
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc);
        var canary = regions.Document(EntityIds.New().ToString());
        var canaryBody = new JsonObject { ["step"] = "before" };
        await Docs.PutAsync(regions[Cluster.Gru], canary, canaryBody, cancellationToken);
        _ = await Converge.UntilAsync(() => Docs.ReadAsync(regions[Cluster.Sjc], canary), document => document is not null, "the canary on sjc", cancellationToken);

        var forgedHttp = regions.Document(EntityIds.New().ToString());
        var forgedGrpc = ForgedDatabase();
        foreach (var region in regions.Regions)
        {
            foreach (var credential in new string?[] { null, PeerTls.NewSecret() })
            {
                using var forged = RawHttp(regions[region].BaseAddress!, credential);
                using var body = new StringContent("{\"forged\":true}", Encoding.UTF8, "application/json");
                using var response = await forged.PutAsync(forgedHttp, body, cancellationToken);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized).Because($"{region} refuses a forged PUT");
            }
        }

        using var sjcChannel = PlainChannel(regions[Cluster.Sjc].BaseAddress!);
        await Assert.That(await StatusOfAsync(() => PushAsync(new Replication.ReplicationClient(sjcChannel), [], forgedGrpc, cancellationToken)))
            .IsEqualTo(StatusCode.Unauthenticated).Because("sjc refuses a forged Push");

        // A legitimate write after the refusals: it replicates, so the refusals did not wedge the peer streams.
        var after = regions.Document(EntityIds.New().ToString());
        await Docs.PutAsync(regions[Cluster.Gru], after, new JsonObject { ["step"] = "after" }, cancellationToken);
        _ = await Converge.UntilAsync(() => Docs.ReadAsync(regions[Cluster.Sjc], after), document => document?["step"]?.GetValue<string>() == "after", "the write after the refusals on sjc", cancellationToken);

        var reverse = regions.Document(EntityIds.New().ToString());
        await Docs.PutAsync(regions[Cluster.Sjc], reverse, new JsonObject { ["step"] = "reverse" }, cancellationToken);
        _ = await Converge.UntilAsync(() => Docs.ReadAsync(regions[Cluster.Gru], reverse), document => document?["step"]?.GetValue<string>() == "reverse", "the reverse write on gru", cancellationToken);

        foreach (var region in regions.Regions)
        {
            await Assert.That(await Docs.StatusAsync(regions[region], forgedHttp, cancellationToken)).IsEqualTo(HttpStatusCode.NotFound)
                .Because($"{region} holds no forged PUT");
            var copy = await Docs.ReadAsync(regions[region], canary, cancellationToken);
            await Assert.That(Docs.Canonical(copy)).IsEqualTo(Docs.Canonical(canaryBody)).Because($"{region} keeps the canary unchanged");
        }
    }

    private static Database ForgedDatabase() => new()
    {
        OrganizationId = EntityIds.New().ToString(),
        ProjectId = EntityIds.New().ToString(),
        DatabaseId = EntityIds.New().ToString(),
    };

    private static Metadata Credential(string value) => new() { { PeerCredential.HeaderName.ToLowerInvariant(), value } };

    // A raw client of the internal listener: HTTP/2 with prior knowledge, and the given credential when there is one.
    private static HttpClient RawHttp(Uri address, string? credential = null)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (credential is not null)
        {
            client.DefaultRequestHeaders.Add(PeerCredential.HeaderName, credential);
        }

        return client;
    }

    // A gRPC channel that sends no credential of the harness's own: the call's metadata is the only one.
    private static GrpcChannel PlainChannel(Uri address) => GrpcChannel.ForAddress(address, new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false },
        DisposeHttpClient = true,
    });

    // The status an RPC ends with, or OK when it succeeds.
    private static async Task<StatusCode> StatusOfAsync(Func<Task> call)
    {
        try
        {
            await call();
            return StatusCode.OK;
        }
        catch (RpcException error)
        {
            return error.StatusCode;
        }
    }

    // The first frame, then the batch: a peer's stream opened without the credential. The server refuses before it reads them.
    private static async Task PushAsync(Replication.ReplicationClient client, Metadata headers, Database database, CancellationToken cancellationToken)
    {
        using var call = client.Push(new CallOptions(headers: headers, cancellationToken: cancellationToken));
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = 1, Hlc = 1, Region = Cluster.Gru, Machine = "forged" } }, cancellationToken);
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = database, LastChangeId = 1, Groups = ByteString.CopyFromUtf8("forged") } }, cancellationToken);
        await call.RequestStream.CompleteAsync();
        _ = await call.ResponseStream.MoveNext(cancellationToken);
    }

    private static async Task DigestAsync(Replication.ReplicationClient client, Metadata headers, Database database, CancellationToken cancellationToken) =>
        _ = await client.DigestAsync(new DigestRequest { Database = database }, new CallOptions(headers: headers, cancellationToken: cancellationToken));

    private static async Task ReadBucketAsync(Replication.ReplicationClient client, Metadata headers, Database database, CancellationToken cancellationToken)
    {
        using var call = client.ReadBucket(new BucketRequest { Database = database, Buckets = { 0u } }, new CallOptions(headers: headers, cancellationToken: cancellationToken));
        _ = await call.ResponseStream.MoveNext(cancellationToken);
    }

}
