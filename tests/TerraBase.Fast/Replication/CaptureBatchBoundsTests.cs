using System.Text;
using Bogus;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using ReplicaCluster = TerraBase.Fast.Replication.PeerStreamTests.Cluster;

namespace TerraBase.Fast.Replication;

/// <summary>Captured batches must fit the real receiver's envelope bounds, including document keys.</summary>
public sealed class CaptureBatchBoundsTests
{
    [Test]
    public async Task Long_document_ids_do_not_make_captured_batches_unreadable_by_the_receiver()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        await using var cluster = await ReplicaCluster.StartAsync(cdc: true, "gru");
        var machine = cluster["gru"];
        var store = await machine.StoreAsync();
        await using (var transaction = await store.BeginTransactionAsync(write: true, token))
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), token);
            _ = await transaction.CommitAsync(token);
        }

        var incarnation = await machine.IncarnationAsync();
        var after = await machine.HeadAsync(cluster.Database);
        var prefix = new Faker().Random.AlphaNumeric(998);
        var body = Encoding.UTF8.GetBytes("""{"n":1}""");
        await using (var rolledBack = await store.BeginTransactionAsync(write: true, token))
        {
            _ = await rolledBack.PutAsync("items", prefix + EntityIds.New(), body, token);
        }

        await using (var overCapacity = await store.BeginTransactionAsync(write: true, token))
        {
            var largeBody = Encoding.UTF8.GetBytes($$"""{"pad":"{{new string('x', 900 * 1024)}}"}""");
            for (var document = 0; document < 3; document++)
            {
                _ = await overCapacity.PutAsync("items", prefix + EntityIds.New(), largeBody, token);
            }

            await Assert.ThrowsAsync<ChangeGroupCapacityException>(() => overCapacity.CommitAsync(token).AsTask());
        }

        await Assert.That(await machine.HeadAsync(cluster.Database)).IsEqualTo(after)
            .Because("neither a disposed transaction nor a failed capacity commit publishes a group");
        const int Commits = 10;
        const int DocumentsPerCommit = 512;
        HashSet<string> expected = [];
        for (var commit = 0; commit < Commits; commit++)
        {
            await using var transaction = await store.BeginTransactionAsync(write: true, token);
            for (var document = 0; document < DocumentsPerCommit; document++)
            {
                var id = prefix + EntityIds.New();
                _ = expected.Add(id);
                _ = await transaction.PutAsync("items", id, body, token);
            }

            _ = await transaction.CommitAsync(token);
        }

        // Keys alone exceed 4 MiB; the tiny states fit the old state-only 2 MiB batch accounting.
        HashSet<string> received = [];
        var groupsRead = 0;
        for (var page = 0; page <= Commits; page++)
        {
            var source = await machine.Groups.ReadAsync(cluster.Database, incarnation, after,
                ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, token);
            await Assert.That(source).IsNotNull();
            if (source!.Groups.Count == 0)
            {
                break;
            }

            var last = source.Groups[^1].ChangeId;
            var envelope = ChangeGroups.Encode(source.Groups);
            var plain = ApplicationEnvelope.Decode(envelope.Span, ReplicationProtocol.MaxApplicationBytes);
            await Assert.That(source.Groups.Sum(group => ChangeGroups.EncodedSize(group.ChangeId, group.Deltas)))
                .IsEqualTo(plain.Length).Because("batch accounting must include the exact protobuf keys and framing");
            await Assert.That(plain.Length <= ChangeGroups.MaxBatchBytes || source.Groups.Count == 1).IsTrue()
                .Because("only one indivisible group may exceed the normal batch budget");
            var decoded = ChangeGroups.Decode(cluster.Database, envelope, checked((ulong)last));
            foreach (var group in decoded)
            {
                await Assert.That(group.ChangeId).IsGreaterThan(after);
                after = group.ChangeId;
                groupsRead++;
                foreach (var delta in group.Deltas)
                {
                    await Assert.That(received.Add(delta.Id)).IsTrue().Because("every committed document travels exactly once");
                }
            }
        }

        await Assert.That(groupsRead).IsEqualTo(Commits);
        await Assert.That(received.SetEquals(expected)).IsTrue();
        await Assert.That(after).IsEqualTo(await machine.HeadAsync(cluster.Database));
    }
}
