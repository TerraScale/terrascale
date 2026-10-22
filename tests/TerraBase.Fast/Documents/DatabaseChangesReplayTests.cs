using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

/// <summary>Real SSE listeners resume directly on a destination with the same scanned prefix.</summary>
public sealed class DatabaseChangesReplayTests
{
    [Test]
    [Arguments("gru", "sjc", true, false, false)]
    [Arguments("gru", "fra", true, false, false)]
    [Arguments("sjc", "gru", true, false, false)]
    [Arguments("sjc", "fra", true, false, false)]
    [Arguments("fra", "gru", true, false, false)]
    [Arguments("fra", "sjc", true, false, false)]
    [Arguments("fra", "gru", true, true, false)]
    [Arguments("fra", "gru", false, false, false)]
    [Arguments("fra", "gru", false, true, false)]
    [Arguments("fra", "gru", true, false, true)]
    [Arguments("fra", "gru", false, false, true)]
    public async Task A_resume_checks_the_destination_prefix_regardless_of_issuer_availability(string from, string to, bool issuerAvailable, bool differentPrefix, bool missingPrefix)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        // Bootstrap fresh catalogs independently; this fixture copies tenant identity and controls each feed explicitly.
        var sourceMachine = EntityIds.New().ToString().ToLowerInvariant();
        var destinationMachine = EntityIds.New().ToString().ToLowerInvariant();
        await using var source = await StartAsync(from, sourceMachine);
        await using var destination = await StartAsync(to, destinationMachine);
        await Assert.That(source.Server.Services.GetRequiredService<ICatalogGate>().IsOpen).IsTrue();
        await Assert.That(destination.Server.Services.GetRequiredService<ICatalogGate>().IsOpen).IsTrue();
        var tenant = await ReplicationTenant.CreateAsync(source.Server, [DocumentApi.Database], token, replicaRegions: [to]);
        await tenant.BindAsync(destination.Server);
        var secret = await tenant.KeyAsync(source.Server, "documents:read", "documents:write");
        await CopyKeyAsync(source.Server, destination.Server, secret, token);
        using var sourceClient = source.DataClient(secret);
        using var destinationClient = destination.DataClient(secret);
        var origin = ReplicaId.Of(4518, 23, 1);
        var first = DocumentApi.NewId();
        var second = DocumentApi.NewId();
        var now = DateTimeOffset.UtcNow;
        var firstStamp = FeedCursor.At(now - TimeSpan.FromMinutes(2));
        var secondStamp = FeedCursor.At(now - TimeSpan.FromMinutes(1));
        await ApplyAsync(source.Server, first, firstStamp, 1);
        if (!missingPrefix)
        {
            await ApplyAsync(destination.Server, differentPrefix ? DocumentApi.NewId() : first, firstStamp, 1);
        }
        var path = tenant.Path(DocumentApi.Database) + "/changes?cursor=now";
        string cursor;
        using (var initial = await sourceClient.GetAsync(new Uri(path, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, token))
        {
            await Assert.That(initial.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var reader = new StreamReader(await initial.Content.ReadAsStreamAsync(token));
            cursor = (await NextAsync(reader, token)).Id;
        }

        await ApplyAsync(source.Server, second, secondStamp, 2);
        await ApplyAsync(destination.Server, second, secondStamp, 2);
        if (!issuerAvailable)
        {
            await source.Server.DisposeAsync();
        }

        // Resume directly with the same URL, scoped credential and Last-Event-ID.
        using var request = Request();
        using var response = await destinationClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.Headers.GetValues("X-TerraBase-Machine")).IsEquivalentTo([destinationMachine]);
        if (differentPrefix || missingPrefix)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Gone);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("cursor_expired");
        }
        else
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
            _ = await NextAsync(reader, token);
            var (_, data) = await NextAsync(reader, token);
            await Assert.That(data.GetProperty("id").GetString()).IsEqualTo(second);
        }

        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.Add("Last-Event-ID", cursor);
            return request;
        }

        async Task ApplyAsync(TerraBaseTestServer server, string id, Hlc stamp, long sequence)
        {
            using var json = JsonDocument.Parse("""{"value":1}""");
            var state = DocumentState.Bottom.Put(new EntryVersion(stamp, origin), json.RootElement);
            _ = await server.Services.GetRequiredService<IChangeGroupSink>().ApplyAsync(tenant.Database(DocumentApi.Database), origin,
                [new OriginGroup(sequence, [new ChangeDelta(DocumentApi.Collection, id, stamp, state.DeltaAt(stamp))])], token);
        }
    }

    private static Task<ManagementApi> StartAsync(string region, string machine) => ManagementApi.StartAsync(settings =>
    {
        settings["region"] = region;
        settings["node-name"] = machine;
    });

    private static async Task CopyKeyAsync(TerraBaseTestServer source, TerraBaseTestServer destination, string secret, CancellationToken token)
    {
        var key = await source.Services.GetRequiredService<IApiKeyStore>().FindAsync(secret, token) switch
        {
            ApiKey value => value,
            _ => throw new InvalidOperationException("the source key was not stored"),
        };
        var sourceStore = await source.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var destinationStore = await destination.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await sourceStore.BeginTransactionAsync(write: false, token);
        await using var write = await destinationStore.BeginTransactionAsync(write: true, token);
        foreach (var (collection, id) in new[] { ("api_keys", key.Id), ("api_key_hashes", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))) })
        {
            var document = (await read.GetAsync(collection, id, token))!;
            await write.CreateCollectionAsync(new CollectionInfo(collection, 0), token);
            _ = await write.PutAsync(collection, id, document.Json, token);
        }

        _ = await write.CommitAsync(token);
    }

    private static async Task<(string Id, JsonElement Data)> NextAsync(StreamReader reader, CancellationToken token)
    {
        var id = string.Empty;
        var data = string.Empty;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line.Length == 0 && data.Length > 0)
            {
                using var json = JsonDocument.Parse(data);
                return (id, json.RootElement.Clone());
            }

            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = line[4..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line[6..];
            }
        }

        throw new InvalidOperationException("the SSE stream ended before the next event");
    }
}
