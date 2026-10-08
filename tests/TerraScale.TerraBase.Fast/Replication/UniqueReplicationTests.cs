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

public sealed class UniqueReplicationTests
{
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
        await Assert.That(frontier.AppliedSequence).IsEqualTo(0UL);
        var digest = frontier.JournalDigest.Memory;
        for (long sequence = 1; sequence <= head; sequence++)
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
            await transaction.SetUniqueConstraintsAsync("people", [new UniqueConstraint("email", ["email"], "case_fold")], token);
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

    private static Uri Document(ReplicationTenant tenant, string id, string database = "db") => new(tenant.Path(database) + "/collections/people/documents/" + id, UriKind.Relative);
}
