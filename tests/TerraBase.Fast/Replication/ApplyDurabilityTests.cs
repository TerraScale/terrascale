using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>The per-transaction FULL contract, separate from a final sync covering earlier acknowledged groups.</summary>
public sealed class ApplyDurabilityTests
{
    private static readonly int[] GroupSizes = [1000, 100, 1];

    [Test]
    public async Task Every_receiver_transaction_commits_with_FULL_before_the_next_chunk()
    {
        var modes = new ConcurrentQueue<int>();
        TursoDatabaseStore? store = null;
        string? target = null;
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                if (step == "replication.apply.committed" && database.DatabaseId == Volatile.Read(ref target))
                {
                    var session = store!.CurrentWriter() ?? throw new InvalidOperationException("the apply still holds its writer");
                    using var command = session.Connection.CreateCommand();
                    command.CommandText = "PRAGMA synchronous;";
                    modes.Enqueue(Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture));
                }
            });
            // The receiver's progress timeout has its own tests; a frozen clock keeps this 1,101-document apply off it under load.
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreamReceiver>(provider, new FakeTimeProvider(DateTimeOffset.UtcNow)));
        });
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var tenant = await api.Server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the database is not registered");
        store = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
        Volatile.Write(ref target, tenant.DatabaseId);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var channel = PeerChannel.Create(api.Server.BaseAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        using var call = client.Push(cancellationToken: deadline.Token);
        var origin = ReplicaId.Of(1, 7, 0);
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } });
        await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();

        // 1,000 + 100 + 1 documents: whole groups force two apply transactions.
        var stamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16;
        OriginGroup[] groups = [.. GroupSizes.Select((count, index) => new OriginGroup(index + 1,
            [.. Enumerable.Range(0, count).Select(_ => Put(origin, stamp + (ulong)index, EntityIds.New().ToString()))]))];
        // The made-up origin's copy of the database, which the ack echoes.
        var sender = ChangeGroups.Wire(tenant, Ulid.NewUlid());
        await call.RequestStream.WriteAsync(new PushFrame
        {
            Batch = new Batch { Database = sender, LastChangeId = 3, Groups = ChangeGroups.Encode(groups) },
        });
        await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
        var ack = call.ResponseStream.Current.Ack;
        await Assert.That(ack.Refused).IsFalse();
        await Assert.That(ack.ChangeId).IsEqualTo(3UL);
        await Assert.That(ack.Database).IsEqualTo(sender);
        await Assert.That(string.Join(", ", modes)).IsEqualTo("2, 2").Because("every receiver transaction commits with synchronous FULL");

        foreach (var group in groups)
        {
            foreach (var id in new[] { group.Deltas[0].Id, group.Deltas[^1].Id }.Distinct())
            {
                using var response = await data.GetAsync(ManagementApi.Uri($"{managed.Data}/collections/items/documents/by-key?id={id}"), deadline.Token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(JsonElement.Parse(await response.Content.ReadAsStringAsync(deadline.Token)).GetProperty("document").GetRawText())
                    .IsEqualTo("""{"n":1}""");
            }
        }
    }

    private static ChangeDelta Put(ReplicaId origin, ulong stamp, string id)
    {
        var at = new EntryVersion(new Hlc(stamp), origin);
        return new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, JsonElement.Parse("""{"n":1}""")).DeltaAt(at.Stamp));
    }
}
