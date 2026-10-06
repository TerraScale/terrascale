using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Bogus;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Replication;

/// <summary>Write-wait (docs/decisions/0010): a member answers a client write once its active peers durably acknowledged it.</summary>
public sealed class WriteWaitTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_write_answered_by_one_member_is_read_on_the_other_without_a_barrier()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        // A generous bound: the test fails on a read that misses a write, not on a slow machine.
        var (a, b) = await TerraBaseTestServer.StartPairAsync((settings, port, peerPort) =>
        {
            Configure(settings, peerTls, clientTls, $"https://localhost:{peerPort}");
            settings["peer-port"] = port.ToString(CultureInfo.InvariantCulture);
            settings["write-wait-ms"] = "30000";
        });
        await using var disposeA = a;
        await using var disposeB = b;
        var tenant = await ReplicationTenant.CreateAsync(a, "shop");
        await tenant.BindAsync(b);
        using var clientA = clientTls.CreateClient(a.ClientAddress!, await tenant.KeyAsync(a, "documents:read", "documents:write"));
        using var clientB = clientTls.CreateClient(b.ClientAddress!, await tenant.KeyAsync(b, "documents:read", "documents:write"));

        // ts-auth's readiness probe behind a round-robin balancer, back to back: every step lands on the other member.
        var collection = Faker.Random.String2(12, "abcdefghijklmnopqrstuvwxyz");
        using (var created = await clientA.PostAsJsonAsync(new Uri($"{tenant.Path("shop")}/collections", UriKind.Relative), new { name = collection }, token))
        {
            created.EnsureSuccessStatusCode();
        }

        for (var round = 0; round < 10; round++)
        {
            var (writer, reader) = round % 2 == 0 ? (clientB, clientA) : (clientA, clientB);
            var document = new Uri($"{tenant.Path("shop")}/collections/{collection}/documents/{Guid.NewGuid():D}", UriKind.Relative);
            using var put = new HttpRequestMessage(HttpMethod.Put, document) { Content = JsonContent.Create(new { value = Faker.Lorem.Sentence() }) };
            put.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            using var written = await writer.SendAsync(put, token);
            await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);

            using var read = await reader.GetAsync(document, token);
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(read.Headers.ETag).IsEqualTo(written.Headers.ETag);

            using var delete = new HttpRequestMessage(HttpMethod.Delete, document);
            delete.Headers.IfMatch.Add(read.Headers.ETag!);
            using var deleted = await writer.SendAsync(delete, token);
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.OK);

            using var gone = await reader.GetAsync(document, token);
            await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await Assert.That(await a.MetricAsync("terrabase_replication_write_wait_seconds_count", "result=\"acknowledged\"", token)).IsGreaterThan(0);
        await Assert.That(() => a.MetricAsync("terrabase_replication_write_wait_seconds_count", "result=\"timeout\"", token))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_peer_that_never_acknowledges_delays_a_write_only_to_the_bound()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await using var peer = await FakePeer.StartAsync(peerTls, async (requests, responses, context) =>
        {
            // Welcomes every stream and reads its groups, but acknowledges none.
            while (await requests.MoveNext(context.CancellationToken))
            {
                if (requests.Current.Hello is { } hello)
                {
                    await responses.WriteAsync(FakePeer.Welcome(hello, out _), context.CancellationToken);
                }
            }
        });
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            Configure(settings, peerTls, clientTls, peer.Address.ToString());
            settings["write-wait-ms"] = "200";
        });
        var tenant = await ReplicationTenant.CreateAsync(server, "shop");
        using var client = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:read", "documents:write"));
        using (var created = await client.PostAsJsonAsync(new Uri($"{tenant.Path("shop")}/collections", UriKind.Relative), new { name = "people" }, token))
        {
            created.EnsureSuccessStatusCode();
        }

        // Committed, so it answers success after the bound rather than an error a client would retry.
        using var written = await client.PutAsJsonAsync(
            new Uri($"{tenant.Path("shop")}/collections/people/documents/{Guid.NewGuid():D}", UriKind.Relative), new { name = Faker.Name.FullName() }, token);
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await server.MetricAsync("terrabase_replication_write_wait_seconds_count", "result=\"timeout\"", token)).IsEqualTo(2);
        await Assert.That(await server.MetricAsync("terrabase_replication_write_wait_seconds_sum", "result=\"timeout\"", token)).IsGreaterThanOrEqualTo(0.4);
    }

    [Test]
    public async Task A_write_does_not_wait_for_a_peer_that_cannot_be_reached()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        // Far below the bound: a write that waited for the unreachable peer would fail this deadline.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            Configure(settings, peerTls, clientTls, $"https://localhost:{TerraBaseTestServer.FreePort()}");
            settings["write-wait-ms"] = "60000";
        });
        var tenant = await ReplicationTenant.CreateAsync(server, "shop");
        using var client = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:read", "documents:write"));
        using (var created = await client.PostAsJsonAsync(new Uri($"{tenant.Path("shop")}/collections", UriKind.Relative), new { name = "people" }, token))
        {
            created.EnsureSuccessStatusCode();
        }

        for (var i = 0; i < 3; i++)
        {
            using var written = await client.PutAsJsonAsync(
                new Uri($"{tenant.Path("shop")}/collections/people/documents/{Guid.NewGuid():D}", UriKind.Relative), new { name = Faker.Name.FullName() }, token);
            await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        await Assert.That(await server.MetricAsync("terrabase_replication_write_wait_seconds_count", "result=\"acknowledged\"", token)).IsEqualTo(4);
    }

    private static void Configure(Dictionary<string, string> settings, PeerTls peerTls, ClientTls clientTls, string peerAddress)
    {
        peerTls.Apply(settings);
        clientTls.Apply(settings);
        settings["peer-addresses"] = peerAddress;
    }
}
