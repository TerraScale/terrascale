using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Fast.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class DocumentCaptureTests
{
    // A copy is a new database: its stream starts on a new origin with a baseline of every register it was copied with,
    // tombstones included, and none of its source's applied positions. On a peer that held older writes of the same
    // documents, the copy's later deletions and writes win.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_copy_sends_every_register_it_was_copied_with_on_a_new_origin(bool drop)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var clock = new RepairClock(DateTimeOffset.UtcNow);
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
        }, services => services.AddSingleton<TimeProvider>(clock));
        // Park repair before provisioning so the receiver's drop-generated _unique tombstone cannot enter the copy.
        await clock.PausedAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var tenant = await ReplicationTenant.CreateAsync(sender, "captured", "copied");
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:read", "documents:write"));
        using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        using (var response = await writer.PutAsync(new Uri(tenant.Path("captured"), UriKind.Relative), null, token))
        {
            response.EnsureSuccessStatusCode();
        }

        var main = tenant.Database("captured");
        var copy = tenant.Database("copied");
        var engine = sender.Services.GetRequiredService<IStorageEngine>();
        var source = (await engine.OpenDatabaseAsync(main, token))!;
        var ids = Enumerable.Range(0, 4).Select(_ => EntityIds.New().ToString()).ToArray();
        await using (var initial = await source.BeginTransactionAsync(write: true, token))
        {
            await initial.CreateCollectionAsync(new CollectionInfo("items", 0), token);
            foreach (var id in ids)
            {
                await initial.PutAsync("items", id, "{\"n\":1}"u8.ToArray(), token);
            }

            await initial.CommitAsync(token);
        }

        using var sourceChannel = peerTls.CreateChannel(sender.PeerAddress!);
        using var targetChannel = peerTls.CreateChannel(receiver.PeerAddress!);
        var sourceRpc = new Features.Replication.V1.Replication.ReplicationClient(sourceChannel);
        var targetRpc = new Features.Replication.V1.Replication.ReplicationClient(targetChannel);
        // The future copy's peer already holds older writes of every document, by a replica that wrote before them.
        var stale = ids.Select(id => Deltas.Write("items", id, "{\"stale\":true}", stamp: new Hlc(1 << 16))).ToArray();
        await ApplyGroupAsync(targetRpc, copy, EntityIds.New().ToByteArray(), 1, stale, token);
        await using (var deletion = await source.BeginTransactionAsync(write: true, token))
        {
            if (drop)
            {
                await Assert.That(await deletion.DropCollectionAsync("items", token)).IsTrue();
            }
            else
            {
                foreach (var id in ids)
                {
                    await Assert.That(await deletion.TombstoneAsync("items", id, token)).IsNotNull();
                }
            }

            await deletion.CommitAsync(token);
        }

        await using (var local = await source.BeginTransactionAsync(write: true, token))
        {
            await local.CreateCollectionAsync(new CollectionInfo("items", 0), token);
            await local.PutAsync("items", ids[2], "{\"local\":true}"u8.ToArray(), token);
            await local.PutAsync("items", ids[1], "{}"u8.ToArray(), token);
            await local.CommitAsync(token);
        }

        await using (var again = await source.BeginTransactionAsync(write: true, token))
        {
            await again.TombstoneAsync("items", ids[1], token);
            await again.CommitAsync(token);
        }

        // Another member writes ids[3] again after its deletion.
        var remoteOrigin = EntityIds.New().ToByteArray();
        await ApplyGroupAsync(sourceRpc, main, remoteOrigin, 1, [Deltas.Write("items", ids[3], "{\"remote\":true}")], token);
        await Assert.That(await engine.DeleteDatabaseAsync(copy, token)).IsTrue();
        await Assert.That(await engine.CopyDatabaseAsync(main, copy, token)).IsTrue();
        var copied = (await engine.OpenDatabaseAsync(copy, token))!;
        await Assert.That(copied.Incarnation).IsNotEqualTo(source.Incarnation);
        await TerraBaseTestServer.ReplicatedAsync(sender);
        var target = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(copy, token))!;
        ReadOnlyMemory<byte> copyOrigin;
        await using (var read = await copied.BeginTransactionAsync(write: false, token))
        await using (var original = await source.BeginTransactionAsync(write: false, token))
        {
            copyOrigin = await read.GetOriginAsync(token);
            var mainOrigin = await original.GetOriginAsync(token);
            await Assert.That(copyOrigin.ToArray().SequenceEqual(mainOrigin.ToArray())).IsFalse();
            await Assert.That(copyOrigin.Span[8..].SequenceEqual(copied.Incarnation.ToByteArray().AsSpan(8, 8))).IsTrue();
            await Assert.That(await read.GetAppliedSequenceAsync(remoteOrigin, token)).IsEqualTo(0);
            await Assert.That(await read.GetLastChangeSequenceAsync(token)).IsEqualTo(1);
            var baseline = (await read.ReadChangesAsync(1, token))!.Changes;
            await Assert.That(baseline.Select(change => change.Id)).IsEquivalentTo(ids.Prepend("items"));
        }

        foreach (var (store, http) in new[] { (copied, writer), (target, reader) })
        {
            await using var read = await store.BeginTransactionAsync(write: false, token);
            foreach (var id in ids.Take(2))
            {
                await Assert.That((await read.GetVersionAsync("items", id, token))?.Deleted).IsTrue();
                using var response = await http.GetAsync(Document(tenant, id, "copied"), token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            await Assert.That((await read.GetAsync("items", ids[2], token))!.Json.Span.SequenceEqual("{\"local\":true}"u8)).IsTrue();
            await Assert.That((await read.GetAsync("items", ids[3], token))!.Json.Span.SequenceEqual("{\"remote\":true}"u8)).IsTrue();
            if (store == target)
            {
                await Assert.That(await read.GetAppliedSequenceAsync(copyOrigin, token)).IsEqualTo(1);
            }
        }

        // A local write on the copy replaces what it was copied with and reaches the peer.
        await PutAsync(tenant, writer, ids[0], "copied");
        await TerraBaseTestServer.ReplicatedAsync(sender);
        await using (var local = await copied.BeginTransactionAsync(write: false, token))
        await using (var remote = await target.BeginTransactionAsync(write: false, token))
        {
            var version = await local.GetVersionAsync("items", ids[0], token);
            await Assert.That(version?.Deleted).IsFalse();
            await Assert.That(await remote.GetVersionAsync("items", ids[0], token)).IsEqualTo(version);
        }
    }

    [Test]
    [Arguments("transaction")]
    [Arguments("sameKey")]
    [Arguments("tombstonePut")]
    [Arguments("putTombstone")]
    [Arguments("drop")]
    [Arguments("rollbackTombstone")]
    [Arguments("rollbackDrop")]
    public async Task Document_mutations_replicate_as_one_delta_per_document_of_their_commit(string operation)
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
        var tenant = await ReplicationTenant.CreateAsync(sender, "captured");
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:read", "documents:write"));
        using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
        using (var response = await writer.PutAsync(new Uri(tenant.Path("captured"), UriKind.Relative), null))
        {
            response.EnsureSuccessStatusCode();
        }

        using (var response = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("captured")}/collections", UriKind.Relative), new { name = "items" }))
        {
            response.EnsureSuccessStatusCode();
        }

        var a = EntityIds.New().ToString();
        var b = EntityIds.New().ToString();
        var c = EntityIds.New().ToString();
        await PutAsync(tenant, writer, a);
        await TerraBaseTestServer.ReplicatedAsync(sender);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var database = tenant.Database("captured");
        var source = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        var target = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        long initialOffset;
        await using (var read = await source.BeginTransactionAsync(write: false, deadline.Token))
        {
            initialOffset = await read.GetOffsetAsync(deadline.Token);
        }

        // Group 1 created the collection, group 2 put a.
        var expectedOffset = initialOffset + 1;
        var sequence = 3L;
        var expectedCount = 1;
        if (operation == "transaction")
        {
            using var response = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("captured")}/transactions", UriKind.Relative), new
            {
                ops = new object[]
                {
                    new { op = "delete", collection = "items", id = a },
                    new { op = "put", collection = "items", id = b, document = new { n = 2 } },
                    new { op = "put", collection = "items", id = c, document = new { n = 2 } },
                },
            }, deadline.Token);
            response.EnsureSuccessStatusCode();
            expectedCount = 3;
        }
        else if (operation == "drop")
        {
            await PutAsync(tenant, writer, b);
            using (var response = await writer.DeleteAsync(Document(tenant, a), deadline.Token))
            {
                response.EnsureSuccessStatusCode();
            }

            using (var response = await writer.DeleteAsync(new Uri($"{tenant.Path("captured")}/collections/items", UriKind.Relative), deadline.Token))
            {
                response.EnsureSuccessStatusCode();
            }

            // The drop's group holds a tombstone over the live document and the collection's own tombstone.
            sequence = 5;
            expectedOffset = initialOffset + 3;
            expectedCount = 2;
        }
        else
        {
            // The public transaction route rejects duplicate targets; the running application's engine covers these paths.
            await using var write = await source.BeginTransactionAsync(write: true, deadline.Token);
            switch (operation)
            {
                case "sameKey":
                    await write.PutAsync("items", a, "{\"z\":true}"u8.ToArray(), deadline.Token);
                    await write.TombstoneAsync("items", a, deadline.Token);
                    await write.PutAsync("items", a, "{\"a\":true}"u8.ToArray(), deadline.Token);
                    break;
                case "tombstonePut":
                    await write.TombstoneAsync("items", a, deadline.Token);
                    await write.PutAsync("items", a, "{\"a\":true}"u8.ToArray(), deadline.Token);
                    break;
                case "putTombstone":
                    await write.PutAsync("items", a, "{\"z\":true}"u8.ToArray(), deadline.Token);
                    await write.TombstoneAsync("items", a, deadline.Token);
                    break;
                case "rollbackDrop":
                    await Assert.That(await write.DropCollectionAsync("items", deadline.Token)).IsTrue();
                    break;
                default:
                    await Assert.That(await write.TombstoneAsync("items", a, deadline.Token)).IsNotNull();
                    break;
            }

            if (operation is "rollbackTombstone" or "rollbackDrop")
            {
                sequence = 2;
                expectedOffset = initialOffset;
            }
            else
            {
                await Assert.That(await write.CommitAsync(deadline.Token)).IsEqualTo(expectedOffset);
            }
        }

        await TerraBaseTestServer.ReplicatedAsync(sender);
        IReadOnlyList<DocumentDelta> changes;
        ReadOnlyMemory<byte> origin;
        await using (var sourceRead = await source.BeginTransactionAsync(write: false, deadline.Token))
        await using (var targetRead = await target.BeginTransactionAsync(write: false, deadline.Token))
        {
            origin = await sourceRead.GetOriginAsync(deadline.Token);
            await Assert.That(origin.Span[8..].SequenceEqual(source.Incarnation.ToByteArray().AsSpan(8, 8))).IsTrue();
            await Assert.That(await sourceRead.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(sequence);
            await Assert.That(await sourceRead.GetOffsetAsync(deadline.Token)).IsEqualTo(expectedOffset);
            await Assert.That(await targetRead.GetAppliedSequenceAsync(origin, deadline.Token)).IsEqualTo(sequence);
            changes = (await sourceRead.ReadChangesAsync(sequence, deadline.Token))!.Changes;
            await Assert.That(changes.Count).IsEqualTo(expectedCount);
            if (operation == "drop")
            {
                var record = changes.Single(change => change.Collection == DocumentDelta.Collections);
                await Assert.That(record.Id == "items" && Deltas.Json(record) is null).IsTrue();
                await Assert.That(await targetRead.GetCollectionAsync("items", deadline.Token)).IsNull();
                changes = [.. changes.Where(change => change.Collection == "items")];
            }

            foreach (var change in changes)
            {
                var winner = Version(change);
                await Assert.That(await sourceRead.GetVersionAsync("items", change.Id, deadline.Token)).IsEqualTo(winner);
                await Assert.That(await targetRead.GetVersionAsync("items", change.Id, deadline.Token)).IsEqualTo(winner);
                using var response = await reader.GetAsync(Document(tenant, change.Id), deadline.Token);
                if (Deltas.Json(change) is { } json)
                {
                    response.EnsureSuccessStatusCode();
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                    await Assert.That(body.RootElement.GetProperty("document").GetRawText()).IsEqualTo(json);
                }
                else
                {
                    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }
            }
        }

        if (operation == "drop")
        {
            await AssertLateWritesJoinAsync(tenant, peerTls, sender, receiver, source, target, writer, reader, origin, sequence, changes, deadline.Token);
        }
    }

    [Test]
    public async Task Empty_mutations_capture_nothing_and_system_removals_stay_physical_in_one_catalog_group()
    {
        using var tls = new ClientTls();
        // Only the measured transaction may add a catalog group: no background usage flush.
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services =>
        {
            foreach (var flush in services.Where(service => service.ImplementationType == typeof(UsageFlushService)).ToList())
            {
                services.Remove(flush);
            }
        });
        var tenant = await ReplicationTenant.CreateAsync(server, "captured");
        using var writer = tls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:write"));
        using (var response = await writer.PutAsync(new Uri(tenant.Path("captured"), UriKind.Relative), null))
        {
            response.EnsureSuccessStatusCode();
        }

        using (var response = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("captured")}/collections", UriKind.Relative), new { name = "items" }))
        {
            response.EnsureSuccessStatusCode();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var database = tenant.Database("captured");
        foreach (var store in new[]
        {
            (await engine.OpenDatabaseAsync(database, deadline.Token))!,
            await engine.OpenSystemDatabaseAsync(deadline.Token),
        })
        {
            long before;
            await using (var head = await store.BeginTransactionAsync(write: false, deadline.Token))
            {
                before = await head.GetLastChangeSequenceAsync(deadline.Token);
            }

            await using (var write = await store.BeginTransactionAsync(write: true, deadline.Token))
            {
                await Assert.That(await write.TombstoneAsync("capture-tests", "missing", deadline.Token)).IsNull();
                await Assert.That(await write.DropCollectionAsync("missing", deadline.Token)).IsFalse();
                if (store.Tenant is null)
                {
                    await Assert.That(await write.DeleteAsync("capture-tests", "missing", deadline.Token)).IsFalse();
                    await write.CreateCollectionAsync(new CollectionInfo("capture-tests", 1), deadline.Token);
                    await write.PutAsync("capture-tests", "physical", "{}"u8.ToArray(), deadline.Token);
                    await Assert.That(await write.DeleteAsync("capture-tests", "physical", deadline.Token)).IsTrue();
                    await Assert.That(await write.GetVersionAsync("capture-tests", "physical", deadline.Token)).IsNull();
                    await write.PutAsync("capture-tests", "dropped", "{}"u8.ToArray(), deadline.Token);
                    await Assert.That(await write.DropCollectionAsync("capture-tests", deadline.Token)).IsTrue();
                    await Assert.That(await write.GetVersionAsync("capture-tests", "dropped", deadline.Token)).IsNull();
                }
                else
                {
                    await Assert.That(await write.DropCollectionAsync("items", deadline.Token)).IsTrue();
                }

                await write.CommitAsync(deadline.Token);
            }

            // The system database captures its removals as one group for catalog followers (docs/decisions/0009); a client
            // database captures only the empty collection's tombstone.
            await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
            await Assert.That(await read.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(before + 1);
            await Assert.That(await read.GetVersionAsync("capture-tests", "physical", deadline.Token)).IsNull();
            await Assert.That(await read.GetVersionAsync("capture-tests", "dropped", deadline.Token)).IsNull();
            await Assert.That(await read.GetCollectionAsync(store.Tenant is null ? "capture-tests" : "items", deadline.Token)).IsNull();
        }
    }

    [Test]
    public async Task A_client_database_refuses_removals_without_a_trace()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        foreach (var prefix in new[] { false, true })
        {
            await using var write = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            await Assert.That(async () =>
            {
                if (prefix)
                {
                    await write.DeleteCollectionsAsync("ite", CancellationToken.None);
                }
                else
                {
                    await write.DeleteAsync("items", id, CancellationToken.None);
                }
            }).Throws<NotSupportedException>();
            await Assert.That(async () => await write.CommitAsync(CancellationToken.None)).Throws<InvalidOperationException>();
        }

        (await api.GetAsync(id)).Status.ShouldBe(HttpStatusCode.OK);
    }

    // Every unclean start of a peer is a new epoch and so a new origin. Applying the latest epoch's stream retires the
    // earlier positions, so local commits still name one position per peer after more epochs than a vector could hold.
    [Test]
    public async Task Local_commits_after_thousands_of_peer_epochs_depend_on_the_latest_only()
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        var tenant = await ReplicationTenant.CreateAsync(server, "captured");
        using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:read", "documents:write"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        using (var response = await writer.PutAsync(new Uri(tenant.Path("captured"), UriKind.Relative), null, token))
        {
            response.EnsureSuccessStatusCode();
        }

        var member = Deltas.NewReplica();
        var file = EntityIds.New().ToByteArray()[8..];
        byte[] Origin(int epoch)
        {
            return [.. BitConverter.GetBytes(ReplicaId.Of(member.Region, member.Member, checked((ushort)epoch)).Value), .. file];
        }

        const int Epochs = (OriginPositions.MaxCount * 4) + 1;
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("captured"), token))!;
        await using (var earlier = await store.BeginDurableTransactionAsync(token))
        {
            for (var epoch = 0; epoch < Epochs - 1; epoch++)
            {
                await earlier.SetAppliedSequenceAsync(Origin(epoch), 1, token);
            }

            await earlier.CommitAsync(token);
        }

        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var latest = Origin(Epochs - 1);
        await ApplyGroupAsync(new Features.Replication.V1.Replication.ReplicationClient(channel), tenant.Database("captured"), latest, 1,
            [Deltas.Write("items", EntityIds.New().ToString(), "{\"n\":0}")], token);
        await PutAsync(tenant, writer, EntityIds.New().ToString());
        await using var read = await store.BeginTransactionAsync(write: false, token);
        var group = (await read.ReadChangesAsync(await read.GetLastChangeSequenceAsync(token), token))!;
        await Assert.That(group.Dependencies.Select(position => (Convert.ToHexString(position.Origin.Span), position.Sequence)))
            .IsEquivalentTo([(Convert.ToHexString(latest), 1L)]);
        await Assert.That(await read.GetAppliedSequenceAsync(Origin(0), token)).IsEqualTo(0);
    }

    // After a drop, writes of the same documents from another member join with the tombstones: older ones stay hidden,
    // newer ones show, on the dropping member and its peer alike, and neither captures them; a local write then replaces all.
    private static async Task AssertLateWritesJoinAsync(
        ReplicationTenant tenant, PeerTls tls, TerraBaseTestServer sender, TerraBaseTestServer receiver, IDatabaseStore source, IDatabaseStore target,
        HttpClient writer, HttpClient reader, ReadOnlyMemory<byte> localOrigin, long localSequence,
        IReadOnlyList<DocumentDelta> deletions, CancellationToken cancellationToken)
    {
        using var sourceChannel = tls.CreateChannel(sender.PeerAddress!);
        using var targetChannel = tls.CreateChannel(receiver.PeerAddress!);
        var peers = new[]
        {
            (Store: source, Client: writer, Rpc: new Features.Replication.V1.Replication.ReplicationClient(sourceChannel)),
            (Store: target, Client: reader, Rpc: new Features.Replication.V1.Replication.ReplicationClient(targetChannel)),
        };
        var incomingOrigin = EntityIds.New().ToByteArray();
        var database = tenant.Database("captured");
        var sequence = 0L;
        foreach (var newer in new[] { false, true })
        {
            sequence++;
            var late = deletions.Select(change => Deltas.Write(
                "items", change.Id, newer ? "{\"newer\":true}" : "{\"older\":true}", stamp: new Hlc((ulong)(Version(change).Version + (newer ? 1 : -1))))).ToArray();
            foreach (var (store, http, rpc) in peers)
            {
                await ApplyGroupAsync(rpc, database, incomingOrigin, sequence, late, cancellationToken);
                await using var read = await store.BeginTransactionAsync(write: false, cancellationToken);
                await Assert.That(await read.GetAppliedSequenceAsync(incomingOrigin, cancellationToken)).IsEqualTo(sequence);
                await Assert.That(await read.GetLastChangeSequenceAsync(cancellationToken)).IsEqualTo(store == source ? localSequence : 0);
                foreach (var (deletion, write) in deletions.Zip(late))
                {
                    await Assert.That(await read.GetVersionAsync("items", deletion.Id, cancellationToken)).IsEqualTo(newer ? Version(write) : Version(deletion));
                    using var response = await http.GetAsync(Document(tenant, deletion.Id), cancellationToken);
                    await Assert.That(response.StatusCode).IsEqualTo(newer ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                }
            }
        }

        await PutAsync(tenant, writer, deletions[0].Id);
        await TerraBaseTestServer.ReplicatedAsync(sender);
        DocumentVersion? written = null;
        foreach (var (store, http, _) in peers)
        {
            await using var read = await store.BeginTransactionAsync(write: false, cancellationToken);
            var version = await read.GetVersionAsync("items", deletions[0].Id, cancellationToken);
            await Assert.That(version?.Deleted).IsFalse();
            written ??= version;
            await Assert.That(version).IsEqualTo(written);
            await Assert.That(await read.GetLastChangeSequenceAsync(cancellationToken)).IsEqualTo(store == source ? localSequence + 1 : 0);
            if (store == target)
            {
                await Assert.That(await read.GetAppliedSequenceAsync(localOrigin, cancellationToken)).IsEqualTo(localSequence + 1);
            }

            using var response = await http.GetAsync(Document(tenant, deletions[0].Id), cancellationToken);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            await Assert.That(body.RootElement.GetProperty("document").GetRawText()).IsEqualTo("{\"n\":1}");
        }
    }

    // What a document with exactly this register reads as.
    private static DocumentVersion Version(DocumentDelta change)
    {
        var winner = change.State.Values.MaxBy(value => (value.Value.Stamp.Value, value.Key.Replica.Value, value.Key.Counter)).Value;
        return new DocumentVersion(checked((long)winner.Stamp.Value), winner.IsTombstone);
    }

    private static async Task ApplyGroupAsync(
        Features.Replication.V1.Replication.ReplicationClient client, TenantContext database, ReadOnlyMemory<byte> origin,
        long sequence, IReadOnlyList<DocumentDelta> changes, CancellationToken cancellationToken)
    {
        using var call = client.Sync(cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(database, origin) }, cancellationToken);
        await Assert.That(await call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        var frontier = call.ResponseStream.Current.Welcome.Frontiers.Single();
        await Assert.That(frontier.DurableSequence).IsEqualTo((ulong)(sequence - 1));
        await Assert.That(frontier.AppliedSequence).IsEqualTo((ulong)(sequence - 1));
        await call.RequestStream.WriteAsync(new SyncRequest
        {
            CommittedGroup = ReplicationProtocol.CreateBatch(database, origin, sequence, new CapturedGroup([], changes)),
        }, cancellationToken);
        await Assert.That(await call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Durable.DurablePrefix).IsEqualTo((ulong)sequence);
        await Assert.That(await call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Applied.AppliedPrefix).IsEqualTo((ulong)sequence);
    }

    private static Uri Document(ReplicationTenant tenant, string id, string database = "captured") =>
        new($"{tenant.Path(database)}/collections/items/documents/by-key?id={id}", UriKind.Relative);

    private static async Task PutAsync(ReplicationTenant tenant, HttpClient writer, string id, string database = "captured")
    {
        using var response = await writer.PutAsJsonAsync(Document(tenant, id, database), new { n = 1 });
        response.EnsureSuccessStatusCode();
    }
}
