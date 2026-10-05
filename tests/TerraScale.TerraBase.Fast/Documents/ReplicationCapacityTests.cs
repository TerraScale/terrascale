using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;

namespace TerraScale.TerraBase.Fast.Documents;

public sealed class ReplicationCapacityTests
{
    [Test]
    public async Task Change_content_admission_reports_the_failing_operation_and_rolls_back()
    {
        await using var api = await DocumentApi.StartAsync();
        var ids = Enumerable.Range(0, 3).Select(_ => DocumentApi.NewId()).ToArray();
        var document = new { text = new Faker().Random.String2(800 * 1024, "abcdef") };
        var operations = ids.Select(id => (object)new { op = "put", collection = "items", id, document }).ToArray();
        var result = await api.TransactAsync(operations);

        await Assert.That((result.Status, result.Code, result.Op)).IsEqualTo((HttpStatusCode.ServiceUnavailable, "capacity", (int?)2));
        foreach (var id in ids)
        {
            await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);
        }

        await AssertStateAsync(api, 0, 0);
        (await api.PutAsync(ids[0], new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        await AssertStateAsync(api, 1, 1);
    }

    [Test]
    public async Task A_collection_drop_exceeding_capture_count_fails_atomically_with_capacity()
    {
        await using var api = await DocumentApi.StartAsync();
        var ids = Enumerable.Range(0, 1025).Select(_ => DocumentApi.NewId()).ToArray();
        var operations = ids.Take(1024).Select(id => (object)new { op = "put", collection = "items", id, document = new { n = 1 } }).ToArray();
        (await api.TransactAsync(operations)).Status.ShouldBe(HttpStatusCode.OK);
        (await api.PutAsync(ids[^1], new { n = 2 })).Status.ShouldBe(HttpStatusCode.Created);
        var dropped = await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/items");

        await Assert.That((dropped.Status, dropped.Code, dropped.Op)).IsEqualTo((HttpStatusCode.ServiceUnavailable, "capacity", (int?)null));
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/items")).Status).IsEqualTo(HttpStatusCode.OK);
        await AssertStateAsync(api, 1025, 2);
        (await api.PutAsync(DocumentApi.NewId(), new { n = 3 })).Status.ShouldBe(HttpStatusCode.Created);
        await AssertStateAsync(api, 1026, 3);
    }

    private static async Task AssertStateAsync(DocumentApi api, long count, long sequence)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var database = await api.TenantOfAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
        var counted = await read.CountAsync("items", null, 2000, deadline.Token);
        await Assert.That(counted).IsEqualTo((count, true));
        await Assert.That(await read.GetLastChangeSequenceAsync(deadline.Token)).IsEqualTo(sequence);
        if (sequence > 0)
        {
            var group = (await read.ReadChangesAsync(sequence, deadline.Token))!;
            await Assert.That(group.Select(change => change.Version).Distinct().Single()).IsEqualTo(await read.GetOffsetAsync(deadline.Token));
        }
    }
}
