using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;

namespace TerraBase.Fast.Documents;

public sealed class ReplicationCapacityTests
{
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
        // Group 1 created the collection.
        await AssertStateAsync(api, 1025);
        (await api.PutAsync(DocumentApi.NewId(), new { n = 3 })).Status.ShouldBe(HttpStatusCode.Created);
        await AssertStateAsync(api, 1026);
    }

    private static async Task AssertStateAsync(DocumentApi api, long count)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var database = await api.TenantOfAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        await using var read = await store.BeginTransactionAsync(write: false, deadline.Token);
        var counted = await read.CountAsync("items", null, 2000, deadline.Token);
        await Assert.That(counted).IsEqualTo((count, true));
    }
}
