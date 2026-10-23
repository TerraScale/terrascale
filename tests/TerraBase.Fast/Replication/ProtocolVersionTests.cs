using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The peer stream's protocol version (docs/terrabase/replication/23-protocol-version-and-pipelining.md): a receiver
/// welcomes a sender of a build before the version with its own version, and ignores a frame of a kind it does not know,
/// so the stream goes on and a newer sender's next batch is acknowledged.
/// </summary>
public sealed class ProtocolVersionTests
{
    [Test]
    public async Task A_frame_of_an_unknown_kind_is_ignored_and_the_next_batch_on_the_stream_is_acknowledged()
    {
        await using var api = await ManagementApi.StartAsync();
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var tenant = await api.Server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the database is not registered");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var channel = PeerChannel.Create(api.Server.BaseAddress, PeerTls.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push(cancellationToken: deadline.Token);
        var origin = ReplicaId.Of(1, 9, 0);
        // A hello without a protocol: a build from before the field.
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } });
        await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome.Protocol).IsEqualTo(ReplicationProtocol.Version);

        // Field 6 of the kind oneof, which this build does not define: a length-delimited empty message.
        await call.RequestStream.WriteAsync(PushFrame.Parser.ParseFrom([(6 << 3) | 2, 0]));
        var sender = ChangeGroups.Wire(tenant, Ulid.NewUlid());
        var at = new EntryVersion(new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16), origin);
        var id = EntityIds.New().ToString();
        OriginGroup[] groups = [new(1, [new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, JsonElement.Parse("""{"n":1}""")).DeltaAt(at.Stamp))])];
        await call.RequestStream.WriteAsync(new PushFrame { Batch = new Batch { Database = sender, LastChangeId = 1, Groups = ChangeGroups.Encode(groups) } });

        await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
        var ack = call.ResponseStream.Current.Ack;
        await Assert.That(ack.Refused).IsFalse();
        await Assert.That(ack.ChangeId).IsEqualTo(1UL);
        await Assert.That(ack.Database).IsEqualTo(sender);
    }

    [Test]
    [Arguments(0u, 1u)]
    [Arguments(1u, 1u)]
    [Arguments(2u, 2u)]
    [Arguments(99u, ReplicationProtocol.Version)]
    public async Task A_stream_uses_the_lower_version_and_a_peer_that_names_none_speaks_1(uint announced, uint used) =>
        await Assert.That(ReplicationProtocol.Negotiate(announced)).IsEqualTo(used);
}
