using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

/// <summary>Real SSE listeners behind a test proxy following Fly's documented instance replay and fallback headers.</summary>
public sealed class DatabaseChangesReplayTests
{
    [Test]
    [Arguments("gru", "sjc", true, false)]
    [Arguments("gru", "fra", true, false)]
    [Arguments("sjc", "gru", true, false)]
    [Arguments("sjc", "fra", true, false)]
    [Arguments("fra", "gru", true, false)]
    [Arguments("fra", "sjc", true, false)]
    [Arguments("fra", "gru", true, true)]
    [Arguments("fra", "gru", false, false)]
    [Arguments("fra", "gru", false, true)]
    public async Task A_resume_replays_to_the_issuer_or_checks_the_fallback_prefix(string from, string to, bool issuerAvailable, bool differentPrefix)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        // Bootstrap fresh catalogs independently; this fixture copies tenant identity and controls each feed explicitly.
        await using var source = await StartAsync(from);
        await using var destination = await StartAsync(to);
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
        await ApplyAsync(destination.Server, differentPrefix ? DocumentApi.NewId() : first, firstStamp, 1);
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

        // The URL, scoped credential and Last-Event-ID stay the same across every proxy hop.
        using var landing = Request();
        using var replay = await destinationClient.SendAsync(landing, HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(replay.Headers.GetValues("fly-replay")).IsEquivalentTo([$"instance={from}-machine;timeout=2s;fallback=force_self"]);
        await Assert.That(replay.Headers.CacheControl!.NoStore).IsTrue();
        using var routed = Request();
        routed.Headers.Add(issuerAvailable ? "Fly-Replay-Src" : "fly-replay-failed", issuerAvailable
            ? $"instance={to}-machine;region={to};t=1"
            : $"instance={from}-machine;replay_source={to}-machine;reason=no_candidate;elapsed_ms=0");
        using var response = await (issuerAvailable ? sourceClient : destinationClient).SendAsync(routed, HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.Headers.Contains("fly-replay")).IsFalse();
        await Assert.That(response.Headers.GetValues("X-TerraBase-Machine")).IsEquivalentTo([$"{(issuerAvailable ? from : to)}-machine"]);
        if (!issuerAvailable && differentPrefix)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Gone);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("cursor_expired");
        }
        else
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
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

    private static Task<ManagementApi> StartAsync(string region) => ManagementApi.StartAsync(settings =>
    {
        settings["region"] = region;
        settings["node-name"] = region + "-machine";
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
