using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.E2E.WalFaults;

/// <summary>
/// A receiver's acknowledgement on a peer stream is a remote durable commit: the change groups' transaction committed and
/// its write-ahead log synced (<c>synchronous = FULL</c>) before the ack went out. So every group a node acknowledged is
/// still there after a power loss of that node drops every unsynced byte of every log.
/// </summary>
public sealed class ReplicatedApplyPowerLossTests
{
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_power_loss_keeps_every_change_group_the_receiver_acknowledged()
    {
        Skip.When(Cluster.FlyRequested, "kills a local node process and edits its files");
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var node = await PowerLossNode.StartAsync();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await node.CommitAsync(EntityIds.New().ToString(), new JsonObject
        {
            ["id"] = EntityIds.New().ToString(),
            ["ops"] = new JsonArray(
                new JsonObject { ["op"] = "create_organization", ["id"] = database.OrganizationId, ["name"] = $"power-{database.OrganizationId}" },
                new JsonObject { ["op"] = "create_project", ["organization"] = database.OrganizationId, ["id"] = database.ProjectId, ["name"] = "power" },
                new JsonObject { ["op"] = "create_database", ["organization"] = database.OrganizationId, ["project"] = database.ProjectId, ["name"] = "canary", ["id"] = database.DatabaseId }),
        });

        // Three groups of another region's machine in one batch, acknowledged up to the last.
        var origin = ReplicaId.Of(1, 7, 0);
        var stamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16;
        string[] ids = [EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString()];
        using (var channel = PeerChannel.Create(node.Address))
        {
            using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
            await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc", Machine = "zzzz-sender" } });
            await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None) && call.ResponseStream.Current.Welcome is not null).IsTrue();
            await call.RequestStream.WriteAsync(new PushFrame
            {
                Batch = new Batch
                {
                    Database = ChangeGroups.Wire(database),
                    LastChangeId = (ulong)ids.Length,
                    Groups = ChangeGroups.Encode([.. ids.Select((id, index) => new OriginGroup(index + 1, [Put(origin, stamp + (ulong)index, id, index)]))]),
                },
            });
            using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
            await Assert.That(await call.ResponseStream.MoveNext(timeout.Token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Ack is { ChangeId: 3, Refused: false }).IsTrue().Because(call.ResponseStream.Current.ToString());
        }

        await node.PowerLossAsync();
        await node.StartAgainAsync();

        foreach (var (index, id) in ids.Index())
        {
            var read = new Uri($"/v1/projects/{database.ProjectId}/databases/canary/collections/items/documents/by-key?id={id}", UriKind.Relative);
            var document = await Docs.ReadAsync(node.Client, read);
            await Assert.That(document?["n"]?.GetValue<int>()).IsEqualTo(index).Because($"group {index + 1} was acknowledged before the power loss");
        }
    }

    private static ChangeDelta Put(ReplicaId origin, ulong stamp, string id, int n)
    {
        var at = new EntryVersion(new Hlc(stamp), origin);
        using var body = JsonDocument.Parse($$"""{"n":{{n}}}""");
        return new ChangeDelta("items", id, at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }
}
