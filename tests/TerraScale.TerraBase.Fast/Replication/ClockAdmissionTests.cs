using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Bogus;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class ClockAdmissionTests
{
    [Test]
    [Arguments("max", false)]
    [Arguments("max", true)]
    [Arguments("nearMax", false)]
    [Arguments("nearMax", true)]
    [Arguments("gap", false)]
    [Arguments("gap", true)]
    [Arguments("chain", false)]
    [Arguments("losing", false)]
    public async Task Rejected_peer_versions_leave_clock_history_frontier_and_documents_unchanged(string scenario, bool deleted)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
        });
        var tenant = await ReplicationTenant.CreateAsync(server, "db");
        using var client = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:write"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        using var collection = await client.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" }, token);
        collection.EnsureSuccessStatusCode();
        var id = Guid.CreateVersion7().ToString("D");
        var value = new Faker().Lorem.Word();
        using var original = await client.PutAsJsonAsync(Document(tenant, id), new { value }, token);
        original.EnsureSuccessStatusCode();
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(tenant.Database("db"), token))!;
        if (scenario == "losing")
        {
            // A legacy row with a clock behind its merge version makes this extreme import lose.
            // Admission must still precede ObserveOffset rather than relying on the merge result.
            await ExecuteAsync(tenant.File(server, "db"), async command =>
            {
                command.CommandText = "UPDATE documents SET version = $version WHERE collection = 'items' AND id = $id;";
                command.Parameters.AddWithValue("$version", VersionClock.MaxVersion);
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(token);
            }, token);
        }

        var origin = RandomNumberGenerator.GetBytes(16);
        long head;
        long sequence;
        StoredDocument before;
        var history = new List<StoredDocument>();
        await using (var read = await store.BeginTransactionAsync(write: false, token))
        {
            head = await read.GetOffsetAsync(token);
            sequence = await read.GetLastChangeSequenceAsync(token);
            before = (await read.GetAsync("items", id, token))!;
            await foreach (var change in read.ChangesAsync("items", null, 0, head, token))
            {
                history.Add(change);
            }
        }

        var invalidVersion = scenario switch
        {
            "max" => long.MaxValue,
            "nearMax" => VersionClock.MaxVersion + 1,
            _ => head + VersionClock.MaxAdvance + 1,
        };
        var addedId = Guid.CreateVersion7().ToString("D");
        DocumentChange[] changes =
        [
            new("new_collection", addedId, scenario == "chain" ? head + VersionClock.MaxAdvance : head + 1, "{}"u8.ToArray()),
            new("items", id, invalidVersion, deleted ? null : "{\"value\":\"replacement\"}"u8.ToArray()),
        ];
        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: token);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(tenant.Database("db"), origin) }, token);
        await Assert.That((await NextAsync(call, token)).FrameCase).IsEqualTo(SyncResponse.FrameOneofCase.Welcome);
        await call.RequestStream.WriteAsync(new SyncRequest { CommittedGroup = ReplicationProtocol.CreateBatch(tenant.Database("db"), origin, 1, changes) }, token);
        var rejected = await NextAsync(call, token);
        await Assert.That(rejected.FrameCase).IsEqualTo(SyncResponse.FrameOneofCase.Error);
        await Assert.That(rejected.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorInvalidBatch);
        await Assert.That(rejected.Error.Retryable).IsFalse();
        await Assert.That(await call.ResponseStream.MoveNext(token)).IsFalse();
        await using (var read = await store.BeginTransactionAsync(write: false, token))
        {
            await Assert.That(await read.GetOffsetAsync(token)).IsEqualTo(head);
            await Assert.That(await read.GetLastChangeSequenceAsync(token)).IsEqualTo(sequence);
            await Assert.That(await read.GetAppliedSequenceAsync(origin, token)).IsEqualTo(0);
            await Assert.That(await read.GetCollectionAsync("new_collection", token)).IsNull();
            var unchanged = (await read.GetAsync("items", id, token))!;
            await Assert.That(unchanged.Version).IsEqualTo(before.Version);
            await Assert.That(unchanged.Json.Span.SequenceEqual(before.Json.Span)).IsTrue();
            var after = new List<StoredDocument>();
            await foreach (var change in read.ChangesAsync("items", null, 0, head, token))
            {
                after.Add(change);
            }

            await Assert.That(after.Select(change => (change.Id, change.Version, change.HistoryOffset))).IsEquivalentTo(
                history.Select(change => (change.Id, change.Version, change.HistoryOffset)));
            await Assert.That(after.Zip(history).All(pair => pair.First.Json.Span.SequenceEqual(pair.Second.Json.Span))).IsTrue();
        }

        // Rejection cannot poison even an unrelated key in this same database.
        var followingId = Guid.CreateVersion7().ToString("D");
        using var following = await client.PutAsJsonAsync(Document(tenant, followingId), new { value }, token);
        await Assert.That(following.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await using var final = await store.BeginTransactionAsync(write: false, token);
        await Assert.That((await final.GetAsync("items", followingId, token))!.Version).IsEqualTo(head + 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Direct_imports_cannot_chain_advances_and_a_caught_failure_remains_rollback_only(bool deleted)
    {
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(clientTls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(server, "db");
        using var client = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:write"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        using var collection = await client.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" }, token);
        collection.EnsureSuccessStatusCode();
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        var origin = RandomNumberGenerator.GetBytes(16);
        long head;
        await using (var read = await store.BeginTransactionAsync(write: false, token))
        {
            head = await read.GetOffsetAsync(token);
        }

        var id = Guid.CreateVersion7().ToString("D");
        await using (var write = await store.BeginTransactionAsync(write: true, token))
        {
            await write.SetAppliedSequenceAsync(origin, 1, token);
            await Assert.That(await write.ApplyAsync(new DocumentChange("new_collection", id, head + VersionClock.MaxAdvance, "{}"u8.ToArray()), token)).IsTrue();
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await write.ApplyAsync(new DocumentChange("new_collection", id, head + VersionClock.MaxAdvance + 1,
                    deleted ? null : "{\"newer\":true}"u8.ToArray()), token);
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => write.CommitAsync(token).AsTask());
        }

        await using (var read = await store.BeginTransactionAsync(write: false, token))
        {
            await Assert.That(await read.GetOffsetAsync(token)).IsEqualTo(head);
            await Assert.That(await read.GetAppliedSequenceAsync(origin, token)).IsEqualTo(0);
            await Assert.That(await read.GetCollectionAsync("new_collection", token)).IsNull();
            await Assert.That(await read.GetVersionAsync("new_collection", id, token)).IsNull();
        }

        using var following = await client.PutAsJsonAsync(Document(tenant, id), new { value = new Faker().Lorem.Word() }, token);
        await Assert.That(following.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await using var final = await store.BeginTransactionAsync(write: false, token);
        await Assert.That((await final.GetAsync("items", id, token))!.Version).IsEqualTo(head + 1);
    }

    [Test]
    public async Task The_last_safe_local_version_replicates_and_terminal_allocation_refuses_further_writes()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        });
        var tenant = await ReplicationTenant.CreateAsync(sender, "db");
        await tenant.BindAsync(receiver);
        using var client = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        using var collection = await client.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" }, token);
        collection.EnsureSuccessStatusCode();
        var source = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        var target = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        await SetClockAsync(tenant.File(sender, "db"), VersionClock.MaxVersion - 1, token);
        await SetClockAsync(tenant.File(receiver, "db"), VersionClock.MaxVersion - VersionClock.MaxAdvance, token);
        var id = Guid.CreateVersion7().ToString("D");
        var value = new Faker().Lorem.Word();
        using var last = await client.PutAsJsonAsync(Document(tenant, id), new { value }, token);
        await Assert.That(last.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await TerraBaseTestServer.ReplicatedAsync(sender);
        await using (var local = await source.BeginTransactionAsync(write: false, token))
        await using (var remote = await target.BeginTransactionAsync(write: false, token))
        {
            var stored = (await remote.GetAsync("items", id, token))!;
            await Assert.That(stored.Version).IsEqualTo(VersionClock.MaxVersion);
            using var json = JsonDocument.Parse(stored.Json);
            await Assert.That(json.RootElement.GetProperty("value").GetString()).IsEqualTo(value);
            // Group 1 created the collection.
            await Assert.That(await remote.GetAppliedSequenceAsync(await local.GetOriginAsync(token), token)).IsEqualTo(2);
            await Assert.That(await remote.GetOffsetAsync(token)).IsEqualTo(VersionClock.MaxVersion);
        }

        using var refused = await client.PutAsJsonAsync(Document(tenant, Guid.CreateVersion7().ToString("D")), new { value }, token);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await using var final = await source.BeginTransactionAsync(write: false, token);
        await Assert.That(await final.GetOffsetAsync(token)).IsEqualTo(VersionClock.MaxVersion);
        await Assert.That(await final.GetLastChangeSequenceAsync(token)).IsEqualTo(2);
    }

    [Test]
    public async Task Equal_terminal_version_winners_use_reserved_shape_history_without_enabling_local_writes()
    {
        using var tls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings => { tls.Apply(settings); clientTls.Apply(settings); });
        var tenant = await ReplicationTenant.CreateAsync(server, "db");
        using var client = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:read", "documents:write"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        var id = Guid.CreateVersion7().ToString("D");
        using var collection = await client.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" }, token);
        collection.EnsureSuccessStatusCode();
        using var created = await client.PutAsJsonAsync(Document(tenant, id), new { value = "a" }, token);
        created.EnsureSuccessStatusCode();
        await ExecuteAsync(tenant.File(server, "db"), async command =>
        {
            command.CommandText = "UPDATE documents SET version = $version WHERE id = $id; UPDATE commit_offset SET value = $version WHERE id = 1;";
            command.Parameters.AddWithValue("$version", VersionClock.MaxVersion);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(token);
        }, token);
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        var origin = RandomNumberGenerator.GetBytes(16);
        ReadOnlyMemory<byte> proof = JournalDigest.Initial(origin);
        using var channel = tls.CreateChannel(server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: token);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(tenant.Database("db"), origin) }, token);
        await Assert.That((await NextAsync(call, token)).Welcome).IsNotNull();
        for (var sequence = 1; sequence <= 2; sequence++)
        {
            var change = sequence == 1
                ? new DocumentChange("items", id, VersionClock.MaxVersion, "{\"value\":\"z\"}"u8.ToArray())
                : new DocumentChange("items", id, VersionClock.MaxVersion, null);
            await call.RequestStream.WriteAsync(new SyncRequest
            {
                CommittedGroup = ReplicationProtocol.CreateBatch(tenant.Database("db"), origin, sequence, [change], proof),
            }, token);
            var durable = (await NextAsync(call, token)).Durable;
            await Assert.That(durable.DurablePrefix).IsEqualTo((ulong)sequence);
            proof = durable.JournalDigest.Memory;
            await Assert.That((await NextAsync(call, token)).Applied.AppliedPrefix).IsEqualTo((ulong)sequence);
            await using var read = await store.BeginTransactionAsync(write: false, token);
            await Assert.That(await read.GetOffsetAsync(token)).IsEqualTo(VersionClock.MaxVersion + sequence);
            await Assert.That(await read.GetVersionAsync("items", id, token)).IsEqualTo(new DocumentVersion(VersionClock.MaxVersion, Deleted: sequence == 2));
            await Assert.That(await read.GetAppliedSequenceAsync(origin, token)).IsEqualTo(sequence);
            var history = new List<StoredDocument>();
            await foreach (var row in read.ChangesAsync("items", null, VersionClock.MaxVersion, VersionClock.MaxVersion + sequence, token))
            {
                history.Add(row);
            }

            await Assert.That(history.Select(row => row.HistoryOffset)).IsEquivalentTo(Enumerable.Range(1, sequence).Select(index => (long?)(VersionClock.MaxVersion + index)));
            await Assert.That(history.All(row => row.Version == VersionClock.MaxVersion)).IsTrue();
        }

        using var refused = await client.PutAsJsonAsync(Document(tenant, Guid.CreateVersion7().ToString("D")), new { value = "local" }, token);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var deleted = await client.GetAsync(Document(tenant, id), token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        await SetClockAsync(tenant.File(server, "db"), long.MaxValue, token);
        var overflowId = Guid.CreateVersion7().ToString("D");
        await using (var write = await store.BeginTransactionAsync(write: true, token))
        {
            await Assert.That(await write.ApplyAsync(new DocumentChange("items", overflowId, VersionClock.MaxVersion, "{}"u8.ToArray()), token)).IsTrue();
            await write.SetAppliedSequenceAsync(origin, 3, token);
            await Assert.ThrowsAsync<OverflowException>(() => write.CommitAsync(token).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => write.CommitAsync(token).AsTask());
        }

        await using var unchanged = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await unchanged.GetOffsetAsync(token)).IsEqualTo(long.MaxValue);
        await Assert.That(await unchanged.GetAppliedSequenceAsync(origin, token)).IsEqualTo(2);
        await Assert.That(await unchanged.GetAsync("items", overflowId, token)).IsNull();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Collection_metadata_with_a_losing_import_advances_once_only_when_it_changes(bool metadataFirst, bool existing)
    {
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(clientTls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(server, "db");
        using var client = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:read", "documents:write"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        using var collection = await client.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" }, token);
        collection.EnsureSuccessStatusCode();
        var id = Guid.CreateVersion7().ToString("D");
        using var written = await client.PutAsJsonAsync(Document(tenant, id), new { value = new Faker().Lorem.Word() }, token);
        written.EnsureSuccessStatusCode();
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        long head;
        StoredDocument document;
        await using (var read = await store.BeginTransactionAsync(write: false, token))
        {
            head = await read.GetOffsetAsync(token);
            document = (await read.GetAsync("items", id, token))!;
        }

        var notification = store.CommitSequence;
        var waiter = store.WaitForCommitAsync(notification, token);
        await using (var write = await store.BeginTransactionAsync(write: true, token))
        {
            if (metadataFirst)
            {
                await Assert.That(await write.CreateCollectionAsync(new CollectionInfo(existing ? "items" : "added", 0), token)).IsEqualTo(!existing);
            }

            await Assert.That(await write.ApplyAsync(new DocumentChange("items", id, document.Version, document.Json), token)).IsFalse();
            if (!metadataFirst)
            {
                await Assert.That(await write.CreateCollectionAsync(new CollectionInfo(existing ? "items" : "added", 0), token)).IsEqualTo(!existing);
            }

            await Assert.That(await write.CommitAsync(token)).IsEqualTo(head + (existing ? 0 : 1));
        }

        if (existing)
        {
            await Assert.That(store.CommitSequence).IsEqualTo(notification);
            await Assert.That(waiter.IsCompleted).IsFalse();
        }
        else
        {
            await waiter.WaitAsync(token);
            await Assert.That(store.CommitSequence).IsEqualTo(notification + 1);
        }

        using var listed = await client.GetAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), token);
        listed.EnsureSuccessStatusCode();
        await using var final = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await final.GetOffsetAsync(token)).IsEqualTo(head + (existing ? 0 : 1));
        await Assert.That((await final.GetAsync("items", id, token))!.Version).IsEqualTo(document.Version);
    }

    private static Task SetClockAsync(string path, long head, CancellationToken token) =>
        ExecuteAsync(path, async command =>
        {
            command.CommandText = "UPDATE commit_offset SET value = $head WHERE id = 1;";
            command.Parameters.AddWithValue("$head", head);
            await command.ExecuteNonQueryAsync(token);
        }, token);

    private static async Task ExecuteAsync(string path, Func<SqliteCommand, Task> execute, CancellationToken token)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        await execute(command);
    }

    private static Uri Document(ReplicationTenant tenant, string id) =>
        new($"{tenant.Path("db")}/collections/items/documents/{id}", UriKind.Relative);

    private static async Task<SyncResponse> NextAsync(AsyncDuplexStreamingCall<SyncRequest, SyncResponse> call, CancellationToken token) =>
        await call.ResponseStream.MoveNext(token) ? call.ResponseStream.Current : throw new InvalidOperationException("the Sync stream ended");
}
