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
using TerraBase.Fast.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.Alpha;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Idle nodes repair divergent durable state over their real TLS listeners.</summary>
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

        var selected = (MerkleTree.Range(new DocumentChange(DocumentChange.Collections, "items", 1, null)) + 1) % MerkleTree.Leaves;
        static int Range(string id)
        {
            return MerkleTree.Range(new DocumentChange("items", id, 1, null));
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
        identity.Origin = ByteString.CopyFrom(EntityIds.New().ToByteArray());
        identity.SourceProof = new JournalProof { Origin = identity.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(identity.Origin.Span)) };
        identity.TargetProof = new JournalProof { Origin = digest.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(digest.Origin.Span)) };
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
                var changes = page.Envelope.IsEmpty ? [] : ChangeGroup.Decode(ApplicationEnvelope.Decode(page.Envelope.Span, ReplicationProtocol.MaxApplicationBytes));
                await Assert.That(changes.Count <= 128).IsTrue();
                await Assert.That(ChangeGroup.ContentBytes(changes) <= IStorageTransaction.MaxChangeGroupBytes).IsTrue();
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
    public async Task A_tombstone_at_the_collection_drop_version_reaches_shapes_when_a_concurrent_live_row_keeps_the_collection(CancellationToken cancellationToken)
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
        var version = long.Parse(original.Json.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture);
        var survivorName = faker.Name.FullName();
        var survivor = await DocumentApi.SendAsync(client, HttpMethod.Put, Document(tenant, survivorId).OriginalString,
            JsonContent.Create(new { name = survivorName }), cancellationToken: cancellationToken);
        await Assert.That(survivor.Status).IsEqualTo(HttpStatusCode.Created);

        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var repair = new AntiEntropy.AntiEntropyClient(channel);
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = tenant.Database("repair").DatabaseId! };
        using var digestCall = repair.DigestAsync(identity, cancellationToken: cancellationToken);
        var digest = await digestCall.ResponseAsync;
        identity.Incarnation = digest.Incarnation;
        identity.Origin = ByteString.CopyFrom(EntityIds.New().ToByteArray());
        identity.SourceProof = new JournalProof { Origin = identity.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(identity.Origin.Span)) };
        identity.TargetProof = new JournalProof { Origin = digest.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(digest.Origin.Span)) };
        // The drop cannot remove a tied live document or the newer concurrent row, so the generation remains.
        await MergeAsync(new DocumentChange(DocumentChange.Collections, "items", version, null));
        var start = await DocumentApi.SendAsync(client, HttpMethod.Get, shape + "?offset=-1", cancellationToken: cancellationToken);
        await Assert.That(start.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var snapshot = await DocumentApi.SendAsync(client, HttpMethod.Get, start.Location!.OriginalString, cancellationToken: cancellationToken);
        await Assert.That(snapshot.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(snapshot.Json.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray())
            .Select(op => op.GetProperty("id").GetString()!)).IsEquivalentTo([deletedId, survivorId]);
        var handle = snapshot.Json.GetProperty("handle").GetString();
        var head = long.Parse(snapshot.Json.GetProperty("next_offset").GetString()!, CultureInfo.InvariantCulture);

        await MergeAsync(new DocumentChange("items", deletedId, version, null));
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

        async Task MergeAsync(DocumentChange state)
        {
            using var merge = repair.MergeAsync(new StateRequest
            {
                Identity = identity,
                Envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(ChangeGroup.Encode([state])).Span),
            }, cancellationToken: cancellationToken);
            await Assert.That((await merge.ResponseAsync).RepairedKeys).IsEqualTo(1U);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task Repairing_only_a_collection_drop_invalidates_warmed_document_and_list_caches(CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var source = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        await using var receiver = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken);
        await tenant.BindAsync(receiver);
        using var receiverWriter = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, ["documents:write"], cancellationToken));
        using var receiverReader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, ["documents:read"], cancellationToken));
        using var sourceWriter = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        var collectionPath = new Uri($"{tenant.Path("repair")}/collections/items", UriKind.Relative);
        foreach (var writer in new[] { receiverWriter, sourceWriter })
        {
            using var created = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative), new { name = "items" }, cancellationToken);
            created.EnsureSuccessStatusCode();
        }

        var documentPath = Document(tenant, DocumentApi.NewId());
        using (var written = await receiverWriter.PutAsJsonAsync(documentPath, new { name = new Faker().Name.FullName() }, cancellationToken))
        {
            written.EnsureSuccessStatusCode();
        }

        using (var written = await sourceWriter.PutAsJsonAsync(documentPath, new { name = new Faker().Name.FullName() }, cancellationToken))
        {
            written.EnsureSuccessStatusCode();
        }

        using (var dropped = await sourceWriter.DeleteAsync(collectionPath, cancellationToken))
        {
            dropped.EnsureSuccessStatusCode();
        }

        var paths = new[] { documentPath, new Uri($"{collectionPath}/documents", UriKind.Relative) };
        foreach (var path in paths)
        {
            using var warm = await receiverReader.GetAsync(path, cancellationToken);
            warm.EnsureSuccessStatusCode();
        }

        var store = (await source.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("repair"), cancellationToken))!;
        DocumentChange drop;
        await using (var read = await store.BeginTransactionAsync(write: false, cancellationToken))
        {
            var version = (await read.GetVersionAsync(DocumentChange.Collections, "items", cancellationToken))!.Value;
            await Assert.That(version.Deleted).IsTrue();
            drop = new DocumentChange(DocumentChange.Collections, "items", version.Version, null);
        }

        using var channel = peerTls.CreateChannel(receiver.PeerAddress!);
        var client = new AntiEntropy.AntiEntropyClient(channel);
        using var merge = client.MergeAsync(new StateRequest
        {
            Identity = await ProofIdentityAsync(source, receiver, tenant, cancellationToken),
            Envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(ChangeGroup.Encode([drop])).Span),
        }, cancellationToken: cancellationToken);
        await Assert.That((await merge.ResponseAsync).RepairedKeys).IsEqualTo(1U);
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
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, token);
        await TerraBaseTestServer.ReplicatedAsync(source);
        var database = tenant.Database("repair");
        async Task<long> BytesAsync(TerraBaseTestServer node, string key)
        {
            var window = new TerraBase.Features.Management.Usage.UsageWindow(
                TerraBase.Features.Management.Usage.UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1);
            var (lifetime, _) = await node.Services.GetRequiredService<TerraBase.Features.Management.Usage.UsageMeter>().GetObservationsAsync(database, window, token);
            return lifetime.Meters?.GetValueOrDefault(key)?.Count ?? 0;
        }

        // Before the repair only replication handshakes were charged as anti-entropy.
        var before = await BytesAsync(source, "replication_bytes|sfo-1|gru-1|anti_entropy:sent");
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(tenant.File(receiver, "repair"))))
        {
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE documents SET version = 1, body = '{\"name\":\"stale\"}' WHERE collection = 'items' AND id = $id;";
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
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, token);
        await TerraBaseTestServer.ReplicatedAsync(source);
        var database = tenant.Database("repair");
        async Task<long> BytesAsync(TerraBaseTestServer node, string key)
        {
            var window = new TerraBase.Features.Management.Usage.UsageWindow(
                TerraBase.Features.Management.Usage.UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1);
            var (lifetime, _) = await node.Services.GetRequiredService<TerraBase.Features.Management.Usage.UsageMeter>().GetObservationsAsync(database, window, token);
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
            corrupt.CommandText = "UPDATE documents SET body = '{\"payload\":\"' || replace(hex(zeroblob(1048577)), '00', 'x') || '\"}' WHERE collection = 'items' AND id = $id;";
            corrupt.Parameters.AddWithValue("$id", ids[0]);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(1);
        }

        clock.Advance(AntiEntropyWorker.ReconcileInterval);
        await TerraBaseTestServer.EventuallyAsync(async _ => await BytesAsync(receiver, "replication_bytes|gru-1|sfo-1|anti_entropy:received") > before);
        await clock.PausedAsync(token);
        await Assert.That(await BytesAsync(source, "replication_bytes|sfo-1|gru-1|anti_entropy:sent"))
            .IsEqualTo(await BytesAsync(receiver, "replication_bytes|gru-1|sfo-1|anti_entropy:received"));
    }

    // Answers the first digest without its applied source, which the requester refuses after reading the region it names.
    private sealed class InvalidFirstDigest : Interceptor
    {
        private int _digests;

        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            var response = await continuation(request, context);
            if (response is MerkleDigest digest && Interlocked.Increment(ref _digests) == 1)
            {
                digest.AppliedSource = null;
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

        // Valid enough to be served, then refused by the repair state: it still crossed the wire.
        var merge = new StateRequest { Identity = identity, Envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(ChangeGroup.Encode([])).Span) };
        using (var mergeCall = client.MergeAsync(merge, cancellationToken: cancellationToken))
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await mergeCall.ResponseAsync);
            await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }

        var window = new TerraBase.Features.Management.Usage.UsageWindow(
            TerraBase.Features.Management.Usage.UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1);
        var (lifetime, _) = await server.Services.GetRequiredService<TerraBase.Features.Management.Usage.UsageMeter>()
            .GetObservationsAsync(database, window, cancellationToken);
        await Assert.That(lifetime.Meters!["replication_bytes|gru-1|sfo-1|anti_entropy:received"].Count).IsEqualTo(digestBytes + merge.CalculateSize() + 5L);
    }

    [Test]
    [Timeout(60_000)]
    public async Task Dropped_collection_deletion_evidence_repairs_a_resurrected_row_without_recreating_the_collection(CancellationToken cancellationToken)
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
        using (var dropped = await writer.DeleteAsync(new Uri($"{tenant.Path("repair")}/collections/items", UriKind.Relative), token))
        {
            dropped.EnsureSuccessStatusCode();
        }

        await TerraBaseTestServer.ReplicatedAsync(source);
        var sourceFile = tenant.File(source, "repair");
        var receiverFile = tenant.File(receiver, "repair");
        var expected = (await StateAsync(receiverFile, ids[0], token))!.Value;
        await Assert.That(expected.Body).IsNull();
        // Deletion can be represented by physical absence or its exact retained tombstone.
        var initialSource = await StateAsync(sourceFile, ids[0], token);
        await Assert.That(initialSource is null || initialSource == expected).IsTrue();
        var sourceFrontiers = await FrontiersAsync(sourceFile, token);
        var receiverFrontiers = await FrontiersAsync(receiverFile, token);
        using var repairs = new RepairCounter(receiver);
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(receiverFile)))
        {
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE documents SET version = 1, body = '{\"name\":\"resurrected\"}' WHERE collection = 'items' AND id = $id;";
            corrupt.Parameters.AddWithValue("$id", ids[0]);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(1);
        }

        // The observed startup pause is still pending because this clock has not advanced.
        clock.Advance(AntiEntropyWorker.ReconcileInterval);
        await TerraBaseTestServer.EventuallyAsync(async checkToken =>
            await StateAsync(receiverFile, ids[0], checkToken) == expected && repairs.Keys >= 1);
        await clock.PausedAsync(token);
        await Assert.That(repairs.Keys).IsEqualTo(1);
        // Both digest scans restore their own retained deletions before an identical replay begins.
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

        // Chaos bypass: leave the durable ACKs, journal and replication clock untouched.
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(damagedFile)))
        {
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                DELETE FROM documents WHERE collection = 'items' AND id = $missing;
                UPDATE documents SET version = 1, body = '{"name":"stale"}' WHERE collection = 'items' AND id = $stale;
                DELETE FROM documents WHERE collection = 'items' AND id = $deleted AND NOT $resurrect;
                UPDATE documents SET version = 1, body = '{"name":"resurrected"}'
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
        // A reconnect temporarily discards unverified ACKs; wait for their journal proofs again.
        Volatile.Write(ref phase, "ordinary replication catch-up");
        await TerraBaseTestServer.ReplicatedAsync(source);
        Volatile.Write(ref phase, "frontier assertions");
        await Assert.That(await FrontiersAsync(sourceFile, token)).IsEquivalentTo(sourceFrontiers);
        await Assert.That(await FrontiersAsync(receiverFile, token)).IsEquivalentTo(receiverFrontiers);
    }

    [Test]
    [Timeout(60_000)]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    [Arguments(false, null, false)]
    [Arguments(true, null, false)]
    [Arguments(true, null, true)]
    public async Task Body_only_corruption_restores_retained_bytes_without_poisoning_an_unchanged_peer(
        bool faultOnSource, bool? greaterBytes, bool faultOnBoth, CancellationToken cancellationToken)
    {
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
        await clock.PausedAsync(cancellationToken);
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken);
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, cancellationToken);
        await TerraBaseTestServer.ReplicatedAsync(source);
        // Canonicalize SeedAsync's physical deletion before measuring the single damaged live key.
        await ReplayIdenticalAsync(peerTls, source, receiver, tenant, cancellationToken);
        var sourceFile = tenant.File(source, "repair");
        var receiverFile = tenant.File(receiver, "repair");
        var expected = (await StateAsync(sourceFile, ids[0], cancellationToken))!.Value;
        await Assert.That(await StateAsync(receiverFile, ids[0], cancellationToken)).IsEqualTo(expected);
        var corrupted = greaterBytes switch { true => "{\"z\":true}", false => "{\"a\":true}", null => null };
        if (corrupted is not null)
        {
            var order = System.Text.Encoding.UTF8.GetBytes(corrupted).AsSpan()
                .SequenceCompareTo(System.Text.Encoding.UTF8.GetBytes(expected.Body!));
            await Assert.That(greaterBytes == true ? order > 0 : order < 0).IsTrue();
        }
        var sourceFrontiers = await FrontiersAsync(sourceFile, cancellationToken);
        var receiverFrontiers = await FrontiersAsync(receiverFile, cancellationToken);
        var sourceHistory = await RetainedHistoryAsync(sourceFile, ids[0], cancellationToken);
        var receiverHistory = await RetainedHistoryAsync(receiverFile, ids[0], cancellationToken);
        using var sourceRepairs = new RepairCounter(source);
        using var receiverRepairs = new RepairCounter(receiver);
        using var sourceReader = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:read"], cancellationToken));
        using var receiverReader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, ["documents:read"], cancellationToken));
        foreach (var node in new[] { source, receiver })
        {
            if (!faultOnBoth && ReferenceEquals(node, source) != faultOnSource)
            {
                continue;
            }

            await using var connection = new SqliteConnection(DocumentApi.ConnectionString(tenant.File(node, "repair")));
            await connection.OpenAsync(cancellationToken);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE documents SET body = $body WHERE collection = 'items' AND id = $id;";
            corrupt.Parameters.AddWithValue("$body", (object?)corrupted ?? DBNull.Value);
            corrupt.Parameters.AddWithValue("$id", ids[0]);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(cancellationToken)).IsEqualTo(1);
            await Assert.That(await StateAsync(tenant.File(node, "repair"), ids[0], cancellationToken)).IsEqualTo((expected.Version, corrupted));
            // A faulty live body is cached too; NULL corruption must appear as a false deletion.
            await Assert.That(await MatchesAsync(ReferenceEquals(node, source) ? sourceReader : receiverReader,
                Document(tenant, ids[0]), corrupted, cancellationToken)).IsTrue();
        }

        clock.Advance(AntiEntropyWorker.ReconcileInterval);
        await clock.PausedAsync(cancellationToken);
        await Assert.That(await StateAsync(sourceFile, ids[0], cancellationToken)).IsEqualTo(expected);
        await Assert.That(await StateAsync(receiverFile, ids[0], cancellationToken)).IsEqualTo(expected);
        await Assert.That(await MatchesAsync(sourceReader, Document(tenant, ids[0]), expected.Body, cancellationToken)).IsTrue();
        await Assert.That(await MatchesAsync(receiverReader, Document(tenant, ids[0]), expected.Body, cancellationToken)).IsTrue();
        await Assert.That(sourceRepairs.Keys).IsEqualTo(faultOnBoth || faultOnSource ? 1L : 0L);
        await Assert.That(receiverRepairs.Keys).IsEqualTo(faultOnBoth || !faultOnSource ? 1L : 0L);

        await ReplayIdenticalAsync(peerTls, source, receiver, tenant, cancellationToken);
        await ReplayIdenticalAsync(peerTls, receiver, source, tenant, cancellationToken);
        await Assert.That(sourceRepairs.Keys).IsEqualTo(faultOnBoth || faultOnSource ? 1L : 0L);
        await Assert.That(receiverRepairs.Keys).IsEqualTo(faultOnBoth || !faultOnSource ? 1L : 0L);
        await Assert.That(await FrontiersAsync(sourceFile, cancellationToken)).IsEquivalentTo(sourceFrontiers);
        await Assert.That(await FrontiersAsync(receiverFile, cancellationToken)).IsEquivalentTo(receiverFrontiers);
        await Assert.That(await RetainedHistoryAsync(sourceFile, ids[0], cancellationToken)).IsEquivalentTo(sourceHistory);
        await Assert.That(await RetainedHistoryAsync(receiverFile, ids[0], cancellationToken)).IsEquivalentTo(receiverHistory);
    }

    [Test]
    [Timeout(60_000)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Repair_merge_restores_the_latest_imported_equal_version_winner_before_another_digest(
        bool greaterFirst, CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); },
            services => services.AddSingleton<TimeProvider>(clock));
        var tenant = await ReplicationTenant.CreateAsync(server, ["repair"], cancellationToken);
        using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, ["documents:write"], cancellationToken));
        using var reader = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, ["documents:read"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, cancellationToken);
        var file = tenant.File(server, "repair");
        var original = (await StateAsync(file, ids[0], cancellationToken))!.Value;
        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var repair = new AntiEntropy.AntiEntropyClient(channel);
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = tenant.Database("repair").DatabaseId! };
        using (var call = repair.DigestAsync(identity, cancellationToken: cancellationToken))
        {
            var digest = await call.ResponseAsync;
            identity.Incarnation = digest.Incarnation;
            identity.Origin = ByteString.CopyFrom(EntityIds.New().ToByteArray());
            identity.SourceProof = new JournalProof { Origin = identity.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(identity.Origin.Span)) };
            identity.TargetProof = new JournalProof { Origin = digest.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(digest.Origin.Span)) };
        }

        const string winner = "{\"z\":true}";
        // Repeated keys must preserve a winner pending this commit, before retained history includes it.
        await Assert.That(await MergeAsync(greaterFirst ? [winner, original.Body!] : [original.Body!, winner])).IsEqualTo(1U);
        await Assert.That(await MergeAsync(original.Body!)).IsEqualTo(0U);
        await Assert.That(await StateAsync(file, ids[0], cancellationToken)).IsEqualTo((original.Version, (string?)winner));
        var frontiers = await FrontiersAsync(file, cancellationToken);
        var history = await RetainedHistoryAsync(file, ids[0], cancellationToken);
        using var repairs = new RepairCounter(server);
        const string corrupted = "{\"zz\":true}";
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(file)))
        {
            await connection.OpenAsync(cancellationToken);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE documents SET body = $body WHERE collection = 'items' AND id = $id;";
            corrupt.Parameters.AddWithValue("$body", corrupted);
            corrupt.Parameters.AddWithValue("$id", ids[0]);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(cancellationToken)).IsEqualTo(1);
        }

        await Assert.That(await MatchesAsync(reader, Document(tenant, ids[0]), corrupted, cancellationToken)).IsTrue();
        // Repair must use the latest accepted history, even though the damaged bytes sort above it.
        await Assert.That(await MergeAsync(winner)).IsEqualTo(1U);
        await Assert.That(await StateAsync(file, ids[0], cancellationToken)).IsEqualTo((original.Version, (string?)winner));
        await Assert.That(await MatchesAsync(reader, Document(tenant, ids[0]), winner, cancellationToken)).IsTrue();
        await Assert.That(await MergeAsync(winner)).IsEqualTo(0U);
        await Assert.That(await MergeAsync(original.Body!)).IsEqualTo(0U);
        using (var call = repair.DigestAsync(identity, cancellationToken: cancellationToken))
        {
            _ = await call.ResponseAsync;
        }

        await Assert.That(repairs.Keys).IsEqualTo(1L);
        await Assert.That(await FrontiersAsync(file, cancellationToken)).IsEquivalentTo(frontiers);
        await Assert.That(await RetainedHistoryAsync(file, ids[0], cancellationToken)).IsEquivalentTo(history);

        async Task<uint> MergeAsync(params string[] bodies)
        {
            var changes = bodies.Select(body => new DocumentChange("items", ids[0], original.Version, System.Text.Encoding.UTF8.GetBytes(body))).ToArray();
            using var call = repair.MergeAsync(new StateRequest
            {
                Identity = identity,
                Envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(ChangeGroup.Encode(changes)).Span),
            }, cancellationToken: cancellationToken);
            return (await call.ResponseAsync).RepairedKeys;
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task Retained_journal_deletion_fences_misleading_live_history_during_digest_and_merge(CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); },
            services => services.AddSingleton<TimeProvider>(clock));
        var tenant = await ReplicationTenant.CreateAsync(server, ["repair"], cancellationToken);
        using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, ["documents:write"], cancellationToken));
        using var reader = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, ["documents:read"], cancellationToken));
        var ids = await SeedAsync(writer, tenant, cancellationToken);
        var file = tenant.File(server, "repair");
        var live = (await StateAsync(file, ids[0], cancellationToken))!.Value;
        var version = await TransactAsync(writer, tenant, [new { op = "delete", collection = "items", id = ids[0] }], cancellationToken);
        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var repair = new AntiEntropy.AntiEntropyClient(channel);
        var identity = await ProofIdentityAsync(server, server, tenant, cancellationToken);
        ByteString expectedDigest;
        using (var call = repair.DigestAsync(identity, cancellationToken: cancellationToken))
        {
            expectedDigest = (await call.ResponseAsync).Envelope;
        }

        var deleted = (version, (string?)null);
        await Assert.That(await StateAsync(file, ids[0], cancellationToken)).IsEqualTo(deleted);
        // Public transactions reject repeated keys. Make the earlier live history appear tied with
        // the deletion, then lose only history tombstones while preserving the real journal proof.
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(file)))
        {
            await connection.OpenAsync(cancellationToken);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                DELETE FROM change_log WHERE collection = 'items' AND id = $id AND body IS NULL;
                UPDATE change_log SET document_version = $version WHERE collection = 'items' AND id = $id AND body IS NOT NULL;
                """;
            corrupt.Parameters.AddWithValue("$id", ids[0]);
            corrupt.Parameters.AddWithValue("$version", version);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(cancellationToken)).IsGreaterThanOrEqualTo(2);
            using var retained = connection.CreateCommand();
            retained.CommandText = "SELECT max(version) FROM changes WHERE collection = 'items' AND id = $id AND body IS NULL;";
            retained.Parameters.AddWithValue("$id", ids[0]);
            await Assert.That(Convert.ToInt64(await retained.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)).IsEqualTo(version);
        }

        var frontiers = await FrontiersAsync(file, cancellationToken);
        var history = await RetainedHistoryAsync(file, ids[0], cancellationToken);
        using var repairs = new RepairCounter(server);
        using (var call = repair.DigestAsync(identity, cancellationToken: cancellationToken))
        {
            await Assert.That((await call.ResponseAsync).Envelope).IsEqualTo(expectedDigest);
        }

        var change = new DocumentChange("items", ids[0], version, System.Text.Encoding.UTF8.GetBytes(live.Body!));
        using (var call = repair.MergeAsync(new StateRequest
        {
            Identity = identity,
            Envelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(ChangeGroup.Encode([change])).Span),
        }, cancellationToken: cancellationToken))
        {
            await Assert.That((await call.ResponseAsync).RepairedKeys).IsEqualTo(0U);
        }

        await Assert.That(await MatchesAsync(reader, Document(tenant, ids[0]), null, cancellationToken)).IsTrue();
        await Assert.That(await StateAsync(file, ids[0], cancellationToken)).IsEqualTo(deleted);
        await Assert.That(repairs.Keys).IsEqualTo(0L);
        await Assert.That(await FrontiersAsync(file, cancellationToken)).IsEquivalentTo(frontiers);
        await Assert.That(await RetainedHistoryAsync(file, ids[0], cancellationToken)).IsEquivalentTo(history);
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
    [Timeout(60_000)]
    public async Task Identical_resurrections_on_both_members_repair_locally_before_their_digests_match(CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var source = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); },
            services => services.AddSingleton<TimeProvider>(clock));
        await using var receiver = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); },
            services => services.AddSingleton<TimeProvider>(clock));
        var tenant = await ReplicationTenant.CreateAsync(source, ["repair"], cancellationToken);
        await tenant.BindAsync(receiver);
        var ids = Enumerable.Range(0, 3).Select(_ => DocumentApi.NewId()).ToArray();
        var name = new Faker().Name.FullName();
        using (var writer = clientTls.CreateClient(source.ClientAddress!, await tenant.KeyAsync(source, ["documents:write"], cancellationToken)))
        using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative),
            new { name = "items" }, cancellationToken))
        {
            collection.EnsureSuccessStatusCode();
        }

        using var sourceChannel = peerTls.CreateChannel(source.PeerAddress!);
        using var receiverChannel = peerTls.CreateChannel(receiver.PeerAddress!);
        var sourceRpc = new AntiEntropy.AntiEntropyClient(sourceChannel);
        var receiverRpc = new AntiEntropy.AntiEntropyClient(receiverChannel);
        // Share the generated collection identity before writing identical document histories.
        using (var seed = sourceRpc.ReadRangeAsync(new RangeRequest
        {
            Identity = await ProofIdentityAsync(receiver, source, tenant, cancellationToken),
            Ranges = ushort.MaxValue,
        }, cancellationToken: cancellationToken))
        using (var imported = receiverRpc.MergeAsync(new StateRequest
        {
            Identity = await ProofIdentityAsync(source, receiver, tenant, cancellationToken),
            Envelope = (await seed.ResponseAsync).Envelope,
        }, cancellationToken: cancellationToken))
        {
            await Assert.That((await imported.ResponseAsync).RepairedKeys).IsEqualTo(1U);
        }

        long deletionVersion = 0;
        foreach (var node in new[] { source, receiver })
        {
            using var writer = clientTls.CreateClient(node.ClientAddress!, await tenant.KeyAsync(node, ["documents:write"], cancellationToken));
            _ = await TransactAsync(writer, tenant, ids.Select(id => (object)new { op = "create", collection = "items", id, document = new { name } }), cancellationToken);
            var version = await TransactAsync(writer, tenant, ids.Select(id => (object)new { op = "delete", collection = "items", id }), cancellationToken);
            if (deletionVersion == 0)
            {
                deletionVersion = version;
            }

            await Assert.That(version).IsEqualTo(deletionVersion);
            using var newer = await writer.PutAsJsonAsync(Document(tenant, ids[^1]), new { name }, cancellationToken);
            newer.EnsureSuccessStatusCode();
        }

        // Equal clocks and shared metadata produce identical digests before injecting the fault.
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = tenant.Database("repair").DatabaseId! };
        using (var sourceDigest = sourceRpc.DigestAsync(identity, cancellationToken: cancellationToken))
        using (var receiverDigest = receiverRpc.DigestAsync(identity, cancellationToken: cancellationToken))
        {
            await Assert.That((await sourceDigest.ResponseAsync).Envelope).IsEqualTo((await receiverDigest.ResponseAsync).Envelope);
        }

        var sourceFile = tenant.File(source, "repair");
        var receiverFile = tenant.File(receiver, "repair");
        var sourceFrontiers = await FrontiersAsync(sourceFile, cancellationToken);
        var receiverFrontiers = await FrontiersAsync(receiverFile, cancellationToken);
        var newerState = (await StateAsync(sourceFile, ids[^1], cancellationToken))!.Value;
        await Assert.That(newerState.Version).IsGreaterThan(deletionVersion);
        await Assert.That(newerState.Body).IsNotNull();
        await Assert.That(await StateAsync(receiverFile, ids[^1], cancellationToken)).IsEqualTo(newerState);
        using var sourceRepairs = new RepairCounter(source);
        using var receiverRepairs = new RepairCounter(receiver);
        foreach (var file in new[] { sourceFile, receiverFile })
        {
            await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file));
            await connection.OpenAsync(cancellationToken);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                UPDATE documents SET version = $version - 1, body = '{"name":"resurrected"}' WHERE collection = 'items' AND id = $older;
                UPDATE documents SET version = $version, body = '{"name":"resurrected"}' WHERE collection = 'items' AND id = $equal;
                """;
            corrupt.Parameters.AddWithValue("$version", deletionVersion);
            corrupt.Parameters.AddWithValue("$older", ids[0]);
            corrupt.Parameters.AddWithValue("$equal", ids[1]);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(cancellationToken)).IsEqualTo(2);
        }

        // Matching corrupt replicas have no divergent range to exchange: each digest must commit its own deletion evidence.
        using (var sourceDigest = sourceRpc.DigestAsync(identity, cancellationToken: cancellationToken))
        using (var receiverDigest = receiverRpc.DigestAsync(identity, cancellationToken: cancellationToken))
        {
            await Assert.That((await sourceDigest.ResponseAsync).Envelope).IsEqualTo((await receiverDigest.ResponseAsync).Envelope);
        }

        foreach (var node in new[] { source, receiver })
        {
            var file = tenant.File(node, "repair");
            using var reader = clientTls.CreateClient(node.ClientAddress!, await tenant.KeyAsync(node, ["documents:read"], cancellationToken));
            foreach (var id in ids.Take(2))
            {
                await Assert.That(await StateAsync(file, id, cancellationToken)).IsEqualTo((deletionVersion, (string?)null));
                await Assert.That(await MatchesAsync(reader, Document(tenant, id), null, cancellationToken)).IsTrue();
            }

            await Assert.That(await StateAsync(file, ids[^1], cancellationToken)).IsEqualTo(newerState);
            await Assert.That(await MatchesAsync(reader, Document(tenant, ids[^1]), newerState.Body, cancellationToken)).IsTrue();
        }

        await Assert.That(sourceRepairs.Keys).IsEqualTo(2);
        await Assert.That(receiverRepairs.Keys).IsEqualTo(2);
        await ReplayIdenticalAsync(peerTls, source, receiver, tenant, cancellationToken);
        await Assert.That(sourceRepairs.Keys).IsEqualTo(2);
        await Assert.That(receiverRepairs.Keys).IsEqualTo(2);
        await Assert.That(await FrontiersAsync(sourceFile, cancellationToken)).IsEquivalentTo(sourceFrontiers);
        await Assert.That(await FrontiersAsync(receiverFile, cancellationToken)).IsEquivalentTo(receiverFrontiers);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Repeated_deletion_history_returns_each_key_once_across_bounded_range_pages(CancellationToken cancellationToken)
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
        var firstDeletion = await TransactAsync(writer, tenant, ids.Select(id => (object)new { op = "delete", collection = "items", id }), cancellationToken);
        var latestDeletion = firstDeletion;
        var repeated = ids.Take(3).ToArray();
        for (var i = 0; i < 16; i++)
        {
            _ = await TransactAsync(writer, tenant, repeated.Select(id => (object)new { op = "create", collection = "items", id, document = new { name } }), cancellationToken);
            latestDeletion = await TransactAsync(writer, tenant, repeated.Select(id => (object)new { op = "delete", collection = "items", id }), cancellationToken);
        }

        using (var newer = await writer.PutAsJsonAsync(Document(tenant, ids[2]), new { name }, cancellationToken))
        {
            newer.EnsureSuccessStatusCode();
        }

        var file = tenant.File(server, "repair");
        var live = (await StateAsync(file, ids[2], cancellationToken))!.Value;
        await Assert.That(live.Version).IsGreaterThan(latestDeletion);
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(file, readOnly: true)))
        {
            await connection.OpenAsync(cancellationToken);
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM changes WHERE collection = 'items' AND body IS NULL;";
            await Assert.That(Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)).IsEqualTo(178);
        }

        var frontiers = await FrontiersAsync(file, cancellationToken);
        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var client = new AntiEntropy.AntiEntropyClient(channel);
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = tenant.Database("repair").DatabaseId! };
        using var digestCall = client.DigestAsync(identity, cancellationToken: cancellationToken);
        var digest = await digestCall.ResponseAsync;
        identity.Incarnation = digest.Incarnation;
        identity.Origin = ByteString.CopyFrom(EntityIds.New().ToByteArray());
        identity.SourceProof = new JournalProof { Origin = identity.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(identity.Origin.Span)) };
        identity.TargetProof = new JournalProof { Origin = digest.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(digest.Origin.Span)) };
        var request = new RangeRequest { Identity = identity, Ranges = ushort.MaxValue };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        var keys = 0;
        while (true)
        {
            using var readCall = client.ReadRangeAsync(request, cancellationToken: cancellationToken);
            var page = await readCall.ResponseAsync;
            var changes = ChangeGroup.Decode(ApplicationEnvelope.Decode(page.Envelope.Span, ReplicationProtocol.MaxApplicationBytes));
            await Assert.That(changes.Count <= 128).IsTrue();
            await Assert.That(ChangeGroup.ContentBytes(changes) <= IStorageTransaction.MaxChangeGroupBytes).IsTrue();
            pages++;
            keys += changes.Count;
            foreach (var change in changes.Where(change => change.Collection == "items"))
            {
                await Assert.That(seen.Add(change.Id)).IsTrue();
                var expected = change.Id == ids[2] ? live : (repeated.Contains(change.Id, StringComparer.Ordinal) ? latestDeletion : firstDeletion, (string?)null);
                await Assert.That((change.Version, change.Json is { } json ? System.Text.Encoding.UTF8.GetString(json.Span) : null)).IsEqualTo(expected);
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

    private static async Task<string[]> RetainedHistoryAsync(string file, string id, CancellationToken token)
    {
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file, readOnly: true));
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'clock:' || value FROM commit_offset
            UNION ALL
            SELECT json_object('sequence', sequence, 'generation', generation, 'offset', version,
                'version', document_version, 'body', body)
            FROM change_log WHERE collection = 'items' AND id = $id
            UNION ALL
            SELECT json_object('journal', sequence, 'ordinal', ordinal, 'version', version, 'body', body)
            FROM changes WHERE collection = 'items' AND id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var rows = await command.ExecuteReaderAsync(token);
        var history = new List<string>();
        while (await rows.ReadAsync(token))
        {
            history.Add(rows.GetString(0));
        }

        return [.. history];
    }

    private static async Task<string[]> FrontiersAsync(string file, CancellationToken token)
    {
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file, readOnly: true));
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'journal:' || sequence || ':' || hex(digest) FROM journal_prefixes
            UNION ALL SELECT 'applied:' || hex(origin) || ':' || sequence || ':' || hex(digest) FROM applied;
            """;
        await using var rows = await command.ExecuteReaderAsync(token);
        var frontiers = new List<string>();
        while (await rows.ReadAsync(token))
        {
            frontiers.Add(rows.GetString(0));
        }

        return [.. frontiers];
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
        var target = await ProofIdentityAsync(from, to, tenant, token);
        identity = await ProofIdentityAsync(to, from, tenant, token);
        var request = new RangeRequest { Identity = identity, Ranges = ushort.MaxValue };
        var replayed = false;
        do
        {
            using var readCall = fromClient.ReadRangeAsync(request, cancellationToken: token);
            var page = await readCall.ResponseAsync;
            if (!page.Envelope.IsEmpty)
            {
                using var mergeCall = toClient.MergeAsync(new StateRequest { Identity = target, Envelope = page.Envelope }, cancellationToken: token);
                var changed = (await mergeCall.ResponseAsync).RepairedKeys;
                await Assert.That(changed).IsEqualTo(0U);
                replayed = true;
            }

            if (!page.HasMore)
            {
                break;
            }

            request.AfterCollection = page.AfterCollection;
            request.AfterId = page.AfterId;
        } while (true);

        await Assert.That(replayed).IsTrue();
    }

    private static async Task<ShardIdentity> ProofIdentityAsync(
        TerraBaseTestServer initiator, TerraBaseTestServer target, ReplicationTenant tenant, CancellationToken token)
    {
        var database = tenant.Database("repair");
        var sourceStore = (await initiator.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token))!;
        var targetStore = (await target.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token))!;
        await using var sourceRead = await sourceStore.BeginTransactionAsync(write: false, token);
        await using var targetRead = await targetStore.BeginTransactionAsync(write: false, token);
        var sourceOrigin = await sourceRead.GetOriginAsync(token);
        var targetOrigin = await targetRead.GetOriginAsync(token);
        var (sourceSequence, sourceDigest) = await targetRead.GetAppliedFrontierAsync(sourceOrigin, token);
        var (targetSequence, targetDigest) = await sourceRead.GetAppliedFrontierAsync(targetOrigin, token);
        var localDigest = await sourceRead.GetChangeDigestAsync(sourceSequence, token);
        await Assert.That(localDigest is not null && localDigest.Value.Span.SequenceEqual(sourceDigest.Span)).IsTrue();
        return new ShardIdentity
        {
            Organization = tenant.OrganizationId,
            Project = tenant.ProjectId,
            Database = database.DatabaseId!,
            Incarnation = ByteString.CopyFrom(targetStore.Incarnation.ToByteArray()),
            Origin = ByteString.CopyFrom(sourceOrigin.Span),
            SourceProof = new JournalProof
            {
                Origin = ByteString.CopyFrom(sourceOrigin.Span),
                Sequence = checked((ulong)sourceSequence),
                Digest = ByteString.CopyFrom(sourceDigest.Span),
            },
            TargetProof = new JournalProof
            {
                Origin = ByteString.CopyFrom(targetOrigin.Span),
                Sequence = checked((ulong)targetSequence),
                Digest = ByteString.CopyFrom(targetDigest.Span),
            },
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
