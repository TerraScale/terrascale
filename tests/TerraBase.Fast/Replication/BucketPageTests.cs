using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class BucketPageTests
{
    [Test]
    [Arguments(false, 1)]
    [Arguments(false, 1048576)]
    [Arguments(true, 1)]
    [Arguments(true, 1048576)]
    public async Task Bucket_pages_preserve_selected_keys_order_limits_and_tombstones(bool sparse, int maxBytes)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider());
        var managed = await api.CreateManagedDatabaseAsync();
        var database = new TenantContext(managed.Organization, PlanTier.Hobby, managed.Project, managed.Id);
        var origin = ReplicaId.Of(99, 1, 0);
        using var body = JsonDocument.Parse("""{"value":"bucket page"}""");
        var live = DocumentState.Bottom.Put(new(new Hlc(1000), origin), body.RootElement);
        var deleted = live.Delete(new(new Hlc(2000), origin));
        var ids = Enumerable.Range(0, 4097).Select(index => ManagementApi.DocumentId("bucket-page-" + index)).ToArray();
        var collision = ids.GroupBy(id => SqliteStorageTransaction.Bucket("items", id)).First(group => group.Count() > 1).Take(2).ToArray();
        var seeded = collision.Concat(ids.Except(collision, StringComparer.Ordinal).Take(6))
            .Select((id, index) => new RepairState("items", id, index == 0 ? deleted : live)).ToArray();
        await Assert.That(await api.Server.Services.GetRequiredService<IChangeGroupSink>().RepairAsync(database, seeded, token)).IsEqualTo(seeded.Length);
        int[] selected = sparse
            ? [SqliteStorageTransaction.Bucket("items", collision[0]), SqliteStorageTransaction.Bucket("items", seeded[^1].Id)]
            : [.. Enumerable.Range(0, 4096)];
        var expected = seeded.Where(state => selected.Contains(SqliteStorageTransaction.Bucket(state.Collection, state.Id)))
            .OrderBy(state => SqliteStorageTransaction.Bucket(state.Collection, state.Id)).ThenBy(state => state.Collection, StringComparer.Ordinal)
            .ThenBy(state => state.Id, StringComparer.Ordinal).ToArray();
        var states = api.Server.Services.GetRequiredService<IReplicaStates>();
        var incarnation = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token))!.Incarnation;
        List<RepairState> actual = [];
        BucketCursor? cursor = null;
        do
        {
            var (page, next) = await states.ReadBucketsAsync(database, incarnation, [.. selected.Reverse(), .. selected], cursor, 2, maxBytes, token)
                ?? throw new InvalidOperationException("the database incarnation is not held");
            await Assert.That(page.Count).IsBetween(1, maxBytes == 1 ? 1 : 2);
            actual.AddRange(page);
            await Assert.That(actual.Count).IsLessThanOrEqualTo(expected.Length);
            if (next is not null)
            {
                var last = page[^1];
                await Assert.That(next).IsEqualTo(new BucketCursor(SqliteStorageTransaction.Bucket(last.Collection, last.Id), last.Collection, last.Id));
                await Assert.That(next).IsNotEqualTo(cursor);
            }
            cursor = next;
        }
        while (cursor is not null);

        await Assert.That(actual).IsEquivalentTo(expected);
        await Assert.That(actual.Select(state => state.Id).SequenceEqual(expected.Select(state => state.Id))).IsTrue();
        await Assert.That(actual.Single(state => state.Id == collision[0]).State.Exists).IsFalse();
        var (empty, end) = await states.ReadBucketsAsync(database, incarnation, [], null, 2, maxBytes, token)
            ?? throw new InvalidOperationException("the database incarnation is not held");
        await Assert.That(empty).IsEmpty();
        await Assert.That(end).IsNull();
        var (pastEnd, pastCursor) = await states.ReadBucketsAsync(database, incarnation, selected, new BucketCursor(4096, "", ""), 2, maxBytes, token)
            ?? throw new InvalidOperationException("the database incarnation is not held");
        await Assert.That(pastEnd).IsEmpty();
        await Assert.That(pastCursor).IsNull();
    }
}
