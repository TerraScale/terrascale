using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Grpc.Core;
using TerraBase.Fast.Documents;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class CollectionSchemaReplicationTests
{
    private const string NameSchema = """{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""";

    [Test]
    public async Task Attaching_and_detaching_a_schema_changes_validation_on_the_peer()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var (a, b) = await TerraBaseTestServer.StartPairAsync((settings, own, other) => Configure(settings, peers, tls, own, other));
        await using var disposeA = a;
        await using var disposeB = b;
        var tenant = await ReplicationTenant.CreateAsync(a, "shop");
        await tenant.BindAsync(b);
        using var clientA = tls.CreateClient(a.ClientAddress!, await tenant.KeyAsync(a, "documents:read", "documents:write"));
        using var clientB = tls.CreateClient(b.ClientAddress!, await tenant.KeyAsync(b, "documents:read", "documents:write"));
        var collection = $"{tenant.Path("shop")}/collections/items";
        var schema = $"/projects/{tenant.ProjectId}/databases/shop/collections/items/schema";
        var document = $"{collection}/documents/{DocumentApi.NewId()}";
        var body = new { count = new Faker().Random.Int(1, 100) };
        var created = await DocumentApi.SendAsync(clientA, HttpMethod.Post, $"{tenant.Path("shop")}/collections",
            JsonContent.Create(new { name = "items" }), cancellationToken: token);
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        await TerraBaseTestServer.ReplicatedAsync(a, b);

        var attached = await SetSchemaAsync(clientA, schema, token);
        await Assert.That(attached.Status).IsEqualTo(HttpStatusCode.OK);
        await TerraBaseTestServer.ReplicatedAsync(a, b);
        await SchemaAsync(clientB, schema, attached: true, token);
        var rejected = await DocumentApi.SendAsync(clientB, HttpMethod.Put, document, JsonContent.Create(body), cancellationToken: token);
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "schema_validation_failed"));
        var missing = await DocumentApi.SendAsync(clientB, HttpMethod.Get, document, cancellationToken: token);
        await Assert.That(missing.Status).IsEqualTo(HttpStatusCode.NotFound);

        var detached = await DocumentApi.SendAsync(clientA, HttpMethod.Delete, schema, cancellationToken: token);
        await Assert.That(detached.Status).IsEqualTo(HttpStatusCode.OK);
        await TerraBaseTestServer.ReplicatedAsync(a, b);
        await SchemaAsync(clientB, schema, attached: false, token);
        var accepted = await DocumentApi.SendAsync(clientB, HttpMethod.Put, document, JsonContent.Create(body), cancellationToken: token);
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.Created);
        await TerraBaseTestServer.ReplicatedAsync(a, b);
        var replicated = await DocumentApi.SendAsync(clientA, HttpMethod.Get, document, cancellationToken: token);
        await Assert.That(replicated.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(replicated.Json.GetProperty("document").GetProperty("count").GetInt32()).IsEqualTo(body.count);

        // An attachment at the value ceiling must fit its replicated metadata envelope too.
        var values = string.Join(',', Enumerable.Repeat("0", 4091));
        var maximum = $$"""{"examples":[{{values}}]}""";
        var maximumAttached = await DocumentApi.SendAsync(clientA, HttpMethod.Put, schema,
            new StringContent(maximum, Encoding.UTF8, "application/json"), cancellationToken: token);
        await Assert.That(maximumAttached.Status).IsEqualTo(HttpStatusCode.OK);
        await TerraBaseTestServer.ReplicatedAsync(a, b);
        var maximumRead = await DocumentApi.SendAsync(clientB, HttpMethod.Get, schema, cancellationToken: token);
        await Assert.That(maximumRead.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(maximumRead.Json.GetProperty("schema").GetProperty("examples").GetArrayLength()).IsEqualTo(4091);
    }

    [Test]
    public async Task A_committed_document_replays_after_the_receiver_attaches_a_stricter_schema()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var receiverAddress = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdGroups = 0;
        // Forward the actual protocol unchanged, but hold the old body until the receiving node has a schema.
        await using var relay = await FakePeer.StartAsync(peers, async (requests, responses, context) =>
        {
            using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var relayToken = relayStop.Token;
            using var channel = peers.CreateChannel(await receiverAddress.Task.WaitAsync(relayToken));
            using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: relayToken);
            var reading = ForwardResponsesAsync();
            try
            {
                while (await requests.MoveNext(relayToken))
                {
                    if (requests.Current.CommittedGroup is not null && Volatile.Read(ref holdGroups) != 0)
                    {
                        held.TrySetResult();
                        await release.Task.WaitAsync(relayToken);
                    }

                    await call.RequestStream.WriteAsync(requests.Current, relayToken);
                }

                await call.RequestStream.CompleteAsync();
                await reading;
            }
            finally
            {
                await relayStop.CancelAsync();
                call.Dispose();
                try
                {
                    await reading;
                }
                catch (Exception error) when (relayStop.IsCancellationRequested && error is RpcException or OperationCanceledException)
                {
                    // Join the relay's response reader even when a node or the test shuts down.
                }
            }

            async Task ForwardResponsesAsync()
            {
                while (await call.ResponseStream.MoveNext(relayToken))
                {
                    await responses.WriteAsync(call.ResponseStream.Current, relayToken);
                }
            }
        });
        var (a, b) = await TerraBaseTestServer.StartPairAsync((settings, own, other) =>
        {
            Configure(settings, peers, tls, own, other);
            if (own < other)
            {
                settings["peer-addresses"] = relay.Address.ToString();
            }
        });
        await using var disposeA = a;
        await using var disposeB = b;
        var (source, receiver) = a.PeerAddress!.Port < b.PeerAddress!.Port ? (a, b) : (b, a);
        receiverAddress.SetResult(receiver.PeerAddress!);
        var tenant = await ReplicationTenant.CreateAsync(source, "shop");
        await tenant.BindAsync(receiver);
        using var writer = tls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, "documents:read", "documents:write"));
        using var reader = tls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read", "documents:write"));
        var schema = $"/projects/{tenant.ProjectId}/databases/shop/collections/items/schema";
        var document = $"{tenant.Path("shop")}/collections/items/documents/{DocumentApi.NewId()}";
        var created = await DocumentApi.SendAsync(writer, HttpMethod.Post, $"{tenant.Path("shop")}/collections",
            JsonContent.Create(new { name = "items" }), cancellationToken: token);
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        await TerraBaseTestServer.ReplicatedAsync(source, receiver);

        Volatile.Write(ref holdGroups, 1);
        var body = new { legacy = new Faker().Commerce.ProductName() };
        var committed = await DocumentApi.SendAsync(writer, HttpMethod.Put, document, JsonContent.Create(body), cancellationToken: token);
        await Assert.That(committed.Status).IsEqualTo(HttpStatusCode.Created);
        await held.Task.WaitAsync(token);
        var absent = await DocumentApi.SendAsync(reader, HttpMethod.Get, document, cancellationToken: token);
        await Assert.That(absent.Status).IsEqualTo(HttpStatusCode.NotFound);

        var attached = await SetSchemaAsync(reader, schema, token);
        await Assert.That(attached.Status).IsEqualTo(HttpStatusCode.OK);
        // The held request stream also delays acknowledgments in this direction.
        // Observe schema delivery before releasing it; assert durable acknowledgment afterward.
        using var expectedSchema = JsonDocument.Parse(NameSchema);
        await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
        {
            var response = await DocumentApi.SendAsync(writer, HttpMethod.Get, schema, cancellationToken: cancellationToken);
            return response.Status == HttpStatusCode.OK
                && JsonElement.DeepEquals(response.Json.GetProperty("schema"), expectedSchema.RootElement);
        });
        await SchemaAsync(writer, schema, attached: true, token);
        var rejected = await DocumentApi.SendAsync(reader, HttpMethod.Put, document, JsonContent.Create(body), cancellationToken: token);
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "schema_validation_failed"));

        release.SetResult();
        await TerraBaseTestServer.ReplicatedAsync(source, receiver);
        var local = await DocumentApi.SendAsync(writer, HttpMethod.Get, document, cancellationToken: token);
        var remote = await DocumentApi.SendAsync(reader, HttpMethod.Get, document, cancellationToken: token);
        await Assert.That(local.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(remote.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(local.ETag).IsEqualTo(committed.ETag);
        await Assert.That(local.Json.GetProperty("document").GetProperty("legacy").GetString()).IsEqualTo(body.legacy);
        await Assert.That(JsonElement.DeepEquals(remote.Json.GetProperty("document"), local.Json.GetProperty("document"))).IsTrue();
        await Assert.That(remote.ETag).IsEqualTo(local.ETag);
        await SchemaAsync(reader, schema, attached: true, token);
    }

    private static void Configure(Dictionary<string, string> settings, PeerTls peers, ClientTls tls, int own, int other)
    {
        peers.Apply(settings);
        tls.Apply(settings);
        settings["peer-port"] = own.ToString(CultureInfo.InvariantCulture);
        settings["peer-addresses"] = $"https://localhost:{other}";
        settings["write-wait-ms"] = "0";
    }

    private static Task<ApiResponse> SetSchemaAsync(HttpClient client, string path, CancellationToken token) =>
        DocumentApi.SendAsync(client, HttpMethod.Put, path, new StringContent(NameSchema, Encoding.UTF8, "application/json"), cancellationToken: token);

    private static async Task SchemaAsync(HttpClient client, string path, bool attached, CancellationToken token)
    {
        var response = await DocumentApi.SendAsync(client, HttpMethod.Get, path, cancellationToken: token);
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
        if (attached)
        {
            using var expected = JsonDocument.Parse(NameSchema);
            await Assert.That(JsonElement.DeepEquals(response.Json.GetProperty("schema"), expected.RootElement)).IsTrue();
        }
        else
        {
            await Assert.That(response.Json.GetProperty("schema").ValueKind).IsEqualTo(JsonValueKind.Null);
        }
    }
}
