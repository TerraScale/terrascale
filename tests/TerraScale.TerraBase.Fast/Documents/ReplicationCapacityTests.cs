using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Documents;

public sealed class ReplicationCapacityTests
{
    [Test]
    public async Task Change_content_admission_reports_the_failing_operation_and_rolls_back()
    {
        await using var api = await DocumentApi.StartAsync();
        var ids = Enumerable.Range(0, 3).Select(_ => DocumentApi.NewId()).ToArray();
        foreach (var id in ids)
        {
            (await api.PutAsync(id, new { n = 0 })).Status.ShouldBe(HttpStatusCode.Created);
        }

        // Each body grows to about 800 KiB in the file, not over HTTP: a multi-megabyte upload can stall for 20 s on
        // loopback. A small patch captures its whole document, so the third patch passes the 2 MiB of change content.
        var tenant = await api.TenantOfAsync();
        var database = new ManagedDatabaseRef(tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId!, DocumentApi.Database);
        var body = $$"""{"n":0,"text":"{{new Faker().Random.String2(800 * 1024, "abcdef")}}"}""";
        foreach (var id in ids)
        {
            var stored = await ManagementApi.ExecuteStoredAsync(
                api.Server.DataDirectory, database, "UPDATE documents SET body = $body WHERE collection = $collection AND id = $id;",
                ("$body", body), ("$collection", DocumentApi.Collection), ("$id", id));
            await Assert.That(stored).IsEqualTo(1);
        }

        // The version covers the body, so read it after the body changed.
        var versions = new List<string>();
        foreach (var id in ids)
        {
            versions.Add((await api.GetAsync(id)).Version);
        }

        var operations = ids.Select(id => (object)new { op = "patch", collection = "items", id, merge = new { n = 1 } }).ToArray();
        var result = await api.TransactAsync(operations);

        await Assert.That((result.Status, result.Code, result.Op)).IsEqualTo((HttpStatusCode.ServiceUnavailable, "capacity", (int?)2));
        for (var i = 0; i < ids.Length; i++)
        {
            var current = await api.GetAsync(ids[i]);
            await Assert.That((current.Status, current.Version)).IsEqualTo((HttpStatusCode.OK, versions[i]));
        }

        await AssertStateAsync(api, 3, 3);
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        await AssertStateAsync(api, 4, 4);
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
