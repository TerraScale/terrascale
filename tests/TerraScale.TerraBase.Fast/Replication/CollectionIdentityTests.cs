using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class CollectionIdentityTests
{
    [Test]
    public async Task An_implicit_collection_replicates_its_generated_identity_with_its_document()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var peers = new PeerTls();
        using var clients = new ClientTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            clients.Apply(settings);
        });
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            clients.Apply(settings);
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        });
        var tenant = await ReplicationTenant.CreateAsync(sender, "shop");
        await tenant.BindAsync(receiver);
        using var source = clients.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:read"));
        using var target = clients.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
        var engine = sender.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(tenant.Database("shop"), token))!;
        var document = EntityIds.New().ToString();
        await using (var write = await store.BeginTransactionAsync(write: true, token))
        {
            await write.PutAsync("implicit", document, "{\"value\":1}"u8.ToArray(), token);
            await write.CommitAsync(token);
        }

        await TerraBaseTestServer.ReplicatedAsync(sender);
        var path = tenant.Path("shop") + "/collections/implicit";
        using var local = await source.GetAsync(new Uri(path, UriKind.Relative), token);
        using var remote = await target.GetAsync(new Uri(path, UriKind.Relative), token);
        await Assert.That((local.StatusCode, remote.StatusCode)).IsEqualTo((HttpStatusCode.OK, HttpStatusCode.OK));
        var localCollection = await local.Content.ReadFromJsonAsync<JsonElement>(token);
        var remoteCollection = await remote.Content.ReadFromJsonAsync<JsonElement>(token);
        var identity = localCollection.GetProperty("id").GetString()!;
        await Assert.That(EntityIds.IsValid(identity)).IsTrue();
        await Assert.That(remoteCollection.GetProperty("id").GetString()).IsEqualTo(identity);
        using var read = await target.GetAsync(new Uri(path + "/documents/by-key?id=" + document, UriKind.Relative), token);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("document").GetProperty("value").GetInt32()).IsEqualTo(1);
        await using var metadata = await store.BeginTransactionAsync(write: false, token);
        var record = (await metadata.GetAsync(DocumentChange.Collections, "implicit", token))!;
        using var body = JsonDocument.Parse(record.Json);
        await Assert.That(body.RootElement.GetProperty("entity_id").GetString()).IsEqualTo(identity);
    }
}
