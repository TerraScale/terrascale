using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed partial class UniqueReplicationTests
{
    private static readonly string[] IncompatibleFields = ["email", "k1", "k2", "k3", "k4", "k5"];

    [Test]
    public async Task A_delayed_schema_and_collection_drop_reconcile_more_than_a_source_groups_count_and_byte_limits()
    {
        using var tls = new PeerTls();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var origin = await ManagementApi.StartAsync(tls.Apply);
        await using var receiver = await ManagementApi.StartAsync(tls.Apply);
        await using var dropOrigin = await ManagementApi.StartAsync(tls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(origin.Server, ["db"], token);
        await tenant.BindAsync(receiver.Server);
        await tenant.BindAsync(dropOrigin.Server);
        using var client = receiver.Tls.CreateClient(receiver.Server.ClientAddress!,
            await tenant.KeyAsync(receiver.Server, ["documents:read", "documents:write", "shapes:read"], token));
        using (var created = await client.PostAsJsonAsync(new Uri(tenant.Path("db") + "/collections", UriKind.Relative), new { name = "people" }, token))
        {
            created.EnsureSuccessStatusCode();
        }

        var ids = Enumerable.Range(0, 1100).Select(_ => Ulid.NewUlid().ToString()).Order(StringComparer.Ordinal).ToArray();
        var email = new Faker().Internet.Email();
        foreach (var page in ids.Chunk(100))
        {
            using var written = await client.PostAsJsonAsync(new Uri(tenant.Path("db") + "/transactions", UriKind.Relative), new
            {
                ops = page.Select(id => new { op = "put", collection = "people", id, document = new { email, padding = new string('x', 10 * 1024) } }).ToArray(),
            }, token);
            written.EnsureSuccessStatusCode();
        }

        var store = (await origin.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        await using (var declaration = await store.BeginTransactionAsync(write: true, token))
        {
            await declaration.CreateCollectionAsync(new CollectionInfo("people", 0), token);
            await declaration.SetUniqueConstraintsAsync("people", [new UniqueConstraint("email", ["email"])], token);
            await declaration.CommitAsync(token);
        }

        var receiving = (await receiver.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        long previous;
        await using (var read = await receiving.BeginTransactionAsync(write: false, token))
        {
            previous = await read.GetOffsetAsync(token);
        }

        await ReplayAsync(origin.Server, receiver.Server, tls, tenant.Database("db"), token);
        await VisibleAsync(client, tenant, ids[0], token);
        using (var conflict = await client.GetAsync(Document(tenant, ids[^1]), token))
        {
            await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        }

        await using (var read = await receiving.BeginTransactionAsync(write: false, token))
        {
            var changed = 0;
            await foreach (var change in read.ChangesAsync("people", null, previous, await read.GetOffsetAsync(token), token))
            {
                await Assert.That(change.Deleted).IsTrue();
                changed++;
            }

            await Assert.That(changed).IsEqualTo(ids.Length - 1);
        }

        foreach (var constrained in new[] { false, true })
        {
            using var shape = await client.GetAsync(new Uri(tenant.Path("db") + "/collections/people/shape?offset=-1", UriKind.Relative), token);
            await Assert.That(shape.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
            var handle = shape.Headers.GetValues("TB-Shape-Handle").Single();
            long shapeOffset;
            await using (var read = await receiving.BeginTransactionAsync(write: false, token))
            {
                shapeOffset = await read.GetOffsetAsync(token);
            }

            await using (var declaration = await store.BeginTransactionAsync(write: true, token))
            {
                await declaration.SetUniqueConstraintsAsync("people", constrained ? [new UniqueConstraint("email", ["email"])] : [], token);
                await declaration.CommitAsync(token);
            }

            await ReplayAsync(origin.Server, receiver.Server, tls, tenant.Database("db"), token);
            using var projected = await client.GetAsync(Document(tenant, ids[^1]), token);
            await Assert.That(projected.StatusCode).IsEqualTo(constrained ? HttpStatusCode.Conflict : HttpStatusCode.OK);
            if (!constrained)
            {
                using var expired = await client.GetAsync(new Uri(tenant.Path("db") + $"/collections/people/shape?offset={shapeOffset}&handle={handle}", UriKind.Relative), token);
                await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                var error = await expired.Content.ReadFromJsonAsync<JsonElement>(token);
                await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("must_refetch");
                using var restart = await client.GetAsync(new Uri(tenant.Path("db") + "/collections/people/shape?offset=-1", UriKind.Relative), token);
                using var snapshot = await client.GetAsync(restart.Headers.Location, token);
                snapshot.EnsureSuccessStatusCode();
                var page = await snapshot.Content.ReadFromJsonAsync<JsonElement>(token);
                await Assert.That(page.GetProperty("changes")[0].GetProperty("ops").GetArrayLength()).IsGreaterThan(1);
            }
        }

        await using (var read = await receiving.BeginTransactionAsync(write: false, token))
        {
            previous = await read.GetOffsetAsync(token);
        }

        using (var removedLoser = await client.DeleteAsync(Document(tenant, ids[^1]), token))
        {
            removedLoser.EnsureSuccessStatusCode();
        }

        using (var repairedLoser = await client.PutAsJsonAsync(Document(tenant, ids[^2]), new { email = "repaired", padding = new string('x', 10 * 1024) }, token))
        {
            repairedLoser.EnsureSuccessStatusCode();
        }

        await VisibleAsync(client, tenant, ids[^2], token);
        using (var removedWinner = await client.DeleteAsync(Document(tenant, ids[0]), token))
        {
            removedWinner.EnsureSuccessStatusCode();
        }

        await VisibleAsync(client, tenant, ids[1], token);
        using (var stillHidden = await client.GetAsync(Document(tenant, ids[2]), token))
        {
            await Assert.That(stillHidden.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        }

        await using (var read = await receiving.BeginTransactionAsync(write: false, token))
        {
            var changes = new List<(string Id, bool Deleted)>();
            await foreach (var change in read.ChangesAsync("people", null, previous, await read.GetOffsetAsync(token), token))
            {
                changes.Add((change.Id, change.Deleted));
            }

            await Assert.That(changes).IsEquivalentTo([(ids[^1], true), (ids[^2], false), (ids[0], true), (ids[1], false)]);
        }

        // An origin that has not received the schema may drop its empty collection while this receiver retains over 1024 states.
        using var dropping = dropOrigin.Tls.CreateClient(dropOrigin.Server.ClientAddress!,
            await tenant.KeyAsync(dropOrigin.Server, ["documents:read", "documents:write"], token));
        using (var created = await dropping.PostAsJsonAsync(new Uri(tenant.Path("db") + "/collections", UriKind.Relative), new { name = "people" }, token))
        {
            created.EnsureSuccessStatusCode();
        }

        var droppingStore = (await dropOrigin.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        await using (var clock = await droppingStore.BeginTransactionAsync(write: true, token))
        {
            await clock.ApplyAsync(new DocumentChange("clock", Ulid.NewUlid().ToString(), previous + 100, null), token);
            await clock.CommitAsync(token);
        }

        using (var dropped = await dropping.DeleteAsync(new Uri(tenant.Path("db") + "/collections/people", UriKind.Relative), token))
        {
            dropped.EnsureSuccessStatusCode();
        }

        await ReplayAsync(dropOrigin.Server, receiver.Server, tls, tenant.Database("db"), token);
        using var absent = await client.GetAsync(Document(tenant, ids[0]), token);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await using var empty = await receiving.BeginTransactionAsync(write: false, token);
        await Assert.That(await empty.GetCollectionAsync("people", token)).IsNull();
        await Assert.That(await empty.GetUniqueConstraintsAsync("people", token)).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Incompatible_source_states_are_retained_as_conflicts_in_either_schema_delivery_order(bool schemaFirst)
    {
        using var tls = new PeerTls();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var schemaOrigin = await ManagementApi.StartAsync(tls.Apply);
        await using var documentOrigin = await ManagementApi.StartAsync(tls.Apply);
        await using var receiver = await ManagementApi.StartAsync(tls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(schemaOrigin.Server, ["db"], token);
        await tenant.BindAsync(documentOrigin.Server);
        await tenant.BindAsync(receiver.Server);
        using var source = documentOrigin.Tls.CreateClient(documentOrigin.Server.ClientAddress!,
            await tenant.KeyAsync(documentOrigin.Server, ["documents:read", "documents:write"], token));
        using (var created = await source.PostAsJsonAsync(new Uri(tenant.Path("db") + "/collections", UriKind.Relative), new { name = "people" }, token))
        {
            created.EnsureSuccessStatusCode();
        }

        var invalid = new[]
        {
            new { email = (object)Array.Empty<string>(), k1 = "", k2 = "", k3 = "", k4 = "", k5 = "" },
            new { email = (object)new { value = "object" }, k1 = "", k2 = "", k3 = "", k4 = "", k5 = "" },
            new { email = (object)new string('x', 17 * 1024), k1 = "", k2 = "", k3 = "", k4 = "", k5 = "" },
            new { email = (object)"shared", k1 = new string('a', 14000), k2 = new string('b', 14000), k3 = new string('c', 14000), k4 = new string('d', 14000), k5 = new string('e', 14000) },
        };
        var ids = invalid.Select(_ => Ulid.NewUlid().ToString()).ToArray();
        for (var index = 0; index < ids.Length; index++)
        {
            using var written = await source.PutAsJsonAsync(Document(tenant, ids[index]), invalid[index], token);
            written.EnsureSuccessStatusCode();
        }

        var store = (await schemaOrigin.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        await using (var declaration = await store.BeginTransactionAsync(write: true, token))
        {
            await declaration.CreateCollectionAsync(new CollectionInfo("people", 0), token);
            await declaration.SetUniqueConstraintsAsync("people", [.. IncompatibleFields.Select(field => new UniqueConstraint(field, [field]))], token);
            await declaration.CommitAsync(token);
        }

        ManagementApi[] origins = schemaFirst ? [schemaOrigin, documentOrigin] : [documentOrigin, schemaOrigin];
        foreach (var origin in origins)
        {
            await ReplayAsync(origin.Server, receiver.Server, tls, tenant.Database("db"), token);
        }

        using var client = receiver.Tls.CreateClient(receiver.Server.ClientAddress!,
            await tenant.KeyAsync(receiver.Server, ["documents:read", "documents:write"], token));
        foreach (var id in ids)
        {
            using var conflict = await client.GetAsync(Document(tenant, id), token);
            await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(conflict.Headers.ETag).IsNotNull();
        }

        using (var admitted = await client.PutAsJsonAsync(Document(tenant, Ulid.NewUlid().ToString()), new { email = "shared" }, token))
        {
            admitted.EnsureSuccessStatusCode();
        }

        using (var repaired = await client.PutAsJsonAsync(Document(tenant, ids[0]), new { email = "repaired" }, token))
        {
            repaired.EnsureSuccessStatusCode();
        }

        await VisibleAsync(client, tenant, ids[0], token);
        using var deleted = await client.DeleteAsync(Document(tenant, ids[1]), token);
        deleted.EnsureSuccessStatusCode();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Partitioned_unique_writes_converge_and_a_deleted_winner_releases_its_key(bool reverse)
    {
        using var tls = new PeerTls();
        var firstData = Directory.CreateTempSubdirectory("terrabase-unique-first-").FullName;
        var secondData = Directory.CreateTempSubdirectory("terrabase-unique-second-").FullName;
        var low = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        var high = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
        var email = new Faker().Internet.Email().ToLowerInvariant();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        try
        {
            ReplicationTenant tenant;
            await using (var first = await ManagementApi.StartAsync(settings => { tls.Apply(settings); settings["data-directory"] = firstData; }))
            await using (var second = await ManagementApi.StartAsync(settings => { tls.Apply(settings); settings["data-directory"] = secondData; }))
            {
                tenant = await ReplicationTenant.CreateAsync(first.Server, ["db", "snapshot"], token);
                await tenant.BindAsync(second.Server);
                await SeedAsync(first, tenant, reverse ? low : high, email, token);
                await SeedAsync(second, tenant, reverse ? high : low, email.ToUpperInvariant(), token);
            }

            await using var b = await ManagementApi.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["data-directory"] = secondData;
            });
            await using var a = await ManagementApi.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["data-directory"] = firstData;
                settings["peer-addresses"] = b.Server.PeerAddress!.ToString();
            });
            await ReplayAsync(b.Server, a.Server, tls, tenant.Database("db"), token);
            await TerraBaseTestServer.ReplicatedAsync(a.Server);

            using var firstClient = a.Tls.CreateClient(a.Server.ClientAddress!, await tenant.KeyAsync(a.Server, "documents:read", "documents:write", "shapes:read"));
            using var secondClient = b.Tls.CreateClient(b.Server.ClientAddress!, await tenant.KeyAsync(b.Server, "documents:read", "documents:write", "shapes:read"));
            foreach (var client in new[] { firstClient, secondClient })
            {
                await VisibleAsync(client, tenant, low, token);
                using var conflict = await client.GetAsync(Document(tenant, high), token);
                await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                var error = await conflict.Content.ReadFromJsonAsync<JsonElement>(token);
                await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unique_violation");
                await Assert.That(conflict.Headers.ETag).IsNotNull();
                using var listed = await client.GetAsync(new Uri(tenant.Path("db") + "/collections/people/documents", UriKind.Relative), token);
                listed.EnsureSuccessStatusCode();
                var page = await listed.Content.ReadFromJsonAsync<JsonElement>(token);
                await Assert.That(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([low]);
            }

            var engine = a.Server.Services.GetRequiredService<IStorageEngine>();
            await Assert.That(await engine.DeleteDatabaseAsync(tenant.Database("snapshot"), token)).IsTrue();
            await Assert.That(await engine.CopyDatabaseAsync(tenant.Database("db"), tenant.Database("snapshot"), token)).IsTrue();
            await TerraBaseTestServer.ReplicatedAsync(a.Server);
            using (var baselineConflict = await secondClient.GetAsync(Document(tenant, high, "snapshot"), token))
            {
                await Assert.That(baselineConflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            }

            using (var snapshotDelete = await firstClient.DeleteAsync(Document(tenant, low, "snapshot"), token))
            {
                snapshotDelete.EnsureSuccessStatusCode();
            }

            await TerraBaseTestServer.ReplicatedAsync(a.Server);
            using (var baselineRestored = await secondClient.GetAsync(Document(tenant, high, "snapshot"), token))
            {
                baselineRestored.EnsureSuccessStatusCode();
            }

            using var deleted = await firstClient.DeleteAsync(Document(tenant, low), token);
            deleted.EnsureSuccessStatusCode();
            await TerraBaseTestServer.ReplicatedAsync(a.Server);
            await VisibleAsync(firstClient, tenant, high, token);
            await VisibleAsync(secondClient, tenant, high, token);
            using var duplicate = await secondClient.PutAsJsonAsync(Document(tenant, Ulid.NewUlid().ToString()), new { email }, token);
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        }
        finally
        {
            Directory.Delete(firstData, recursive: true);
            Directory.Delete(secondData, recursive: true);
        }
    }

    private static async Task ReplayAsync(TerraBaseTestServer source, TerraBaseTestServer target, PeerTls tls, TenantContext database, CancellationToken token)
    {
        var store = (await source.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token))!;
        await using var read = await store.BeginTransactionAsync(write: false, token);
        var origin = await read.GetOriginAsync(token);
        var head = await read.GetLastChangeSequenceAsync(token);
        using var channel = tls.CreateChannel(target.PeerAddress!);
        var rpc = new Features.Replication.V1.Replication.ReplicationClient(channel);
        using var call = rpc.Sync(cancellationToken: token);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(database, origin) }, token);
        await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
        var frontier = call.ResponseStream.Current.Welcome.Frontiers.Single();
        var digest = frontier.JournalDigest.Memory;
        for (var sequence = checked((long)frontier.AppliedSequence + 1); sequence <= head; sequence++)
        {
            var changes = (await read.ReadChangesAsync(sequence, token))!;
            await call.RequestStream.WriteAsync(new SyncRequest
            {
                CommittedGroup = ReplicationProtocol.CreateBatch(database, origin, sequence, changes, digest),
            }, token);
            await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Durable.DurablePrefix).IsEqualTo((ulong)sequence);
            await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Applied.AppliedPrefix).IsEqualTo((ulong)sequence);
            digest = call.ResponseStream.Current.Applied.JournalDigest.Memory;
        }
    }

    private static async Task SeedAsync(ManagementApi api, ReplicationTenant tenant, string id, string email, CancellationToken token)
    {
        using var data = api.Tls.CreateClient(api.Server.ClientAddress!, await tenant.KeyAsync(api.Server, ["documents:read", "documents:write"], token));
        using var create = await data.PostAsJsonAsync(new Uri(tenant.Path("db") + "/collections", UriKind.Relative), new { name = "people" }, token);
        create.EnsureSuccessStatusCode();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        await using (var transaction = await store.BeginTransactionAsync(write: true, token))
        {
            await transaction.SetUniqueConstraintsAsync("people", [new UniqueConstraint("email", ["email"], "lowercase")], token);
            await transaction.CommitAsync(token);
        }
        using var put = await data.PutAsJsonAsync(Document(tenant, id), new { email }, token);
        put.EnsureSuccessStatusCode();
    }

    private static async Task VisibleAsync(HttpClient client, ReplicationTenant tenant, string id, CancellationToken token)
    {
        using var read = await client.GetAsync(Document(tenant, id), token);
        read.EnsureSuccessStatusCode();
        var document = await read.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(document.GetProperty("id").GetString()).IsEqualTo(id);
    }

    private static Uri Document(ReplicationTenant tenant, string id, string database = "db") => new(tenant.Path(database) + "/collections/people/documents/by-key?id=" + Uri.EscapeDataString(id), UriKind.Relative);
}
