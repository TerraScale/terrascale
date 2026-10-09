using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class CaptureAdmissionTests
{
    [Test]
    [Arguments("long")]
    [Arguments("control")]
    [Arguments("surrogate")]
    [Arguments("collection")]
    [Arguments("empty")]
    public async Task Invalid_keys_cannot_commit_and_admitted_boundary_keys_replicate(string invalid)
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
        using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        using var created = await client.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" }, token);
        created.EnsureSuccessStatusCode();
        var source = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        var collection = invalid == "collection" ? "BadCollection" : "items";
        var id = invalid switch
        {
            "long" => new string('a', 257),
            "control" => "bad\nkey",
            "surrogate" => "bad\ud800key",
            "empty" => string.Empty,
            _ => "key",
        };
        long offset;
        await using (var read = await source.BeginTransactionAsync(write: false, token))
        {
            offset = await read.GetOffsetAsync(token);
        }

        foreach (var imported in new[] { false, true })
        {
            await using (var write = await source.BeginTransactionAsync(write: true, token))
            {
                if (imported)
                {
                    await Assert.That(async () => await write.ApplyAsync(new DocumentChange(collection, id, 1, "{}"u8.ToArray()), token))
                        .Throws<InvalidDataException>();
                }
                else
                {
                    await write.PutAsync("items", "must-rollback", "{}"u8.ToArray(), token);
                    await Assert.That(async () => await write.PutAsync(collection, id, "{}"u8.ToArray(), token)).Throws<InvalidDataException>();
                }

                await Assert.That(async () => await write.CommitAsync(token)).Throws<InvalidOperationException>();
            }

            // Group 1 created the collection.
            await using var read = await source.BeginTransactionAsync(write: false, token);
            await Assert.That(await read.GetLastChangeSequenceAsync(token)).IsEqualTo(1);
            await Assert.That(await read.GetOffsetAsync(token)).IsEqualTo(offset);
            await Assert.That(await read.GetAsync("items", "must-rollback", token)).IsNull();
        }

        var boundaryIds = new[] { new string('a', 256), new string('é', 256) };
        await using (var write = await source.BeginTransactionAsync(write: true, token))
        {
            foreach (var boundary in boundaryIds)
            {
                await write.PutAsync("items", boundary, "{\"admitted\":true}"u8.ToArray(), token);
            }

            await write.CommitAsync(token);
        }

        var following = EntityIds.New().ToString();
        using var added = await client.PutAsJsonAsync(new Uri($"{tenant.Path("db")}/collections/items/documents/by-key?id={following}", UriKind.Relative), new { after = "rejection" }, token);
        added.EnsureSuccessStatusCode();
        await TerraBaseTestServer.ReplicatedAsync(sender);
        await using var local = await source.BeginTransactionAsync(write: false, token);
        await Assert.That(await local.GetLastChangeSequenceAsync(token)).IsEqualTo(3);
        for (var sequence = 2L; sequence <= 3; sequence++)
        {
            var group = (await local.ReadChangesAsync(sequence, token))!;
            var decoded = ChangeGroup.Decode(ChangeGroup.Encode(group));
            await Assert.That(decoded.Select(change => (change.Collection, change.Id, change.Version)))
                .IsEquivalentTo(group.Select(change => (change.Collection, change.Id, change.Version)));
            foreach (var change in decoded)
            {
                using var response = await reader.GetAsync(new Uri($"{tenant.Path("db")}/collections/items/documents/by-key?id={Uri.EscapeDataString(change.Id)}", UriKind.Relative), token);
                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
                await Assert.That(body.RootElement.GetProperty("document").GetRawText()).IsEqualTo(Encoding.UTF8.GetString(change.Json!.Value.Span));
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Dropping_a_collection_releases_pending_bodies_before_admitting_other_deletions(bool capturedFirst)
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
        string[] names = ["items", "other"];
        foreach (var name in names)
        {
            using var created = await client.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name }, token);
            created.EnsureSuccessStatusCode();
        }

        const string early = "00000000000000000000000001";
        const string late = "7ZZZZZZZZZZZZZZZZZZZZZZZZZ";
        var captured = capturedFirst ? early : late;
        var existing = capturedFirst ? late : early;
        var retained = EntityIds.New().ToString();
        using var seeded = await client.PutAsJsonAsync(new Uri($"{tenant.Path("db")}/collections/items/documents/by-key?id={existing}", UriKind.Relative), new { value = "old" }, token);
        seeded.EnsureSuccessStatusCode();
        // Two names (5 bytes), ULIDs (26 bytes) and these bodies fill exactly the 2 MiB capture budget.
        var json = Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', IStorageTransaction.MaxChangeGroupBytes / 2 - 31 - 12) + "\"}");
        var source = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        long offset;
        await using (var write = await source.BeginTransactionAsync(write: true, token))
        {
            await write.PutAsync("other", retained, json, token);
            await write.PutAsync("items", captured, json, token);
            await Assert.That(await write.DropCollectionAsync("items", token)).IsTrue();
            offset = await write.CommitAsync(token);
        }

        await TerraBaseTestServer.ReplicatedAsync(sender);
        await using var local = await source.BeginTransactionAsync(write: false, token);
        // Groups 1 and 2 created the collections, 3 seeded; the drop's group also holds the collection's tombstone.
        var group = (await local.ReadChangesAsync(4, token))!;
        await Assert.That(group.Count).IsEqualTo(4);
        await Assert.That(group.Single(change => change.Id == retained).Json!.Value.Span.SequenceEqual(json)).IsTrue();
        await Assert.That(group.Where(change => change.Collection == "items").All(change => change.Json is null && change.Version == offset)).IsTrue();
        await Assert.That(group.Single(change => change.Collection == DocumentChange.Collections)).IsEqualTo(new DocumentChange(DocumentChange.Collections, "items", offset, null));
        var target = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        await using var remote = await target.BeginTransactionAsync(write: false, token);
        await Assert.That(await remote.GetAppliedSequenceAsync(await local.GetOriginAsync(token), token)).IsEqualTo(4);
        await Assert.That(await remote.GetCollectionAsync("items", token)).IsNull();
        var received = (await remote.GetAsync("other", retained, token))!;
        await Assert.That(received.Json.Span.SequenceEqual(json)).IsTrue();
        await Assert.That(await remote.GetVersionAsync("items", existing, token)).IsEqualTo(new DocumentVersion(offset, Deleted: true));
        await Assert.That(await remote.GetVersionAsync("items", captured, token)).IsEqualTo(new DocumentVersion(offset, Deleted: true));
    }

    [Test]
    public async Task Dropping_a_collection_replaces_every_pending_state_at_the_capture_count_limit()
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
        using var created = await client.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" }, token);
        created.EnsureSuccessStatusCode();
        var source = (await sender.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        // The drop's group also holds the collection's own tombstone.
        var ids = Enumerable.Range(0, IStorageTransaction.MaxChangeGroupChanges - 1).Select(_ => EntityIds.New().ToString()).ToArray();
        var json = JsonSerializer.SerializeToUtf8Bytes(new { value = new Faker().Lorem.Word() });
        long offset;
        await using (var write = await source.BeginTransactionAsync(write: true, token))
        {
            // Internal mutations may share a key; the public transaction API refuses duplicate targets.
            foreach (var id in ids)
            {
                await write.PutAsync("items", id, json, token);
            }

            await Assert.That(await write.DropCollectionAsync("items", token)).IsTrue();
            offset = await write.CommitAsync(token);
        }

        await TerraBaseTestServer.ReplicatedAsync(sender);
        await using var local = await source.BeginTransactionAsync(write: false, token);
        await Assert.That(await local.GetCollectionAsync("items", token)).IsNull();
        // Group 1 created the collection.
        var group = (await local.ReadChangesAsync(2, token))!;
        await Assert.That(group.Count).IsEqualTo(IStorageTransaction.MaxChangeGroupChanges);
        await Assert.That(group.Where(change => change.Collection == "items").Select(change => change.Id)).IsEquivalentTo(ids);
        await Assert.That(group.All(change => change.Json is null && change.Version == offset)).IsTrue();
        var target = (await receiver.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        await using var remote = await target.BeginTransactionAsync(write: false, token);
        await Assert.That(await remote.GetAppliedSequenceAsync(await local.GetOriginAsync(token), token)).IsEqualTo(2);
        foreach (var id in ids)
        {
            await Assert.That(await remote.GetVersionAsync("items", id, token)).IsEqualTo(new DocumentVersion(offset, Deleted: true));
        }
    }
}
