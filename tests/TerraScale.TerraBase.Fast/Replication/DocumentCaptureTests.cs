using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class DocumentCaptureTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_copy_keeps_physical_deletions_in_its_baseline_without_losing_newer_resurrections(bool drop)
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
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7().ToString("D")).ToArray();
        var old = ids.Select(id => new DocumentChange("items", id, 1, "{\"n\":1}"u8.ToArray())).ToArray();
        await using (var initial = await source.BeginTransactionAsync(write: true, token))
        {
            await initial.CreateCollectionAsync(new CollectionInfo("items", 0), token);
            foreach (var change in old)
            {
                await initial.PutAsync(change.Collection, change.Id, change.Json!.Value, token);
            }

            await initial.CommitAsync(token);
        }

        using var sourceChannel = peerTls.CreateChannel(sender.PeerAddress!);
        using var targetChannel = peerTls.CreateChannel(receiver.PeerAddress!);
        var sourceRpc = new Features.Replication.V1.Replication.ReplicationClient(sourceChannel);
        var targetRpc = new Features.Replication.V1.Replication.ReplicationClient(targetChannel);
        // The future copy's peer already holds the old live state when its new baseline arrives.
        await ApplyGroupAsync(targetRpc, copy, Guid.NewGuid().ToByteArray(bigEndian: true), 1, old, token);
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
                    await Assert.That(await deletion.DeleteAsync("items", id, token)).IsTrue();
                }
            }

            // Verify physical removal while this writer excludes background repair.
            foreach (var id in ids)
            {
                await Assert.That(await deletion.GetVersionAsync("items", id, token)).IsNull();
            }

            await Assert.That(await deletion.CommitAsync(token)).IsEqualTo(2);
        }

        await using (var local = await source.BeginTransactionAsync(write: true, token))
        {
            await local.CreateCollectionAsync(new CollectionInfo("items", 0), token);
            await Assert.That(await local.PutAsync("items", ids[2], "{\"local\":true}"u8.ToArray(), token)).IsEqualTo(3);
            await local.PutAsync("items", ids[1], "{}"u8.ToArray(), token);
            await local.CommitAsync(token);
        }

        // Multiple retained deletions of one key must materialize its newest version.
        await using (var again = await source.BeginTransactionAsync(write: true, token))
        {
            await again.DeleteAsync("items", ids[1], token);
            await Assert.That(await again.GetVersionAsync("items", ids[1], token)).IsNull();
            await Assert.That(await again.CommitAsync(token)).IsEqualTo(4);
        }

        var remoteOrigin = Guid.NewGuid().ToByteArray(bigEndian: true);
        await ApplyGroupAsync(sourceRpc, main, remoteOrigin, 1,
            [new DocumentChange("items", ids[3], 10, "{\"remote\":true}"u8.ToArray())], token);
        await Assert.That(await engine.DeleteDatabaseAsync(copy, token)).IsTrue();
        await Assert.That(await engine.CopyDatabaseAsync(main, copy, token)).IsTrue();
        var copied = (await engine.OpenDatabaseAsync(copy, token))!;
        await Assert.That(copied.Incarnation).IsNotEqualTo(source.Incarnation);
        await TerraBaseTestServer.ReplicatedAsync(sender);
        var target = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(copy, token))!;
        ReadOnlyMemory<byte> copyOrigin;
        long head;
        await using (var read = await copied.BeginTransactionAsync(write: false, token))
        {
            copyOrigin = await read.GetOriginAsync(token);
            await Assert.That(new Guid(copyOrigin.Span, bigEndian: true)).IsEqualTo(copied.Incarnation);
            await Assert.That(await read.GetAppliedSequenceAsync(remoteOrigin, token)).IsEqualTo(0);
            head = await read.GetLastChangeSequenceAsync(token);
            await Assert.That(head).IsEqualTo(2);
            var deletions = (await read.ReadChangesAsync(head, token))!;
            await Assert.That(deletions.Select(change => change.Id)).IsEquivalentTo(ids.Take(2));
            await Assert.That(deletions.All(change => change.Version == (change.Id == ids[0] ? 2 : 4) && change.Json is null)).IsTrue();
        }

        await using (var read = await source.BeginTransactionAsync(write: false, token))
        {
            // Repair may materialize the retained deletion, preserving its exact version and deleted state.
            foreach (var id in ids.Take(2))
            {
                var state = await read.GetVersionAsync("items", id, token);
                await Assert.That(state is null || state == new DocumentVersion(id == ids[0] ? 2 : 4, Deleted: true)).IsTrue();
                await Assert.That(await read.GetAsync("items", id, token)).IsNull();
            }

            await Assert.That(await read.GetLastChangeSequenceAsync(token)).IsEqualTo(4);
            await Assert.That(await read.GetAppliedSequenceAsync(remoteOrigin, token)).IsEqualTo(1);
        }

        // Both lower and equal-version delayed puts must lose after copying and after baseline import.
        var delayedOrigin = Guid.NewGuid().ToByteArray(bigEndian: true);
        foreach (var version in new[] { 1L, 2L })
        {
            var delayed = old.Select(change => change with { Version = version == 2 && change.Id == ids[1] ? 4 : version }).ToArray();
            await ApplyGroupAsync(sourceRpc, copy, delayedOrigin, version, delayed, token);
            await ApplyGroupAsync(targetRpc, copy, delayedOrigin, version, delayed, token);
            foreach (var (store, http) in new[] { (copied, writer), (target, reader) })
            {
                await using var read = await store.BeginTransactionAsync(write: false, token);
                await Assert.That(await read.GetAppliedSequenceAsync(delayedOrigin, token)).IsEqualTo(version);
                await Assert.That(await read.GetLastChangeSequenceAsync(token)).IsEqualTo(store == copied ? head : 0);
                foreach (var id in ids.Take(2))
                {
                    await Assert.That(await read.GetVersionAsync("items", id, token)).IsEqualTo(new DocumentVersion(id == ids[0] ? 2 : 4, Deleted: true));
                    using var response = await http.GetAsync(Document(tenant, id, "copied"), token);
                    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                await Assert.That((await read.GetAsync("items", ids[2], token))!.Json.Span.SequenceEqual("{\"local\":true}"u8)).IsTrue();
                await Assert.That((await read.GetAsync("items", ids[3], token))!.Json.Span.SequenceEqual("{\"remote\":true}"u8)).IsTrue();
                await Assert.That(await read.GetVersionAsync("items", ids[2], token)).IsEqualTo(new DocumentVersion(3, Deleted: false));
                await Assert.That(await read.GetVersionAsync("items", ids[3], token)).IsEqualTo(new DocumentVersion(10, Deleted: false));
                if (store == target)
                {
                    await Assert.That(await read.GetAppliedSequenceAsync(copyOrigin, token)).IsEqualTo(head);
                }
            }
        }

        // A local resurrection on the copy still allocates above the imported clock and reaches the peer.
        await PutAsync(tenant, writer, ids[0], "copied");
        await TerraBaseTestServer.ReplicatedAsync(sender);
        foreach (var store in new[] { copied, target })
        {
            await using var read = await store.BeginTransactionAsync(write: false, token);
            await Assert.That(await read.GetVersionAsync("items", ids[0], token)).IsEqualTo(new DocumentVersion(11, Deleted: false));
        }
    }

    [Test]
    [Arguments("transaction")]
    [Arguments("sameKey")]
    [Arguments("deletePut")]
    [Arguments("putDelete")]
    [Arguments("physicalDelete")]
    [Arguments("drop")]
    [Arguments("rollbackDelete")]
    [Arguments("rollbackDrop")]
    public async Task Document_mutations_replicate_with_the_shared_incarnation_and_commit_offset(string operation)
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

        var a = Guid.CreateVersion7().ToString("D");
        var b = Guid.CreateVersion7().ToString("D");
        var c = Guid.CreateVersion7().ToString("D");
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

            // The drop's group also holds the collection's tombstone.
            sequence = 5;
            expectedOffset = initialOffset + 3;
            expectedCount = 3;
        }
        else
        {
            // The public transaction route rejects duplicate targets and never exposes physical removal.
            // Use the running application's engine to cover these internal mutation paths.
            await using var write = await source.BeginTransactionAsync(write: true, deadline.Token);
            if (operation == "sameKey")
            {
                await write.PutAsync("items", a, "{\"z\":true}"u8.ToArray(), deadline.Token);
                await write.TombstoneAsync("items", a, deadline.Token);
                await write.PutAsync("items", a, "{\"a\":true}"u8.ToArray(), deadline.Token);
            }
            else if (operation == "deletePut")
            {
                await write.DeleteAsync("items", a, deadline.Token);
                await Assert.That(await write.GetVersionAsync("items", a, deadline.Token)).IsNull();
                await write.PutAsync("items", a, "{\"a\":true}"u8.ToArray(), deadline.Token);
            }
            else if (operation == "putDelete")
            {
                await write.PutAsync("items", a, "{\"z\":true}"u8.ToArray(), deadline.Token);
                await write.DeleteAsync("items", a, deadline.Token);
                await Assert.That(await write.GetVersionAsync("items", a, deadline.Token)).IsNull();
            }
            else if (operation == "rollbackDrop")
            {
                await Assert.That(await write.DropCollectionAsync("items", deadline.Token)).IsTrue();
                await Assert.That(await write.GetVersionAsync("items", a, deadline.Token)).IsNull();
            }
            else
            {
                await Assert.That(await write.DeleteAsync("items", a, deadline.Token)).IsTrue();
                await Assert.That(await write.GetVersionAsync("items", a, deadline.Token)).IsNull();
            }

            if (operation is "rollbackDelete" or "rollbackDrop")
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
        IReadOnlyList<DocumentChange> changes;
        ReadOnlyMemory<byte> origin;
        await using (var sourceRead = await source.BeginTransactionAsync(write: false, deadline.Token))
        await using (var targetRead = await target.BeginTransactionAsync(write: false, deadline.Token))
        {
            origin = await sourceRead.GetOriginAsync(deadline.Token);
            await Assert.That(new Guid(origin.Span, bigEndian: true)).IsEqualTo(source.Incarnation);
            await Assert.That(await sourceRead.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(sequence);
            await Assert.That(await targetRead.GetAppliedSequenceAsync(origin, deadline.Token)).IsEqualTo(sequence);
            changes = (await sourceRead.ReadChangesAsync(sequence, deadline.Token))!;
            await Assert.That(changes.Count).IsEqualTo(expectedCount);
            await Assert.That(changes.Select(change => change.Version).Distinct().Single()).IsEqualTo(expectedOffset);
            if (operation == "drop")
            {
                await Assert.That(changes.Single(change => change.Collection == DocumentChange.Collections))
                    .IsEqualTo(new DocumentChange(DocumentChange.Collections, "items", expectedOffset, null));
                await Assert.That(await targetRead.GetCollectionAsync("items", deadline.Token)).IsNull();
                changes = [.. changes.Where(change => change.Collection == "items")];
            }

            foreach (var change in changes)
            {
                var local = await sourceRead.GetVersionAsync("items", change.Id, deadline.Token);
                var expected = new DocumentVersion(change.Version, change.Json is null);
                if (operation is "physicalDelete" or "putDelete" or "drop")
                {
                    // Maintenance can restore the captured tombstone after the physical mutation committed.
                    await Assert.That(local is null || local == expected).IsTrue();
                    await Assert.That(await sourceRead.GetAsync("items", change.Id, deadline.Token)).IsNull();
                }
                else
                {
                    await Assert.That(local).IsEqualTo(expected);
                }

                var state = await targetRead.GetVersionAsync("items", change.Id, deadline.Token);
                await Assert.That(state!.Value.Version).IsEqualTo(change.Version);
                await Assert.That(state.Value.Deleted).IsEqualTo(change.Json is null);
                using var response = await reader.GetAsync(Document(tenant, change.Id), deadline.Token);
                if (change.Json is { } json)
                {
                    response.EnsureSuccessStatusCode();
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                    await Assert.That(body.RootElement.GetProperty("document").GetRawText()).IsEqualTo(System.Text.Encoding.UTF8.GetString(json.Span));
                }
                else
                {
                    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }
            }
        }

        if (operation is "physicalDelete" or "drop")
        {
            await AssertDeletionMergeAsync(tenant, peerTls, sender, receiver, source, target, writer, reader, origin, sequence, changes, deadline.Token);
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
                await Assert.That(await write.DeleteAsync("capture-tests", "missing", deadline.Token)).IsFalse();
                await Assert.That(await write.TombstoneAsync("capture-tests", "missing", deadline.Token)).IsNull();
                await Assert.That(await write.DropCollectionAsync("missing", deadline.Token)).IsFalse();
                if (store.Tenant is null)
                {
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

    private static async Task AssertDeletionMergeAsync(
        ReplicationTenant tenant, PeerTls tls, TerraBaseTestServer sender, TerraBaseTestServer receiver, IDatabaseStore source, IDatabaseStore target,
        HttpClient writer, HttpClient reader, ReadOnlyMemory<byte> localOrigin, long localSequence,
        IReadOnlyList<DocumentChange> deletions, CancellationToken cancellationToken)
    {
        using var sourceChannel = tls.CreateChannel(sender.PeerAddress!);
        using var targetChannel = tls.CreateChannel(receiver.PeerAddress!);
        var peers = new[]
        {
            (Store: source, Client: writer, Rpc: new Features.Replication.V1.Replication.ReplicationClient(sourceChannel)),
            (Store: target, Client: reader, Rpc: new Features.Replication.V1.Replication.ReplicationClient(targetChannel)),
        };
        var incomingOrigin = Guid.NewGuid().ToByteArray(bigEndian: true);
        var version = deletions[0].Version;
        var sequence = 0L;
        var database = tenant.Database("captured");
        CollectionInfo? metadata;
        CollectionInfo? targetMetadata;
        await using (var read = await source.BeginTransactionAsync(write: false, cancellationToken))
        {
            metadata = await read.GetCollectionAsync("items", cancellationToken);
        }

        await using (var read = await target.BeginTransactionAsync(write: false, cancellationToken))
        {
            targetMetadata = await read.GetCollectionAsync("items", cancellationToken);
        }

        // Delayed live bodies and tombstones must preserve deletion at both lower and equal versions.
        // Repair can materialize the exact retained tombstone; neither node recaptures imports.
        foreach (var delayedVersion in new[] { version - 1, version })
        {
            foreach (var tombstone in new[] { false, true })
            {
                sequence++;
                var delayed = deletions.Select(change => change with
                {
                    Version = delayedVersion,
                    Json = tombstone ? null : (ReadOnlyMemory<byte>?)"{\"z\":true}"u8.ToArray(),
                }).ToArray();
                foreach (var (store, http, rpc) in peers)
                {
                    await ApplyGroupAsync(rpc, database, incomingOrigin, sequence, delayed, cancellationToken);
                    await using var read = await store.BeginTransactionAsync(write: false, cancellationToken);
                    await Assert.That(await read.GetAppliedSequenceAsync(incomingOrigin, cancellationToken)).IsEqualTo(sequence);
                    await Assert.That(await read.GetLastChangeSequenceAsync(cancellationToken)).IsEqualTo(store == source ? localSequence : 0);
                    foreach (var deletion in deletions)
                    {
                        await Assert.That(await read.GetAsync("items", deletion.Id, cancellationToken)).IsNull();
                        var state = await read.GetVersionAsync("items", deletion.Id, cancellationToken);
                        var expected = new DocumentVersion(version, Deleted: true);
                        if (store == source)
                        {
                            await Assert.That(state is null || state == expected).IsTrue();
                        }
                        else
                        {
                            await Assert.That(state).IsEqualTo(expected);
                        }

                        using var response = await http.GetAsync(Document(tenant, deletion.Id), cancellationToken);
                        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                    }

                    await Assert.That(await read.GetCollectionAsync("items", cancellationToken)).IsEqualTo(store == source ? metadata : targetMetadata);
                }
            }
        }

        // Higher live writes and higher tombstones still win over retained deletion evidence.
        foreach (var tombstone in new[] { false, true })
        {
            sequence++;
            version++;
            var newer = deletions.Select(change => change with
            {
                Version = version,
                Json = tombstone ? null : (ReadOnlyMemory<byte>?)"{\"newer\":true}"u8.ToArray(),
            }).ToArray();
            foreach (var (store, http, rpc) in peers)
            {
                await ApplyGroupAsync(rpc, database, incomingOrigin, sequence, newer, cancellationToken);
                await using var read = await store.BeginTransactionAsync(write: false, cancellationToken);
                await Assert.That(await read.GetAppliedSequenceAsync(incomingOrigin, cancellationToken)).IsEqualTo(sequence);
                await Assert.That(await read.GetLastChangeSequenceAsync(cancellationToken)).IsEqualTo(store == source ? localSequence : 0);
                foreach (var change in newer)
                {
                    await Assert.That(await read.GetVersionAsync("items", change.Id, cancellationToken)).IsEqualTo(new DocumentVersion(version, tombstone));
                    var state = await read.GetAsync("items", change.Id, cancellationToken);
                    await Assert.That(state is null).IsEqualTo(tombstone);
                    using var response = await http.GetAsync(Document(tenant, change.Id), cancellationToken);
                    await Assert.That(response.StatusCode).IsEqualTo(tombstone ? HttpStatusCode.NotFound : HttpStatusCode.OK);
                    if (state is not null)
                    {
                        await Assert.That(state.Json.Span.SequenceEqual(change.Json!.Value.Span)).IsTrue();
                    }
                }
            }
        }

        // The shared clock observes imports; the next local write supersedes them and still replicates.
        long nextLocalVersion;
        await using (var read = await source.BeginTransactionAsync(write: false, cancellationToken))
        {
            nextLocalVersion = await read.GetOffsetAsync(cancellationToken) + 1;
        }

        await PutAsync(tenant, writer, deletions[0].Id);
        await TerraBaseTestServer.ReplicatedAsync(sender);
        foreach (var (store, http, _) in peers)
        {
            await using var read = await store.BeginTransactionAsync(write: false, cancellationToken);
            await Assert.That(await read.GetVersionAsync("items", deletions[0].Id, cancellationToken)).IsEqualTo(new DocumentVersion(nextLocalVersion, Deleted: false));
            await Assert.That(await read.GetLastChangeSequenceAsync(cancellationToken)).IsEqualTo(store == source ? localSequence + 1 : 0);
            await Assert.That(await read.GetAppliedSequenceAsync(incomingOrigin, cancellationToken)).IsEqualTo(sequence);
            if (store == target)
            {
                await Assert.That(await read.GetAppliedSequenceAsync(localOrigin, cancellationToken)).IsEqualTo(localSequence + 1);
            }

            using var response = await http.GetAsync(Document(tenant, deletions[0].Id), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    private static async Task ApplyGroupAsync(
        Features.Replication.V1.Replication.ReplicationClient client, TenantContext database, ReadOnlyMemory<byte> origin,
        long sequence, IReadOnlyList<DocumentChange> changes, CancellationToken cancellationToken)
    {
        using var call = client.Sync(cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(database, origin) }, cancellationToken);
        await Assert.That(await call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        var frontier = call.ResponseStream.Current.Welcome.Frontiers.Single();
        await Assert.That(frontier.DurableSequence).IsEqualTo((ulong)(sequence - 1));
        await Assert.That(frontier.AppliedSequence).IsEqualTo((ulong)(sequence - 1));
        await call.RequestStream.WriteAsync(new SyncRequest
        {
            CommittedGroup = ReplicationProtocol.CreateBatch(database, origin, sequence, changes, frontier.JournalDigest.Memory),
        }, cancellationToken);
        await Assert.That(await call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Durable.DurablePrefix).IsEqualTo((ulong)sequence);
        await Assert.That(await call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Applied.AppliedPrefix).IsEqualTo((ulong)sequence);
    }

    private static Uri Document(ReplicationTenant tenant, string id, string database = "captured") =>
        new($"{tenant.Path(database)}/collections/items/documents/{id}", UriKind.Relative);

    private static async Task PutAsync(ReplicationTenant tenant, HttpClient writer, string id, string database = "captured")
    {
        using var response = await writer.PutAsJsonAsync(Document(tenant, id, database), new { n = 1 });
        response.EnsureSuccessStatusCode();
    }
}
