using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class StoredJsonAdmissionTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Storage_rejects_replicated_duplicates_before_PATCH_and_transactions_use_them(bool nested, bool ops)
    {
        using var tls = new PeerTls();
        await using var api = await DocumentApi.StartAsync(tls.Apply);
        var tenant = await api.TenantOfAsync();
        var id = DocumentApi.NewId();
        var json = nested ? """{"a":{"b":1,"b":2}}""" : """{"a":1,"a":2}""";
        var original = await api.PutAsync(id, nested ? new { a = (object)new { b = 1 } } : new { a = (object)1 });
        original.Status.ShouldBe(HttpStatusCode.Created);
        var origin = EntityIds.New().ToByteArray();
        using var channel = tls.CreateChannel(api.Server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(tenant, origin) });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
        await call.RequestStream.WriteAsync(new SyncRequest
        {
            CommittedGroup = ReplicationProtocol.CreateBatch(tenant, origin, 1, Deltas.Group(Deltas.Write("items", id, json))),
        });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorInvalidBatch);
        await call.RequestStream.CompleteAsync();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
        }

        var body = ops ? """{"ops":[{"op":"set","path":"PATH","value":3}]}""".Replace("PATH", nested ? "/a/b" : "/a", StringComparison.Ordinal)
            : nested ? """{"a":{"b":3}}""" : """{"a":3}""";
        var patch = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), body, ops ? DocumentPatch.OpsMediaType : DocumentPatch.MergeMediaType);
        await Assert.That(patch.Status).IsEqualTo(HttpStatusCode.OK);
        var fresh = DocumentApi.NewId();
        using var patchDocument = JsonDocument.Parse(body);
        var step = new Dictionary<string, object>
        {
            ["op"] = "patch",
            ["collection"] = "items",
            ["id"] = id,
            [ops ? "ops" : "merge"] = ops ? patchDocument.RootElement.GetProperty("ops") : patchDocument.RootElement,
        };
        var transaction = await api.TransactAsync(new { op = "put", collection = "items", id = fresh, document = new { ok = true } }, step);
        await Assert.That(transaction.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.GetAsync(fresh)).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.GetAsync(id)).Json.GetProperty("document").GetRawText()).IsEqualTo(nested ? "{\"a\":{\"b\":3}}" : "{\"a\":3}");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Storage_rejects_malformed_JSON_without_advancing_shapes_or_allowing_commit(bool imported)
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        await using (var write = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            var malformed = Encoding.UTF8.GetBytes("{\"a\":");
            var id = DocumentApi.NewId();
            await Assert.That(async () =>
            {
                if (imported)
                {
                    await write.ApplyAsync(Deltas.Write("items", id, Encoding.UTF8.GetString(malformed)), CancellationToken.None);
                }
                else
                {
                    await write.PutAsync("items", id, malformed, CancellationToken.None);
                }
            }).Throws<DocumentValidationException>();
            await Assert.That(async () => await write.CommitAsync(CancellationToken.None)).Throws<InvalidOperationException>();
        }

        using var start = await shapes.GetAsync(new Uri(api.Shape + "?offset=-1", UriKind.Relative));
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var snapshot = await DocumentApi.SendAsync(shapes, HttpMethod.Get, start.Headers.Location!.OriginalString);
        var handle = start.Headers.GetValues("TB-Shape-Handle").Single();
        var log = await DocumentApi.SendAsync(shapes, HttpMethod.Get, $"{api.Shape}?handle={handle}&offset=0");
        await Assert.That(snapshot.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(log.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(snapshot.Json.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray())).IsEmpty();
        await Assert.That(log.Json.GetProperty("changes").GetArrayLength()).IsEqualTo(0);
        var next = DocumentApi.NewId();
        (await api.PutAsync(next, new { ok = true })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.GetAsync(next)).Status.ShouldBe(HttpStatusCode.OK);
    }
}
