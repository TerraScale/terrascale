using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.E2E.WalFaults;

/// <summary>
/// A receiver's acknowledgement on a peer stream is a remote durable commit: every transaction that joined the batch's change
/// groups committed and synced its write-ahead log (<c>synchronous = FULL</c>) before the ack went out, also when the batch
/// is split into several transactions. So the ack waits for every one of those syncs, and every group a node acknowledged is
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

        // Three groups of another region's machine in one batch, of 1,000, 100 and 1 documents: more than one transaction's
        // 1,024, so the receiver joins them in two transactions (groups never split). Every log sync is held 300 ms, so
        // the ack can only come after both transactions' syncs.
        var origin = ReplicaId.Of(1, 7, 0);
        var stamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16;
        int[] sizes = [1000, 100, 1];
        var ids = sizes.Select(size => Enumerable.Range(0, size).Select(_ => EntityIds.New().ToString()).ToArray()).ToArray();
        var delay = TimeSpan.FromMilliseconds(300);
        using (var channel = PeerChannel.Create(node.Address))
        {
            using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
            await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc", Machine = "zzzz-sender" } });
            await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None) && call.ResponseStream.Current.Welcome is not null).IsTrue();

            // A first batch makes the database's file here, whose own syncs are not the timed batch's; acknowledged first.
            await call.RequestStream.WriteAsync(new PushFrame
            {
                Batch = new Batch { Database = ChangeGroups.Wire(database), LastChangeId = 1, Groups = ChangeGroups.Encode([new OriginGroup(1, [Put(origin, stamp, EntityIds.New().ToString(), -1)])]) },
            });
            using (var warm = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout))
            {
                await Assert.That(await call.ResponseStream.MoveNext(warm.Token) && call.ResponseStream.Current.Ack is { ChangeId: 1, Refused: false }).IsTrue();
            }

            node.DelaySyncs(delay);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await call.RequestStream.WriteAsync(new PushFrame
            {
                Batch = new Batch
                {
                    Database = ChangeGroups.Wire(database),
                    LastChangeId = (ulong)ids.Length + 1,
                    Groups = ChangeGroups.Encode([.. ids.Select((group, index) => new OriginGroup(
                        index + 2, [.. group.Select((id, n) => Put(origin, stamp + 1 + (ulong)(index * 2048 + n), id, index))]))]),
                },
            });
            using var timeout = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout);
            await Assert.That(await call.ResponseStream.MoveNext(timeout.Token)).IsTrue();
            var acked = clock.Elapsed;
            node.ClearFaults();
            await Assert.That(call.ResponseStream.Current.Ack is { ChangeId: 4, Refused: false }).IsTrue().Because(call.ResponseStream.Current.ToString());
            await Assert.That(acked).IsGreaterThanOrEqualTo(delay * 2).Because("the ack waits for both transactions' log syncs");
        }

        await node.PowerLossAsync();
        await node.StartAgainAsync();

        // The first and last document of each group: both transactions survived.
        foreach (var (index, id) in ids.Index().SelectMany(group => new[] { (group.Index, group.Item[0]), (group.Index, group.Item[^1]) }))
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
