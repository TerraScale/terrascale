using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Fast.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.Alpha;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Idle nodes repair divergent durable registers over their real TLS listeners.</summary>
public sealed class AntiEntropyTests
{
    [Test]
    [Timeout(120_000)]
    public async Task Range_pages_bound_keys_and_bytes_and_continue_past_empty_selected_pages(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = deadline.Token;
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        var tenant = await ReplicationTenant.CreateAsync(server, ["repair"], cancellationToken);
        using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, ["documents:write"], cancellationToken));
        using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative), new { name = "items" }, token))
        {
            collection.EnsureSuccessStatusCode();
        }

        var selected = (MerkleTree.Range(DocumentDelta.Collections, "items") + 1) % MerkleTree.Leaves;
        static int Range(string id)
        {
            return MerkleTree.Range("items", id);
        }
        var small = Enumerable.Range(0, 10_000).Select(i => $"00000000000000{i:000000000000}")
            .Where(id => Range(id) != selected).Take(130).ToArray();
        var faker = new Faker();
        using (var transaction = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/transactions", UriKind.Relative),
            new { ops = small.Select(id => new { op = "create", collection = "items", id, document = new { name = faker.Name.FullName() } }).ToArray() }, token))
        {
            transaction.EnsureSuccessStatusCode();
        }

        var cursor = 0;
        for (var i = 0; i < 4; i++)
        {
            var last = i == 3;
            cursor = Enumerable.Range(cursor + 1, 10_000 - cursor - 1)
                .First(n => Range($"7ZZZZZZZZZZZZZ{n:000000000000}") == selected == last);
            using var created = await writer.PutAsJsonAsync(Document(tenant, $"7ZZZZZZZZZZZZZ{cursor:000000000000}"),
                new { payload = faker.Random.AlphaNumeric(650_000) }, token);
            created.EnsureSuccessStatusCode();
        }

        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var client = new AntiEntropy.AntiEntropyClient(channel);
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = tenant.Database("repair").DatabaseId! };
        using var digestCall = client.DigestAsync(identity, cancellationToken: token);
        var digest = await digestCall.ResponseAsync;
        identity.Incarnation = digest.Incarnation;
        foreach (var ranges in new[] { (uint)ushort.MaxValue, 1U << selected })
        {
            var request = new RangeRequest { Identity = identity, Ranges = ranges };
            var pages = 0;
            var keys = 0;
            var emptyContinuations = 0;
            while (true)
            {
                using var readCall = client.ReadRangeAsync(request, cancellationToken: token);
                var page = await readCall.ResponseAsync;
                var changes = Decode(page);
                await Assert.That(changes.Count <= 128).IsTrue();
                await Assert.That(Deltas.ContentBytes(changes) <= IStorageTransaction.MaxChangeGroupBytes).IsTrue();
                pages++;
                keys += changes.Count;
                if (page.Envelope.IsEmpty && page.HasMore)
                {
                    emptyContinuations++;
                }

                if (!page.HasMore)
                {
                    break;
                }

                request.AfterCollection = page.AfterCollection;
                request.AfterId = page.AfterId;
            }

            await Assert.That(pages).IsEqualTo(3);
            await Assert.That(keys).IsEqualTo(ranges == ushort.MaxValue ? 135 : 1);
            await Assert.That(emptyContinuations).IsEqualTo(ranges == ushort.MaxValue ? 0 : 2);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task A_repaired_tombstone_reaches_shapes_while_a_concurrent_live_row_keeps_a_dropped_collection(CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        var tenant = await ReplicationTenant.CreateAsync(server, ["repair"], cancellationToken);
        using var client = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server,
            ["documents:read", "documents:write", "shapes:read"], cancellationToken));
        var collections = $"{tenant.Path("repair")}/collections";
        var shape = $"{collections}/items/shape";
        var deletedId = DocumentApi.NewId();
        var survivorId = DocumentApi.NewId();
        var faker = new Faker();
        var collection = await DocumentApi.SendAsync(client, HttpMethod.Post, collections,
            JsonContent.Create(new { name = "items" }), cancellationToken: cancellationToken);
        await Assert.That(collection.Status).IsEqualTo(HttpStatusCode.Created);
        var original = await DocumentApi.SendAsync(client, HttpMethod.Put, Document(tenant, deletedId).OriginalString,
            JsonContent.Create(new { name = faker.Name.FullName() }), cancellationToken: cancellationToken);
        await Assert.That(original.Status).IsEqualTo(HttpStatusCode.Created);
        var survivorName = faker.Name.FullName();
        var survivor = await DocumentApi.SendAsync(client, HttpMethod.Put, Document(tenant, survivorId).OriginalString,
            JsonContent.Create(new { name = survivorName }), cancellationToken: cancellationToken);
        await Assert.That(survivor.Status).IsEqualTo(HttpStatusCode.Created);

        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var repair = new AntiEntropy.AntiEntropyClient(channel);
        var identity = await IdentityAsync(server, tenant, cancellationToken);
        var registers = await RegistersAsync(server, tenant, cancellationToken);
        // A peer that knew only the collection and the first document drops the collection: the survivor, which it never
        // saw, keeps the collection and its generation.
        await MergeAsync(Deltas.Write(DocumentDelta.Collections, "items", null, over: registers[(DocumentDelta.Collections, "items")].State));
        var start = await DocumentApi.SendAsync(client, HttpMethod.Get, shape + "?offset=-1", cancellationToken: cancellationToken);
        await Assert.That(start.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var snapshot = await DocumentApi.SendAsync(client, HttpMethod.Get, start.Location!.OriginalString, cancellationToken: cancellationToken);
        await Assert.That(snapshot.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(snapshot.Json.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray())
            .Select(op => op.GetProperty("id").GetString()!)).IsEquivalentTo([deletedId, survivorId]);
        var handle = snapshot.Json.GetProperty("handle").GetString();
        var head = long.Parse(snapshot.Json.GetProperty("next_offset").GetString()!, CultureInfo.InvariantCulture);

        // Its tombstone of the document it knew reaches shapes at a local offset of its own.
        await MergeAsync(Deltas.Write("items", deletedId, null, over: registers[("items", deletedId)].State));
        var log = await DocumentApi.SendAsync(client, HttpMethod.Get, $"{shape}?handle={handle}&offset={head}", cancellationToken: cancellationToken);
        await Assert.That(log.Status).IsEqualTo(HttpStatusCode.OK);
        var change = log.Json.GetProperty("changes").EnumerateArray().Single();
        await Assert.That(change.GetProperty("offset").GetString()).IsEqualTo((head + 1).ToString(CultureInfo.InvariantCulture));
        var deletion = change.GetProperty("ops").EnumerateArray().Single();
        await Assert.That(deletion.GetProperty("op").GetString()).IsEqualTo("delete");
        await Assert.That(deletion.GetProperty("id").GetString()).IsEqualTo(deletedId);
        var retained = await DocumentApi.SendAsync(client, HttpMethod.Get, Document(tenant, survivorId).OriginalString, cancellationToken: cancellationToken);
        await Assert.That(retained.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(retained.Json.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(survivorName);

        // Deleting the survivor here completes the drop, as applying that tombstone does on every other member: no member
        // keeps an empty collection that the others removed.
        var deleted = await DocumentApi.SendAsync(client, HttpMethod.Delete, Document(tenant, survivorId).OriginalString, cancellationToken: cancellationToken);
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        var listed = await DocumentApi.SendAsync(client, HttpMethod.Get, collections, cancellationToken: cancellationToken);
        await Assert.That(listed.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(listed.Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)).DoesNotContain("items");

        async Task MergeAsync(DocumentDelta state)
        {
            using var merge = repair.MergeAsync(new StateRequest { Identity = identity, Envelope = Envelope([state]) }, cancellationToken: cancellationToken);
            await Assert.That((await merge.ResponseAsync).RepairedKeys).IsEqualTo(1U);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task Repairing_a_collection_drop_invalidates_warmed_document_and_list_caches(CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var source = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        await using var receiver = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken);
        await tenant.BindAsync(receiver);
        using var receiverReader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, ["documents:read"], cancellationToken));
        using var sourceWriter = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        var collectionPath = new Uri($"{tenant.Path("repair")}/collections/items", UriKind.Relative);
        using (var created = await sourceWriter.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative), new { name = "items" }, cancellationToken))
        {
            created.EnsureSuccessStatusCode();
        }

        var documentPath = Document(tenant, DocumentApi.NewId());
        using (var written = await sourceWriter.PutAsJsonAsync(documentPath, new { name = new Faker().Name.FullName() }, cancellationToken))
        {
            written.EnsureSuccessStatusCode();
        }

        // Repair alone brings the collection and its document to the receiver, whose reads then warm its caches.
        await Assert.That(await MergeAllAsync(peerTls, source, receiver, tenant, cancellationToken)).IsEqualTo(2U);
        var paths = new[] { documentPath, new Uri($"{collectionPath}/documents", UriKind.Relative) };
        foreach (var path in paths)
        {
            using var warm = await receiverReader.GetAsync(path, cancellationToken);
            warm.EnsureSuccessStatusCode();
        }

        using (var dropped = await sourceWriter.DeleteAsync(collectionPath, cancellationToken))
        {
            dropped.EnsureSuccessStatusCode();
        }

        // The drop's tombstones arrive in key order, the collection's own first: its drop completes with the document's.
        await Assert.That(await MergeAllAsync(peerTls, source, receiver, tenant, cancellationToken)).IsEqualTo(2U);
        foreach (var path in paths)
        {
            using var repaired = await receiverReader.GetAsync(path, cancellationToken);
            await Assert.That(repaired.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task Both_regions_meter_the_same_anti_entropy_bytes_for_a_repair(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = deadline.Token;
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var started = DateTimeOffset.UtcNow;
        var clock = new RepairClock(started);
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["region"] = "gru-1";
        }, services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(started)));
        await using var source = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["region"] = "sfo-1";
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        }, services => services.AddSingleton<TimeProvider>(clock));
        await clock.PausedAsync(token);
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken, replicaRegions: ["gru-1"]);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, token);
        await TerraBaseTestServer.ReplicatedAsync(source);
        var database = tenant.Database("repair");
        async Task<long> BytesAsync(TerraBaseTestServer node, string key)
        {
            var window = new TerraBase.Features.Management.Usage.UsageWindow(
                TerraBase.Features.Management.Usage.UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1);
            var (lifetime, _) = await node.Services.GetRequiredService<TerraBase.Features.Management.Usage.UsageMeter>().GetObservationsAsync(database, window, token) switch
            {
                TerraBase.Features.Management.Usage.UsageCounts counts => counts,
                Unavailable error => throw new InvalidOperationException(error.Message),
            };
            return lifetime.Meters?.GetValueOrDefault(key)?.Count ?? 0;
        }

        // Before the repair only replication handshakes were charged as anti-entropy.
        var before = await BytesAsync(source, "replication_bytes|sfo-1|gru-1|anti_entropy:sent");
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(tenant.File(receiver, "repair"))))
        {
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "DELETE FROM documents WHERE collection = 'items' AND id = $id;";
            corrupt.Parameters.AddWithValue("$id", ids[0]);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(1);
        }

        using var repairs = new RepairCounter(receiver);
        clock.Advance(AntiEntropyWorker.ReconcileInterval);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(repairs.Keys >= 1));
        await clock.PausedAsync(token);

        // The repair's Merkle exchange is charged too, once at each end, to the same directed region pair.
        var sent = await BytesAsync(source, "replication_bytes|sfo-1|gru-1|anti_entropy:sent");
        await Assert.That(sent).IsGreaterThan(before);
        await Assert.That(await BytesAsync(receiver, "replication_bytes|gru-1|sfo-1|anti_entropy:received")).IsEqualTo(sent);
        var returned = await BytesAsync(receiver, "replication_bytes|gru-1|sfo-1|anti_entropy:sent");
        await Assert.That(returned).IsGreaterThan(0);
        await Assert.That(await BytesAsync(source, "replication_bytes|sfo-1|gru-1|anti_entropy:received")).IsEqualTo(returned);
    }

    [Test]
    [Timeout(60_000)]
    public async Task An_unanswered_digest_is_charged_to_the_region_its_peer_named_before_even_in_a_failed_repair(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = deadline.Token;
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var started = DateTimeOffset.UtcNow;
        var clock = new RepairClock(started);
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["region"] = "gru-1";
        }, services =>
        {
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(started));
            services.AddSingleton<InvalidFirstDigest>();
            services.AddGrpc(options => options.Interceptors.Add<InvalidFirstDigest>());
        });
        await using var source = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["region"] = "sfo-1";
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        }, services => services.AddSingleton<TimeProvider>(clock));
        await clock.PausedAsync(token);
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken, replicaRegions: ["gru-1"]);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, token);
        await TerraBaseTestServer.ReplicatedAsync(source);
        var database = tenant.Database("repair");
        async Task<long> BytesAsync(TerraBaseTestServer node, string key)
        {
            var window = new TerraBase.Features.Management.Usage.UsageWindow(
                TerraBase.Features.Management.Usage.UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1);
            var (lifetime, _) = await node.Services.GetRequiredService<TerraBase.Features.Management.Usage.UsageMeter>().GetObservationsAsync(database, window, token) switch
            {
                TerraBase.Features.Management.Usage.UsageCounts counts => counts,
                Unavailable error => throw new InvalidOperationException(error.Message),
            };
            return lifetime.Meters?.GetValueOrDefault(key)?.Count ?? 0;
        }

        // An answered digest names the peer's region, though the source refuses it and that repair fails.
        clock.Advance(AntiEntropyWorker.ReconcileInterval);
        await TerraBaseTestServer.EventuallyAsync(async _ => await BytesAsync(receiver, "replication_bytes|gru-1|sfo-1|anti_entropy:received")
            == await BytesAsync(source, "replication_bytes|sfo-1|gru-1|anti_entropy:sent") && await BytesAsync(source, "replication_bytes|sfo-1|gru-1|anti_entropy:received") > 0);
        await clock.PausedAsync(token);
        var before = await BytesAsync(receiver, "replication_bytes|gru-1|sfo-1|anti_entropy:received");

        // The receiver can no longer answer a digest: the delivered request still counts on both ends.
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(tenant.File(receiver, "repair"))))
        {
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE documents SET state = zeroblob(3 * 1024 * 1024 + 1) WHERE collection = 'items' AND id = $id;";
            corrupt.Parameters.AddWithValue("$id", ids[0]);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(1);
        }

        clock.Advance(AntiEntropyWorker.ReconcileInterval);
        await TerraBaseTestServer.EventuallyAsync(async _ => await BytesAsync(receiver, "replication_bytes|gru-1|sfo-1|anti_entropy:received") > before);
        await clock.PausedAsync(token);
        await Assert.That(await BytesAsync(source, "replication_bytes|sfo-1|gru-1|anti_entropy:sent"))
            .IsEqualTo(await BytesAsync(receiver, "replication_bytes|gru-1|sfo-1|anti_entropy:received"));
    }

    // Answers the first digest without its incarnation, which the requester refuses after reading the region it names.
    private sealed class InvalidFirstDigest : Interceptor
    {
        private int _digests;

        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            var response = await continuation(request, context);
            if (response is MerkleDigest digest && Interlocked.Increment(ref _digests) == 1)
            {
                digest.Incarnation = ByteString.Empty;
            }

            return response;
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task A_rejected_anti_entropy_request_still_counts_its_delivered_bytes(CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["region"] = "gru-1";
        });
        var tenant = await ReplicationTenant.CreateAsync(server, ["repair"], cancellationToken);
        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var client = new AntiEntropy.AntiEntropyClient(channel);
        var database = tenant.Database("repair");
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = database.DatabaseId!, Region = "sfo-1" };
        var digestBytes = identity.CalculateSize() + 5L;
        using (var digestCall = client.DigestAsync(identity, cancellationToken: cancellationToken))
        {
            identity.Incarnation = (await digestCall.ResponseAsync).Incarnation;
        }

        // Valid enough to be served, then refused by the repair state (a group of no change): it still crossed the wire.
        var merge = new StateRequest { Identity = identity, Envelope = Envelope([]) };
        using (var mergeCall = client.MergeAsync(merge, cancellationToken: cancellationToken))
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await mergeCall.ResponseAsync);
            await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }

        var window = new TerraBase.Features.Management.Usage.UsageWindow(
            TerraBase.Features.Management.Usage.UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1);
        var (lifetime, _) = await server.Services.GetRequiredService<TerraBase.Features.Management.Usage.UsageMeter>()
            .GetObservationsAsync(database, window, cancellationToken) switch
        {
            TerraBase.Features.Management.Usage.UsageCounts counts => counts,
            Unavailable error => throw new InvalidOperationException(error.Message),
        };
        await Assert.That(lifetime.Meters!["replication_bytes|gru-1|sfo-1|anti_entropy:received"].Count).IsEqualTo(digestBytes + merge.CalculateSize() + 5L);
    }

    [Test]
    [Timeout(60_000)]
    public async Task A_register_rolled_back_past_a_drop_rejoins_its_tombstone_without_recreating_the_collection(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = deadline.Token;
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var started = DateTimeOffset.UtcNow;
        var clock = new RepairClock(started);
        var receiverClock = new FakeTimeProvider(started);
        await using var receiver = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); },
            services => services.AddSingleton<TimeProvider>(receiverClock));
        await using var source = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        }, services => services.AddSingleton<TimeProvider>(clock));
        await clock.PausedAsync(token);
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, token);
        await TerraBaseTestServer.ReplicatedAsync(source);
        var sourceFile = tenant.File(source, "repair");
        var receiverFile = tenant.File(receiver, "repair");
        var live = await RowAsync(receiverFile, ids[0], token);
        using (var dropped = await writer.DeleteAsync(new Uri($"{tenant.Path("repair")}/collections/items", UriKind.Relative), token))
        {
            dropped.EnsureSuccessStatusCode();
        }

        await TerraBaseTestServer.ReplicatedAsync(source);
        var expected = (await StateAsync(receiverFile, ids[0], token))!.Value;
        await Assert.That(expected.Body).IsNull();
        await Assert.That(await StateAsync(sourceFile, ids[0], token)).IsEqualTo(expected);
        var sourceFrontiers = await FrontiersAsync(sourceFile, token);
        var receiverFrontiers = await FrontiersAsync(receiverFile, token);
        using var repairs = new RepairCounter(receiver);
        // The receiver's register goes back to the live write the drop's tombstone replaced.
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(receiverFile)))
        {
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE documents SET state = $state, version = $version, body = $body WHERE collection = 'items' AND id = $id;";
            corrupt.Parameters.AddWithValue("$state", live.State);
            corrupt.Parameters.AddWithValue("$version", live.Version);
            corrupt.Parameters.AddWithValue("$body", live.Body);
            corrupt.Parameters.AddWithValue("$id", ids[0]);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(1);
        }

        // The observed startup pause is still pending because this clock has not advanced.
        clock.Advance(AntiEntropyWorker.ReconcileInterval);
        await TerraBaseTestServer.EventuallyAsync(async checkToken =>
            await StateAsync(receiverFile, ids[0], checkToken) == expected && repairs.Keys >= 1);
        await clock.PausedAsync(token);
        await Assert.That(repairs.Keys).IsEqualTo(1);
        // The rolled-back register is older than the source's: joining it there changed nothing.
        await Assert.That(await StateAsync(sourceFile, ids[0], token)).IsEqualTo(expected);
        await ReplayIdenticalAsync(peerTls, source, receiver, tenant, token);
        await ReplayIdenticalAsync(peerTls, receiver, source, tenant, token);
        await Assert.That(repairs.Keys).IsEqualTo(1);
        await Assert.That(await FrontiersAsync(sourceFile, token)).IsEquivalentTo(sourceFrontiers);
        await Assert.That(await FrontiersAsync(receiverFile, token)).IsEquivalentTo(receiverFrontiers);
        foreach (var node in new[] { source, receiver })
        {
            using var client = clientTls.CreateClient(node.ClientAddress!, await tenant.KeyAsync(node, ["documents:read"], cancellationToken));
            using var listed = await client.GetAsync(new Uri($"{tenant.Path("repair")}/collections/items/documents", UriKind.Relative), token);
            await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    [Timeout(60_000)]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Caught_up_members_repair_missing_and_stale_rows_without_new_writes(bool resurrectDeleted, bool faultOnSource, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = deadline.Token;
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        await using var source = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        });
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        var damaged = faultOnSource ? source : receiver;
        using var reader = clientTls.CreateClient(damaged.ClientAddress!, await tenant.KeyAsync(damaged, ["documents:read"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, token);
        await TerraBaseTestServer.ReplicatedAsync(source);
        var sourceFile = tenant.File(source, "repair");
        var receiverFile = tenant.File(receiver, "repair");
        var damagedFile = tenant.File(damaged, "repair");
        var expected = new Dictionary<string, (long Version, string? Body)>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            expected.Add(id, (await StateAsync(sourceFile, id, token))!.Value);
            await Assert.That(await StateAsync(receiverFile, id, token)).IsEqualTo(expected[id]);
        }

        await Assert.That(await MatchesAsync(reader, Document(tenant, ids[4]), expected[ids[4]].Body, token)).IsTrue();
        await Assert.That(expected[ids[^1]].Body).IsNull();
        var sourceFrontiers = await FrontiersAsync(sourceFile, token);
        var receiverFrontiers = await FrontiersAsync(receiverFile, token);
        using var repairs = new RepairCounter(damaged);
        var phase = "healing";
        var mismatchedDocument = -1;
        var storedStateMatches = -1;
        using var timeoutDiagnostics = token.Register(() => Console.WriteLine(
            $"Chaos ({resurrectDeleted}, {faultOnSource}): phase={Volatile.Read(ref phase)}; mismatched document={Volatile.Read(ref mismatchedDocument)}; stored state matches={Volatile.Read(ref storedStateMatches)}; repaired keys={repairs.Keys}"));

        // Chaos bypass: leave the durable ACKs, journal and replication clock untouched. A stale or resurrected row holds an
        // older register of another replica instead of the real write.
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(damagedFile)))
        {
            await connection.OpenAsync(token);
            StoredRegisters.Register(connection);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                DELETE FROM documents WHERE collection = 'items' AND id = $missing;
                UPDATE documents SET state = tb_state('{"name":"stale"}'), version = 65536, body = '{"name":"stale"}'
                    WHERE collection = 'items' AND id = $stale;
                DELETE FROM documents WHERE collection = 'items' AND id = $deleted AND NOT $resurrect;
                UPDATE documents SET state = tb_state('{"name":"resurrected"}'), version = 65536, body = '{"name":"resurrected"}'
                    WHERE collection = 'items' AND id = $deleted AND $resurrect;
                """;
            corrupt.Parameters.AddWithValue("$missing", ids[0]);
            corrupt.Parameters.AddWithValue("$stale", ids[1]);
            corrupt.Parameters.AddWithValue("$deleted", ids[^1]);
            corrupt.Parameters.AddWithValue("$resurrect", resurrectDeleted);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(3);
        }

        // No client writes follow the injection. A 404 alone cannot prove a missing tombstone was restored.
        await TerraBaseTestServer.EventuallyAsync(async checkToken =>
        {
            foreach (var (id, state) in expected)
            {
                if (!await MatchesAsync(reader, Document(tenant, id), state.Body, checkToken))
                {
                    Volatile.Write(ref mismatchedDocument, Array.IndexOf(ids, id));
                    Volatile.Write(ref storedStateMatches, await StateAsync(damagedFile, id, checkToken) == state ? 1 : 0);
                    return false;
                }
            }

            Volatile.Write(ref mismatchedDocument, -1);
            var tombstoneMatches = await StateAsync(damagedFile, ids[^1], checkToken) == expected[ids[^1]];
            Volatile.Write(ref storedStateMatches, tombstoneMatches ? 1 : 0);
            return tombstoneMatches && repairs.Keys >= 3;
        });

        foreach (var (id, state) in expected)
        {
            await Assert.That(await StateAsync(damagedFile, id, token)).IsEqualTo(state);
        }

        await Assert.That(repairs.Keys).IsEqualTo(3);
        Volatile.Write(ref phase, "ordinary replication catch-up");
        await TerraBaseTestServer.ReplicatedAsync(source);
        Volatile.Write(ref phase, "frontier assertions");
        await Assert.That(await FrontiersAsync(sourceFile, token)).IsEquivalentTo(sourceFrontiers);
        await Assert.That(await FrontiersAsync(receiverFile, token)).IsEquivalentTo(receiverFrontiers);
    }

    // Reads, queries and SQL use a row's version and body; digests hash its register. A row damaged beside its intact
    // register, on either end of the exchange, is rewritten from the register by the next pass, with no write anywhere.
    [Test]
    [Timeout(60_000)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_row_damaged_beside_its_intact_register_reads_as_the_register_again(bool damageSource, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = deadline.Token;
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var started = DateTimeOffset.UtcNow;
        var clock = new RepairClock(started);
        await using var receiver = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); },
            services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(started)));
        await using var source = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        }, services => services.AddSingleton<TimeProvider>(clock));
        await clock.PausedAsync(token);
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        var damaged = damageSource ? source : receiver;
        using var reader = clientTls.CreateClient(damaged.ClientAddress!, await tenant.KeyAsync(damaged, ["documents:read"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, token);
        await TerraBaseTestServer.ReplicatedAsync(source);
        var file = tenant.File(damaged, "repair");
        var expected = new Dictionary<string, (long Version, string? Body)>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            expected.Add(id, (await StateAsync(file, id, token))!.Value);
        }

        var registers = await RegistersAsync(damaged, tenant, token);
        var frontiers = await FrontiersAsync(file, token);
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(file)))
        {
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                UPDATE documents SET body = '{"name":"damaged"}' WHERE collection = 'items' AND id = $changed;
                UPDATE documents SET body = NULL WHERE collection = 'items' AND id = $erased;
                UPDATE documents SET version = version + 1 WHERE collection = 'items' AND id = $versioned;
                """;
            corrupt.Parameters.AddWithValue("$changed", ids[1]);
            corrupt.Parameters.AddWithValue("$erased", ids[2]);
            corrupt.Parameters.AddWithValue("$versioned", ids[3]);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(3);
        }

        // Customers see the damage, a false deletion included, until the next pass.
        await Assert.That(await MatchesAsync(reader, Document(tenant, ids[1]), "{\"name\":\"damaged\"}", token)).IsTrue();
        await Assert.That(await MatchesAsync(reader, Document(tenant, ids[2]), null, token)).IsTrue();
        // One pass digests both ends.
        clock.Advance(AntiEntropyWorker.ReconcileInterval);
        await clock.PausedAsync(token);
        foreach (var (id, state) in expected)
        {
            await Assert.That(await StateAsync(file, id, token)).IsEqualTo(state);
            await Assert.That(await MatchesAsync(reader, Document(tenant, id), state.Body, token)).IsTrue();
        }

        var repaired = await RegistersAsync(damaged, tenant, token);
        await Assert.That(repaired.Keys).IsEquivalentTo(registers.Keys);
        foreach (var (key, register) in registers)
        {
            await Assert.That(repaired[key]).IsEqualTo(register);
        }

        await Assert.That(await FrontiersAsync(file, token)).IsEquivalentTo(frontiers);
    }

    [Test]
    [Timeout(60_000)]
    public async Task A_source_with_the_only_peer_list_pulls_an_acknowledged_row_and_identical_replays_repair_nothing(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = deadline.Token;
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        await using var source = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        });
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        using var sourceReader = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:read"], cancellationToken));
        using var receiverReader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, ["documents:read"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, token);
        await TerraBaseTestServer.ReplicatedAsync(source);
        var sourceFile = tenant.File(source, "repair");
        var receiverFile = tenant.File(receiver, "repair");
        var retained = (await StateAsync(receiverFile, ids[0], token))!.Value;
        await Assert.That(await MatchesAsync(receiverReader, Document(tenant, ids[0]), retained.Body, token)).IsTrue();
        var sourceFrontiers = await FrontiersAsync(sourceFile, token);
        var receiverFrontiers = await FrontiersAsync(receiverFile, token);
        using var sourceRepairs = new RepairCounter(source);
        using var receiverRepairs = new RepairCounter(receiver);

        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(sourceFile)))
        {
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "DELETE FROM documents WHERE collection = 'items' AND id = $id;";
            corrupt.Parameters.AddWithValue("$id", ids[0]);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(1);
        }

        // The receiver has no configured peers: recovery requires the source's maintenance pull.
        await TerraBaseTestServer.EventuallyAsync(async checkToken =>
            await MatchesAsync(sourceReader, Document(tenant, ids[0]), retained.Body, checkToken) && sourceRepairs.Keys >= 1);
        await Assert.That(await StateAsync(sourceFile, ids[0], token)).IsEqualTo(retained);
        await Assert.That(sourceRepairs.Keys).IsEqualTo(1);
        await Assert.That(receiverRepairs.Keys).IsEqualTo(0);

        // Replay every range, including unchanged rows, through alpha gRPC. Each durable merge must report zero.
        await ReplayIdenticalAsync(peerTls, receiver, source, tenant, token);
        await Assert.That(sourceRepairs.Keys).IsEqualTo(1);
        await Assert.That(receiverRepairs.Keys).IsEqualTo(0);
        await TerraBaseTestServer.ReplicatedAsync(source);
        await Assert.That(await FrontiersAsync(sourceFile, token)).IsEquivalentTo(sourceFrontiers);
        await Assert.That(await FrontiersAsync(receiverFile, token)).IsEquivalentTo(receiverFrontiers);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Repeated_deletions_return_each_register_once_across_bounded_range_pages(CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        var tenant = await ReplicationTenant.CreateAsync(server, ["repair"], cancellationToken);
        using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, ["documents:write"], cancellationToken));
        using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative),
            new { name = "items" }, cancellationToken))
        {
            collection.EnsureSuccessStatusCode();
        }

        var ids = Enumerable.Range(0, 130).Select(_ => DocumentApi.NewId()).ToArray();
        var name = new Faker().Name.FullName();
        _ = await TransactAsync(writer, tenant, ids.Select(id => (object)new { op = "create", collection = "items", id, document = new { name } }), cancellationToken);
        _ = await TransactAsync(writer, tenant, ids.Select(id => (object)new { op = "delete", collection = "items", id }), cancellationToken);
        var repeated = ids.Take(3).ToArray();
        for (var i = 0; i < 16; i++)
        {
            _ = await TransactAsync(writer, tenant, repeated.Select(id => (object)new { op = "create", collection = "items", id, document = new { name } }), cancellationToken);
            _ = await TransactAsync(writer, tenant, repeated.Select(id => (object)new { op = "delete", collection = "items", id }), cancellationToken);
        }

        using (var newer = await writer.PutAsJsonAsync(Document(tenant, ids[2]), new { name }, cancellationToken))
        {
            newer.EnsureSuccessStatusCode();
        }

        var file = tenant.File(server, "repair");
        var live = (await StateAsync(file, ids[2], cancellationToken))!.Value;
        await Assert.That(live.Body).IsNotNull();
        var frontiers = await FrontiersAsync(file, cancellationToken);
        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var client = new AntiEntropy.AntiEntropyClient(channel);
        var identity = await IdentityAsync(server, tenant, cancellationToken);
        var request = new RangeRequest { Identity = identity, Ranges = ushort.MaxValue };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        var keys = 0;
        while (true)
        {
            using var readCall = client.ReadRangeAsync(request, cancellationToken: cancellationToken);
            var page = await readCall.ResponseAsync;
            var changes = Decode(page);
            await Assert.That(changes.Count <= 128).IsTrue();
            await Assert.That(Deltas.ContentBytes(changes) <= IStorageTransaction.MaxChangeGroupBytes).IsTrue();
            pages++;
            keys += changes.Count;
            foreach (var change in changes.Where(change => change.Collection == "items"))
            {
                await Assert.That(seen.Add(change.Id)).IsTrue();
                // Each register reads as its row: the live write or the latest tombstone.
                var winner = change.State.Values.MaxBy(value => (value.Value.Stamp.Value, value.Key.Replica.Value, value.Key.Counter)).Value;
                await Assert.That(((long)winner.Stamp.Value, Deltas.Json(change))).IsEqualTo((await StateAsync(file, change.Id, cancellationToken))!.Value);
                await Assert.That(Deltas.Json(change) is null).IsEqualTo(change.Id != ids[2]);
            }

            if (!page.HasMore)
            {
                break;
            }

            await Assert.That(page.AfterCollection != request.AfterCollection || page.AfterId != request.AfterId).IsTrue();
            request.AfterCollection = page.AfterCollection;
            request.AfterId = page.AfterId;
        }

        await Assert.That(pages).IsEqualTo(2);
        await Assert.That(keys).IsEqualTo(131);
        await Assert.That(seen).IsEquivalentTo(ids);
        await Assert.That(await StateAsync(file, ids[2], cancellationToken)).IsEqualTo(live);
        await Assert.That(await FrontiersAsync(file, cancellationToken)).IsEquivalentTo(frontiers);
    }

    private static List<DocumentDelta> Decode(StatePage page) => page.Envelope.IsEmpty ? []
        : [.. DeltaGroup.Decode(ApplicationEnvelope.Decode(page.Envelope.Span, ReplicationProtocol.MaxApplicationBytes), maxChanges: 128).Changes];

    private static ByteString Envelope(IReadOnlyList<DocumentDelta> states) =>
        ByteString.CopyFrom(ApplicationEnvelope.Encode(DeltaGroup.Encode(new CapturedGroup([], states), out _)).Span);

    private static async Task<long> TransactAsync(HttpClient writer, ReplicationTenant tenant, IEnumerable<object> operations, CancellationToken token)
    {
        using var response = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/transactions", UriKind.Relative), new { ops = operations.ToArray() }, token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return long.Parse(body.RootElement.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture);
    }

    private static async Task<string[]> SeedAsync(HttpClient writer, ReplicationTenant tenant, CancellationToken token)
    {
        using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative), new { name = "items" }, token))
        {
            collection.EnsureSuccessStatusCode();
        }

        var faker = new Faker();
        var ids = Enumerable.Range(0, 6).Select(_ => DocumentApi.NewId()).ToArray();
        foreach (var id in ids)
        {
            // HTTP responses re-encode these escapes; retained replication state must still keep its exact bytes.
            using var created = await writer.PutAsJsonAsync(Document(tenant, id), new { name = faker.Name.FullName() + " 'é'", city = faker.Address.City() }, token);
            created.EnsureSuccessStatusCode();
        }

        using var deleted = await writer.DeleteAsync(Document(tenant, ids[^1]), token);
        deleted.EnsureSuccessStatusCode();
        return ids;
    }

    private static Uri Document(ReplicationTenant tenant, string id) =>
        new($"{tenant.Path("repair")}/collections/items/documents/by-key?id={id}", UriKind.Relative);

    private static async Task<bool> MatchesAsync(HttpClient reader, Uri path, string? expected, CancellationToken token)
    {
        using var response = await reader.GetAsync(path, token);
        if (expected is null)
        {
            return response.StatusCode == HttpStatusCode.NotFound;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        using var expectedBody = JsonDocument.Parse(expected);
        return JsonElement.DeepEquals(body.RootElement.GetProperty("document"), expectedBody.RootElement);
    }

    private static async Task<(long Version, string? Body)?> StateAsync(string file, string id, CancellationToken token)
    {
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file, readOnly: true));
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, body FROM documents WHERE collection = 'items' AND id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var row = await command.ExecuteReaderAsync(token);
        return await row.ReadAsync(token) ? (row.GetInt64(0), row.IsDBNull(1) ? null : row.GetString(1)) : null;
    }

    // A document row as stored: its register, and the version and body of the register's last-writer-wins write.
    private static async Task<(byte[] State, long Version, string Body)> RowAsync(string file, string id, CancellationToken token)
    {
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file, readOnly: true));
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, version, body FROM documents WHERE collection = 'items' AND id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var row = await command.ExecuteReaderAsync(token);
        return await row.ReadAsync(token) ? (row.GetFieldValue<byte[]>(0), row.GetInt64(1), row.GetString(2))
            : throw new InvalidOperationException("the document row is missing");
    }

    // This database's own stream and the applied position of every other origin: repair never moves either.
    private static async Task<string[]> FrontiersAsync(string file, CancellationToken token)
    {
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file, readOnly: true));
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'journal:' || sequence || ':' || hex(dependencies) FROM journal
            UNION ALL SELECT 'applied:' || hex(origin) || ':' || sequence FROM applied;
            """;
        await using var rows = await command.ExecuteReaderAsync(token);
        var frontiers = new List<string>();
        while (await rows.ReadAsync(token))
        {
            frontiers.Add(rows.GetString(0));
        }

        return [.. frontiers];
    }

    // Every register of the database by key, read through the engine.
    private static async Task<Dictionary<(string Collection, string Id), DocumentDelta>> RegistersAsync(
        TerraBaseTestServer server, ReplicationTenant tenant, CancellationToken token)
    {
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("repair"), token))!;
        await using var read = await store.BeginTransactionAsync(write: false, token);
        var page = await read.ReadReplicationStateAsync(null, null, token);
        await Assert.That(page.HasMore).IsFalse();
        return page.States.ToDictionary(state => (state.Collection, state.Id));
    }

    private static async Task ReplayIdenticalAsync(
        PeerTls tls, TerraBaseTestServer from, TerraBaseTestServer to, ReplicationTenant tenant, CancellationToken token)
    {
        using var fromChannel = tls.CreateChannel(from.PeerAddress!);
        using var toChannel = tls.CreateChannel(to.PeerAddress!);
        var fromClient = new AntiEntropy.AntiEntropyClient(fromChannel);
        var toClient = new AntiEntropy.AntiEntropyClient(toChannel);
        var database = tenant.Database("repair");
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = database.DatabaseId! };
        using var fromDigestCall = fromClient.DigestAsync(identity, cancellationToken: token);
        using var toDigestCall = toClient.DigestAsync(identity, cancellationToken: token);
        var fromDigest = await fromDigestCall.ResponseAsync;
        var toDigest = await toDigestCall.ResponseAsync;
        await Assert.That(fromDigest.Envelope).IsEqualTo(toDigest.Envelope);
        var (repaired, replayed) = await CopyRangesAsync(fromClient, toClient, await IdentityAsync(from, tenant, token), await IdentityAsync(to, tenant, token), token);
        await Assert.That(repaired).IsEqualTo(0U);
        await Assert.That(replayed).IsTrue();
    }

    // Reads every range of from and merges each page into to: the keys whose register changed.
    private static async Task<uint> MergeAllAsync(PeerTls tls, TerraBaseTestServer from, TerraBaseTestServer to, ReplicationTenant tenant, CancellationToken token)
    {
        using var fromChannel = tls.CreateChannel(from.PeerAddress!);
        using var toChannel = tls.CreateChannel(to.PeerAddress!);
        var (repaired, _) = await CopyRangesAsync(new AntiEntropy.AntiEntropyClient(fromChannel), new AntiEntropy.AntiEntropyClient(toChannel),
            await IdentityAsync(from, tenant, token), await IdentityAsync(to, tenant, token), token);
        return repaired;
    }

    private static async Task<(uint Repaired, bool Replayed)> CopyRangesAsync(
        AntiEntropy.AntiEntropyClient fromClient, AntiEntropy.AntiEntropyClient toClient, ShardIdentity from, ShardIdentity to, CancellationToken token)
    {
        var request = new RangeRequest { Identity = from, Ranges = ushort.MaxValue };
        uint repaired = 0;
        var replayed = false;
        while (true)
        {
            using var readCall = fromClient.ReadRangeAsync(request, cancellationToken: token);
            var page = await readCall.ResponseAsync;
            if (!page.Envelope.IsEmpty)
            {
                using var mergeCall = toClient.MergeAsync(new StateRequest { Identity = to, Envelope = page.Envelope }, cancellationToken: token);
                repaired += (await mergeCall.ResponseAsync).RepairedKeys;
                replayed = true;
            }

            if (!page.HasMore)
            {
                return (repaired, replayed);
            }

            request.AfterCollection = page.AfterCollection;
            request.AfterId = page.AfterId;
        }
    }

    // A repair request pinned to target's database file.
    private static async Task<ShardIdentity> IdentityAsync(TerraBaseTestServer target, ReplicationTenant tenant, CancellationToken token)
    {
        var database = tenant.Database("repair");
        var store = (await target.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token))!;
        return new ShardIdentity
        {
            Organization = tenant.OrganizationId,
            Project = tenant.ProjectId,
            Database = database.DatabaseId!,
            Incarnation = ByteString.CopyFrom(store.Incarnation.ToByteArray()),
        };
    }

    private sealed class RepairCounter : IDisposable
    {
        private readonly MeterListener _listener;
        private long _keys;

        public RepairCounter(TerraBaseTestServer server)
        {
            var scope = server.Services.GetRequiredService<IMeterFactory>();
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == Telemetry.MeterName && ReferenceEquals(instrument.Meter.Scope, scope)
                        && instrument.Name == "terrabase.replication.repaired.keys")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _keys, value));
            _listener.Start();
        }

        public long Keys => Interlocked.Read(ref _keys);

        public void Dispose() => _listener.Dispose();
    }
}
