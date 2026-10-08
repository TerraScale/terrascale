using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Drives a node's Sync receiver directly, acting as a peer origin with crafted change groups.</summary>
public sealed class SyncProtocolTests
{
    private static readonly Faker Faker = new();

    [Test]
    [Arguments("empty")]
    [Arguments("malformed")]
    [Arguments("utf8")]
    [Arguments("array")]
    [Arguments("duplicate")]
    public async Task An_older_duplicate_with_an_inadmissible_body_is_refused_without_an_ACK(string invalid)
    {
        await using var node = await Node.StartAsync("duplicates");
        var database = node.Tenant.Database("duplicates");
        var origin = RandomNumberGenerator.GetBytes(16);
        await ApplyAsync(node, "duplicates", origin, 1, Put("items", "first", 1));
        await ApplyAsync(node, "duplicates", origin, 2, Put("items", "second", 2));
        byte[] body = invalid switch
        {
            "empty" => [],
            "malformed" => "{"u8.ToArray(),
            "utf8" => [(byte)'{', (byte)'"', (byte)'x', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}'],
            "array" => "[]"u8.ToArray(),
            "duplicate" => "{\"x\":1,\"x\":2}"u8.ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };
        using var call = node.Sync();
        var welcome = await HelloAsync(call, database, origin);
        var frontier = welcome.Frontiers.Single();
        await Assert.That(frontier.DurableSequence).IsEqualTo(2UL);
        // Sequence 1 is older than the current prefix. Its digest is self-consistent, but its body is invalid.
        await SendAsync(call, Batch(database, origin, 1, [new DocumentChange("items", "poison", 1, body)]));
        var error = (await NextAsync(call)).Error;
        await Assert.That(error.Code).IsEqualTo(StreamErrorCode.StreamErrorInvalidBatch);
        await Assert.That(error.Retryable).IsFalse();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var store = (await node.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
        var (sequence, digest) = await read.GetAppliedFrontierAsync(origin, deadline.Token);
        await Assert.That(sequence).IsEqualTo(2);
        await Assert.That(digest.Span.SequenceEqual(frontier.JournalDigest.Span)).IsTrue();
        await Assert.That(await read.GetAsync("items", "poison", deadline.Token)).IsNull();
        await Assert.That(await read.GetAsync("items", "first", deadline.Token)).IsNotNull();
        await Assert.That(await read.GetAsync("items", "second", deadline.Token)).IsNotNull();
    }

    [Test]
    public async Task Equal_project_and_database_UUIDs_in_different_organizations_keep_separate_files_and_applied_frontiers()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var channel = tls.CreateChannel(server.PeerAddress!);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var first = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby,
            EntityIds.New().ToString(), EntityIds.New().ToString());
        var second = first with { OrganizationId = EntityIds.New().ToString() };
        TenantContext[] databases = [await server.RegisterAsync(first), await server.RegisterAsync(second)];
        var origin = RandomNumberGenerator.GetBytes(16);
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var stores = new List<IDatabaseStore>();
        foreach (var database in databases)
        {
            using var call = client.Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            var welcome = await HelloAsync(call, database, origin);
            await Assert.That(welcome.Frontiers.Single().HasDurableSequence).IsFalse();
            await Assert.That(welcome.LocalIdentity.TenantId.Span.SequenceEqual(Ulid.Parse(database.OrganizationId).ToByteArray())).IsTrue();
            await SendAsync(call, Batch(database, origin, 1, [Put("items", "same", 1, database.OrganizationId)]));
            await Assert.That((await NextAsync(call)).Durable.DurablePrefix).IsEqualTo(1UL);
            await Assert.That((await NextAsync(call)).Applied.AppliedPrefix).IsEqualTo(1UL);
            var store = (await engine.OpenDatabaseAsync(database, deadline.Token))!;
            stores.Add(store);
            await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
            await Assert.That(await read.GetAppliedSequenceAsync(origin, deadline.Token)).IsEqualTo(1);
            await Assert.That(File.Exists(Path.Combine(server.DataDirectory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db"))).IsTrue();
        }

        await Assert.That(stores[0].Incarnation).IsNotEqualTo(stores[1].Incarnation);
        foreach (var (store, database) in stores.Zip(databases))
        {
            await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
            var document = (await read.GetAsync("items", "same", deadline.Token))!;
            using var json = JsonDocument.Parse(document.Json);
            await Assert.That(json.RootElement.GetProperty("text").GetString()).IsEqualTo(database.OrganizationId);
            await Assert.That(await read.GetAppliedSequenceAsync(origin, deadline.Token)).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("duplicate")]
    [Arguments("escapedDuplicate")]
    [Arguments("surrogateValue")]
    [Arguments("surrogateKey")]
    [Arguments("depth")]
    [Arguments("nodes")]
    [Arguments("number")]
    [Arguments("oversized")]
    [Arguments("notObject")]
    [Arguments("validBoundary")]
    public async Task Replicated_JSON_obeys_the_same_admission_as_HTTPS_writes(string scenario)
    {
        await using var node = await Node.StartAsync("json");
        using var writer = await node.ClientAsync("documents:write");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var id = EntityIds.New().ToString();
        var json = scenario switch
        {
            "duplicate" => """{"x":1,"x":2}""",
            "escapedDuplicate" => """{"x":1,"\u0078":2}""",
            "surrogateValue" => """{"x":"\ud800"}""",
            "surrogateKey" => """{"\ud800":1}""",
            "depth" => "{\"x\":" + new string('[', 17) + "0" + new string(']', 17) + "}",
            "nodes" => "{\"x\":[" + string.Join(',', Enumerable.Repeat("0", 4096)) + "]}",
            "number" => """{"x":1e308}""",
            "oversized" => "{\"x\":\"" + new string('a', RequestJson.MaxDocumentBytes) + "\"}",
            "notObject" => "[1,2]",
            "validBoundary" => "{\"x\":\"" + new string('a', RequestJson.MaxDocumentBytes - 8) + "\"}",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var bytes = Encoding.UTF8.GetBytes(json);
        using (var created = await writer.PutAsync(new Uri($"{node.Tenant.Path("json")}", UriKind.Relative), null, deadline.Token))
        {
            created.EnsureSuccessStatusCode();
        }

        using (var collection = await writer.PostAsJsonAsync(new Uri($"{node.Tenant.Path("json")}/collections", UriKind.Relative), new { name = "items" }, deadline.Token))
        {
            collection.EnsureSuccessStatusCode();
        }

        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await writer.PutAsync(new Uri($"{node.Tenant.Path("json")}/collections/items/documents/{id}", UriKind.Relative), body, deadline.Token);
        await Assert.That(response.StatusCode).IsEqualTo(scenario switch
        {
            "validBoundary" => HttpStatusCode.Created,
            "oversized" => HttpStatusCode.RequestEntityTooLarge,
            _ => HttpStatusCode.BadRequest,
        });
        var origin = RandomNumberGenerator.GetBytes(16);
        using var call = node.Sync();
        await HelloAsync(call, node.Tenant.Database("json"), origin);
        await SendAsync(call, Batch(node.Tenant.Database("json"), origin, 1, [new DocumentChange("items", id, 2, bytes)]));
        var result = await NextAsync(call);
        var replicated = scenario == "validBoundary";
        if (replicated)
        {
            await Assert.That(result.Durable.DurablePrefix).IsEqualTo(1UL);
        }
        else
        {
            await Assert.That(result.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorInvalidBatch);
            await Assert.That(result.Error.Retryable).IsFalse();
        }

        var store = (await node.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(node.Tenant.Database("json"), deadline.Token))!;
        await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
        await Assert.That(await read.GetAppliedSequenceAsync(origin, deadline.Token)).IsEqualTo(replicated ? 1 : 0);
        var document = await read.GetAsync("items", id, deadline.Token);
        if (replicated)
        {
            await Assert.That(document!.Version).IsEqualTo(2);
            await Assert.That(document.Json.Span.SequenceEqual(bytes)).IsTrue();
        }
        else
        {
            await Assert.That(document).IsNull();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Recreating_the_receiver_ends_the_old_stream_without_acknowledging_its_duplicate(bool applyOnStream)
    {
        await using var node = await Node.StartAsync("retired");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var origin = RandomNumberGenerator.GetBytes(16);
        var change = Put("items", "a", 1);
        if (!applyOnStream)
        {
            await ApplyAsync(node, "retired", origin, 1, change);
        }

        using var call = node.Sync();
        var welcome = await HelloAsync(call, node.Tenant.Database("retired"), origin);
        if (applyOnStream)
        {
            await SendAsync(call, Batch(node.Tenant.Database("retired"), origin, 1, [change]));
            await Assert.That((await NextAsync(call)).Durable.DurablePrefix).IsEqualTo(1UL);
            await NextAsync(call);
        }
        else
        {
            await Assert.That(welcome.Frontiers[0].DurableSequence).IsEqualTo(1UL);
        }

        var engine = node.Server.Services.GetRequiredService<IStorageEngine>();
        await Assert.That(await engine.DeleteDatabaseAsync(node.Tenant.Database("retired"), deadline.Token)).IsTrue();
        await engine.CreateDatabaseAsync(node.Tenant.Database("retired"), deadline.Token);
        try
        {
            await SendAsync(call, Batch(node.Tenant.Database("retired"), origin, 1, [change]));
        }
        catch (RpcException writeError) when (writeError.StatusCode is StatusCode.Cancelled or StatusCode.FailedPrecondition)
        {
            // Retirement can close the request writer before it observes the server's trailers.
            // The response reader must still report the exact retirement status below.
        }

        var error = await Assert.That(async () => await NextAsync(call)).Throws<RpcException>();
        await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        await Assert.That(error.Status.Detail).IsEqualTo("the receiving database was deleted; reopen the Sync stream");
        await Assert.That(await node.ListAsync("retired", "items")).IsEmpty();

        using var resumed = node.Sync();
        var fresh = await HelloAsync(resumed, node.Tenant.Database("retired"), origin);
        await Assert.That(fresh.LocalIdentity.MemberIncarnation.Equals(welcome.LocalIdentity.MemberIncarnation)).IsFalse();
        await Assert.That(fresh.Frontiers[0].HasDurableSequence).IsFalse();
        await SendAsync(resumed, Batch(node.Tenant.Database("retired"), origin, 1, [change]));
        await Assert.That((await NextAsync(resumed)).Durable.DurablePrefix).IsEqualTo(1UL);
        await Assert.That((await node.ListAsync("retired", "items")).Single().Id).IsEqualTo("a");
    }

    [Test]
    public async Task Groups_apply_once_and_a_new_stream_resumes_after_them()
    {
        await using var node = await Node.StartAsync("ledger");
        var origin = RandomNumberGenerator.GetBytes(16);
        var first = new[] { Put("people", "a", 1), Put("people", "b", 1), Put("people", "c", 1), Delete("people", "c", 2) };

        using (var call = node.Sync())
        {
            var welcome = await HelloAsync(call, node.Tenant.Database("ledger"), origin);
            await Assert.That(welcome.Frontiers[0].HasDurableSequence).IsFalse();

            await SendAsync(call, Batch(node.Tenant.Database("ledger"), origin, 1, first));
            await Assert.That((await NextAsync(call)).Durable.DurablePrefix).IsEqualTo(1UL);
            await Assert.That((await NextAsync(call)).Applied.AppliedPrefix).IsEqualTo(1UL);

            // A redelivered group is acknowledged again and not applied twice.
            await SendAsync(call, Batch(node.Tenant.Database("ledger"), origin, 1, first));
            await Assert.That((await NextAsync(call)).Durable.DurablePrefix).IsEqualTo(1UL);
            await NextAsync(call);
            await call.RequestStream.CompleteAsync();
        }

        using (var call = node.Sync())
        {
            var welcome = await HelloAsync(call, node.Tenant.Database("ledger"), origin);
            await Assert.That(welcome.Frontiers[0].DurableSequence).IsEqualTo(1UL);

            // A gap is refused as retryable; the sender resumes after the prefix.
            await SendAsync(call, Batch(node.Tenant.Database("ledger"), origin, 3, [Put("people", "d", 1)], welcome.Frontiers[0].JournalDigest.Memory));
            var gap = (await NextAsync(call)).Error;
            await Assert.That(gap.Code).IsEqualTo(StreamErrorCode.StreamErrorInvalidBatch);
            await Assert.That(gap.Retryable).IsTrue();
        }

        var documents = await node.ListAsync("ledger", "people");
        await Assert.That(documents.Select(document => document.Id)).IsEquivalentTo(["a", "b"]);
        await Assert.That(await node.Server.MetricAsync("terrabase_replication_received_groups_total", "result=\"duplicate\"")).IsEqualTo(1);
    }

    [Test]
    public async Task Corrupt_and_oversized_frames_are_rejected_without_applying_anything()
    {
        await using var node = await Node.StartAsync("vault");
        var origin = RandomNumberGenerator.GetBytes(16);
        var valid = Batch(node.Tenant.Database("vault"), origin, 1, [Put("items", "x", 1)]);

        var flipped = valid.Clone();
        var envelope = flipped.ApplicationEnvelope.ToByteArray();
        envelope[^1] ^= 0xFF;
        flipped.ApplicationEnvelope = ByteString.CopyFrom(envelope);

        // A compression bomb: valid checksums, but the block expands far beyond the 1 KiB its header declares.
        // Decompression must stop at the declared size instead of inflating.
        var inflated = Batch(node.Tenant.Database("vault"), origin, 1, [Put("items", "x", 1, new string('a', 64 * 1024))]);
        var bomb = inflated.ApplicationEnvelope.ToByteArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bomb.AsSpan(24), 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(bomb.AsSpan(28), Crc32C(bomb.AsSpan(0, 28)));
        inflated.ApplicationEnvelope = ByteString.CopyFrom(bomb);
        inflated.DecodedApplicationBytes = inflated.WalGroupBytes = 1024;

        var oversized = valid.Clone();
        oversized.DecodedApplicationBytes = oversized.WalGroupBytes = ReplicationProtocol.MaxApplicationBytes + 1;

        var lz4Less = valid.Clone();
        lz4Less.Codec = PayloadCodec.Identity;

        // Within the 4 MiB envelope limit, beyond the 2 MiB limit on one group's collections, ids and documents.
        var groupTooLarge = valid.Clone();
        var oversizedPlain = ChangeGroup.Encode([Put("items", "x", 1, new string('a', 3 * 1024 * 1024))]);
        groupTooLarge.ApplicationEnvelope = ByteString.CopyFrom(ApplicationEnvelope.Encode(oversizedPlain).Span);
        groupTooLarge.DecodedApplicationBytes = groupTooLarge.WalGroupBytes = (uint)oversizedPlain.Length;
        groupTooLarge.EncodedApplicationBytes = (uint)groupTooLarge.ApplicationEnvelope.Length;

        var otherReplica = valid.Clone();
        otherReplica.EventId.Origin.ReplicaId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16));

        var otherTenant = valid.Clone();
        otherTenant.EventId.TenantId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16));

        var otherProject = valid.Clone();
        otherProject.EventId.ProjectId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16));

        var otherEpoch = valid.Clone();
        otherEpoch.MembershipEpoch = 7;

        // Well-formed JSON whose string holds a byte that is not UTF-8.
        var notUtf8 = Batch(node.Tenant.Database("vault"), origin, 1, [new DocumentChange("items", "x", 1, new byte[] { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}' })]);

        foreach (var (batch, code) in new[]
        {
            (flipped, StreamErrorCode.StreamErrorInvalidBatch),
            (inflated, StreamErrorCode.StreamErrorInvalidBatch),
            (oversized, StreamErrorCode.StreamErrorInvalidBatch),
            (lz4Less, StreamErrorCode.StreamErrorUnsupportedFormat),
            (groupTooLarge, StreamErrorCode.StreamErrorInvalidBatch),
            (otherReplica, StreamErrorCode.StreamErrorInvalidBatch),
            (otherEpoch, StreamErrorCode.StreamErrorInvalidBatch),
            (otherTenant, StreamErrorCode.StreamErrorInvalidBatch),
            (otherProject, StreamErrorCode.StreamErrorInvalidBatch),
            (notUtf8, StreamErrorCode.StreamErrorInvalidBatch),
        })
        {
            using var call = node.Sync();
            await HelloAsync(call, node.Tenant.Database("vault"), origin);
            await SendAsync(call, batch);
            var error = (await NextAsync(call)).Error;
            await Assert.That(error.Code).IsEqualTo(code);
            await Assert.That(error.Retryable).IsFalse();
        }

        // A message beyond the gRPC limit never reaches the service.
        using (var call = node.Sync())
        {
            await HelloAsync(call, node.Tenant.Database("vault"), origin);
            var huge = valid.Clone();
            huge.ApplicationEnvelope = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(ReplicationProtocol.MaxMessageBytes + 1));
            try
            {
                await SendAsync(call, huge);
            }
            catch (RpcException)
            {
                // The server may reset the stream while the message is still being written.
            }

            var error = await Assert.That(async () => await NextAsync(call)).Throws<RpcException>();
            await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var store = (await node.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(node.Tenant.Database("vault"), deadline.Token))!;
        await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
        await Assert.That(await read.GetAppliedSequenceAsync(origin, deadline.Token)).IsEqualTo(0);
        await Assert.That(await read.GetAsync("items", "x", deadline.Token)).IsNull();
        using var missing = await node.Http.GetAsync(new Uri($"{node.Tenant.Path("vault")}/collections/items/documents/x", UriKind.Relative));
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await node.Server.MetricAsync("terrabase_replication_received_groups_total", "result=\"rejected\"")).IsEqualTo(10);
    }

    [Test]
    public async Task Concurrent_versions_of_a_document_resolve_the_same_way_in_any_order()
    {
        await using var node = await Node.StartAsync("ab", "ba");
        var left = RandomNumberGenerator.GetBytes(16);
        var right = RandomNumberGenerator.GetBytes(16);
        var a = Put("docs", "same", 2, Faker.Lorem.Sentence());
        var b = Put("docs", "same", 2, Faker.Lorem.Sentence());
        var winner = string.CompareOrdinal(Encoding.UTF8.GetString(a.Json!.Value.Span), Encoding.UTF8.GetString(b.Json!.Value.Span)) > 0 ? a : b;

        // Database "ab" sees a then b; database "ba" sees b then a. A tombstone at version 3 beats both.
        foreach (var (database, firstChange, secondChange) in new[] { ("ab", a, b), ("ba", b, a) })
        {
            await ApplyAsync(node, database, left, 1, firstChange);
            await ApplyAsync(node, database, right, 1, secondChange);
        }

        var ab = await node.ListAsync("ab", "docs");
        var ba = await node.ListAsync("ba", "docs");
        await ApplyAsync(node, "ab", left, 2, Delete("docs", "same", 3));
        await ApplyAsync(node, "ab", right, 2, Put("docs", "same", 2, "{\"late\":true}"));

        await Assert.That(ab.Single().Data.GetRawText()).IsEqualTo(Encoding.UTF8.GetString(winner.Json!.Value.Span));
        await Assert.That(ba.Single().Data.GetRawText()).IsEqualTo(ab.Single().Data.GetRawText());
        await Assert.That(ab.Single().Version).IsEqualTo(2);
        await Assert.That(await node.ListAsync("ab", "docs")).IsEmpty();
    }

    [Test]
    [Arguments("message")]
    [Arguments("credit")]
    [Arguments("group")]
    [Arguments("changes")]
    [Arguments("encoded")]
    [Arguments("decoded")]
    public async Task A_group_beyond_the_limits_the_sender_offered_is_refused(string limit)
    {
        await using var node = await Node.StartAsync("bounded");
        var origin = RandomNumberGenerator.GetBytes(16);
        var hello = ReplicationProtocol.CreateHello(node.Tenant.Database("bounded"), origin);
        // Each case lowers one limit below a group that is otherwise valid: about 1.4 KiB of random text in two changes.
        switch (limit)
        {
            case "message":
                hello.Limits.MaxGrpcMessageBytes = 512;
                break;
            case "credit":
                hello.Limits.ByteCredit = 512;
                break;
            case "group":
                hello.Limits.MaxTransactionGroupBytes = 512;
                break;
            case "changes":
                hello.Limits.MaxChangesPerGroup = 1;
                break;
            case "encoded":
                hello.Limits.MaxEncodedApplicationBytes = 512;
                break;
            default:
                hello.Limits.MaxDecodedApplicationBytes = 512;
                break;
        }

        using var call = node.Sync();
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello });
        var welcome = (await NextAsync(call)).Welcome;
        var blob = Convert.ToBase64String(RandomNumberGenerator.GetBytes(512));
        await SendAsync(call, Batch(node.Tenant.Database("bounded"), origin, 1, [Put("items", "a", 1, blob), Put("items", "b", 1, blob)]));
        var error = (await NextAsync(call)).Error;

        // Welcome echoes the lowered limit, and the receiver enforces it as the sender must.
        await Assert.That(welcome.Limits.Equals(hello.Limits)).IsTrue();
        await Assert.That(error.Code).IsEqualTo(StreamErrorCode.StreamErrorInvalidBatch);
        await Assert.That(error.Retryable).IsFalse();
        await Assert.That(await node.ListAsync("bounded", "items")).IsEmpty();
    }

    [Test]
    public async Task A_document_at_the_last_version_refuses_writes_instead_of_losing_them()
    {
        await using var node = await Node.StartAsync("limits");
        var engine = node.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(node.Tenant.Database("limits"), CancellationToken.None))!;
        await using (var setup = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await setup.CreateCollectionAsync(new CollectionInfo("docs", 0), CancellationToken.None);
            await setup.PutAsync("docs", "max", "{\"text\":\"legacy\"}"u8.ToArray(), CancellationToken.None);
            await setup.CommitAsync(CancellationToken.None);
        }

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = node.Tenant.File(node.Server, "limits"),
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync(CancellationToken.None);
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                UPDATE documents SET version = 9223372036854775807 WHERE collection = 'docs' AND id = 'max';
                UPDATE commit_offset SET value = 9223372036854775807 WHERE id = 1;
                """;
            await seed.ExecuteNonQueryAsync(CancellationToken.None);
        }
        using var writer = await node.ClientAsync("documents:write");
        var document = new Uri($"{node.Tenant.Path("limits")}/collections/docs/documents/max", UriKind.Relative);

        using var deleted = await writer.DeleteAsync(document);
        using var replaced = await writer.PutAsJsonAsync(document, new { text = "newer" });
        var remaining = await node.ListAsync("limits", "docs");

        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(remaining.Single().Version).IsEqualTo(long.MaxValue);
    }

    private static async Task ApplyAsync(Node node, string database, byte[] origin, long sequence, DocumentChange change)
    {
        using var call = node.Sync();
        var welcome = await HelloAsync(call, node.Tenant.Database(database), origin);
        await SendAsync(call, Batch(node.Tenant.Database(database), origin, sequence, [change], welcome.Frontiers[0].JournalDigest.Memory));
        await Assert.That((await NextAsync(call)).Durable.DurablePrefix).IsEqualTo((ulong)sequence);
    }

    private static DocumentChange Put(string collection, string id, long version, string? text = null) =>
        new(collection, id, version, JsonSerializer.SerializeToUtf8Bytes(new { text = text ?? Faker.Lorem.Sentence() }));

    private static DocumentChange Delete(string collection, string id, long version) => new(collection, id, version, null);

    private static CommittedGroupBatch Batch(TenantContext database, byte[] origin, long sequence, IReadOnlyList<DocumentChange> changes, ReadOnlyMemory<byte> previous = default) =>
        ReplicationProtocol.CreateBatch(database, origin, sequence, changes, previous);

    private static async Task<Welcome> HelloAsync(AsyncDuplexStreamingCall<SyncRequest, SyncResponse> call, TenantContext database, byte[] origin)
    {
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(database, origin) });
        return (await NextAsync(call)).Welcome;
    }

    private static Task SendAsync(AsyncDuplexStreamingCall<SyncRequest, SyncResponse> call, CommittedGroupBatch batch) =>
        call.RequestStream.WriteAsync(new SyncRequest { CommittedGroup = batch });

    private static async Task<SyncResponse> NextAsync(AsyncDuplexStreamingCall<SyncRequest, SyncResponse> call)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        return await call.ResponseStream.MoveNext(timeout.Token)
            ? call.ResponseStream.Current
            : throw new InvalidOperationException("the Sync stream ended");
    }

    // CRC-32C as the envelope uses it, to forge a header that passes its checksum.
    private static uint Crc32C(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc = BitOperations.Crc32C(crc, value);
        }

        return ~crc;
    }

    // One node with its peer and client listeners, a gRPC client and a document API client.
    private sealed class Node : IAsyncDisposable
    {
        private readonly PeerTls _peerTls;
        private readonly ClientTls _clientTls;
        private readonly Grpc.Net.Client.GrpcChannel _channel;

        private Node(PeerTls peerTls, ClientTls clientTls, TerraBaseTestServer server, ReplicationTenant tenant, HttpClient http)
        {
            _peerTls = peerTls;
            _clientTls = clientTls;
            Server = server;
            Tenant = tenant;
            Http = http;
            _channel = peerTls.CreateChannel(server.PeerAddress!);
        }

        public TerraBaseTestServer Server { get; }

        public ReplicationTenant Tenant { get; }

        public HttpClient Http { get; }

        public static async Task<Node> StartAsync(params string[] databases)
        {
            var peerTls = new PeerTls();
            var clientTls = new ClientTls();
            var server = await TerraBaseTestServer.StartAsync(settings =>
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
            });
            var tenant = await ReplicationTenant.CreateAsync(server, databases);
            var http = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:read"));
            return new Node(peerTls, clientTls, server, tenant, http);
        }

        public AsyncDuplexStreamingCall<SyncRequest, SyncResponse> Sync() =>
            new Features.Replication.V1.Replication.ReplicationClient(_channel).Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);

        public async Task<HttpClient> ClientAsync(params string[] scopes) =>
            _clientTls.CreateClient(Server.ClientAddress!, await Tenant.KeyAsync(Server, scopes));

        public async Task<List<(string Id, long Version, JsonElement Data)>> ListAsync(string database, string collection)
        {
            using var response = await Http.GetAsync(new Uri($"{Tenant.Path(database)}/collections/{collection}/documents?limit=100", UriKind.Relative));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            response.EnsureSuccessStatusCode();
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // Public versions are ETags bound to each receiving incarnation. Verify numeric state through
            // this running node's engine while still reading every document over its real HTTPS listener.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            var store = await Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(Tenant.Database(database), deadline.Token);
            await using var read = await store!.BeginTransactionAsync(write: false, deadline.Token);
            var result = new List<(string Id, long Version, JsonElement Data)>();
            foreach (var document in page.RootElement.GetProperty("items").EnumerateArray())
            {
                var id = document.GetProperty("id").GetString()!;
                var version = await read.GetVersionAsync(collection, id, deadline.Token);
                result.Add((id, version!.Value.Version, document.GetProperty("document").Clone()));
            }

            return result;
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Dispose();
            Http.Dispose();
            await Server.DisposeAsync();
            _peerTls.Dispose();
            _clientTls.Dispose();
        }
    }
}
