using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

public sealed class ShapeCacheE2ETests
{
    [Test]
    [Category("local-only")]
    [Scenario(Nodes.InProcess)]
    [Arguments(Cluster.Gru)]
    public async Task A_mirrored_sealed_chunk_is_immutable_public_ciphertext_while_the_origin_and_live_tail_are_private_no_store(string region)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var cluster = await Cluster.StartMirroredAsync();
        var origin = cluster[region];
        var shape = $"{cluster.DatabasePath}/collections/{cluster.Collection}/shape";
        await Docs.PutAsync(origin, cluster.Document(EntityIds.New().ToString()), new JsonObject { ["value"] = "before" }, token);
        using var bootstrap = await origin.GetAsync(Target($"{shape}?offset=-1"), token);
        await Assert.That(bootstrap.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await PrivateNoStoreAsync(bootstrap);
        var handle = Header(bootstrap, "TB-Shape-Handle")!;
        var location = bootstrap.Headers.Location!.OriginalString;
        var firstPage = location.Split('?', '&').Single(part => part.StartsWith("offset=", StringComparison.Ordinal))["offset=".Length..];
        using var page = await origin.GetAsync(Target(location), token);
        await Docs.EnsureSuccessAsync(page);
        await PrivateNoStoreAsync(page);
        var snapshot = (await page.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("next_offset").GetString()!;
        await Assert.That(snapshot.StartsWith("snap.", StringComparison.Ordinal)).IsFalse().Because("the snapshot fits one page");
        await Docs.PutAsync(origin, cluster.Document(EntityIds.New().ToString()), new JsonObject { ["value"] = "after" }, token);
        var next = await Converge.UntilAsync(async () =>
        {
            using var chunk = await origin.GetAsync(Target($"{shape}?handle={handle}&offset={snapshot}"), token);
            return await ReadyAsync(chunk) && Header(chunk, "TB-Shape-Up-To-Date") == "false" ? Header(chunk, "TB-Shape-Next-Offset") : null;
        }, offset => offset is not null, $"chunk {snapshot} closed on {region}");

        var sealedChunk = await SealedChunkAsync(origin, shape, handle, firstPage, snapshot, region, token);
        await Assert.That(sealedChunk.GetProperty("next_offset").GetString()).IsEqualTo(next);
        var url = new Uri(sealedChunk.GetProperty("url").GetString()!, UriKind.Absolute);
        await Assert.That(url.Scheme).IsEqualTo(Uri.UriSchemeHttps);
        await Assert.That(url.Authority).IsNotEqualTo(origin.BaseAddress!.Authority).Because("the object store answers the object, not TerraBase");

        using var objectStore = cluster.ObjectClient(region);
        byte[]? first = null;
        for (var fetch = 1; fetch <= 2; fetch++)
        {
            using var answer = await objectStore.GetAsync(url, token);
            await Assert.That(answer.StatusCode).IsEqualTo(HttpStatusCode.OK).Because($"unauthenticated fetch {fetch} of {region}'s chunk from {url.Host}");
            var cache = answer.Headers.CacheControl;
            await Assert.That(cache is { Public: true, Private: false, NoStore: false, NoCache: false } && cache.MaxAge == TimeSpan.FromDays(365)
                && cache.Extensions.Any(extension => extension.Name == "immutable")).IsTrue().Because($"fetch {fetch} answered Cache-Control: {cache}");
            var bytes = await answer.Content.ReadAsByteArrayAsync(token);
            await Assert.That(bytes.LongLength).IsEqualTo(sealedChunk.GetProperty("size").GetInt64());
            await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(sealedChunk.GetProperty("sha256").GetString());
            await Assert.That(bytes.AsSpan().StartsWith("TBS1"u8)).IsTrue().Because("only TBS1 ciphertext is shared-cacheable");
            await Assert.That(first is null || first.AsSpan().SequenceEqual(bytes)).IsTrue().Because("an immutable object answers the same bytes");
            first = bytes;
            Console.WriteLine($"FR-24 {region} object fetch {fetch}: host {url.Host}, Cache-Control {cache}");
        }

        using var invalid = await origin.GetAsync(Target($"{shape}?offset=invalid"), token);
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await PrivateNoStoreAsync(invalid);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(Converge.Deadline);
        var live = origin.GetAsync(Target($"{shape}?handle={handle}&offset={next}&live=true"), bound.Token);
        try
        {
            await Docs.PutAsync(origin, cluster.Document(EntityIds.New().ToString()), new JsonObject { ["value"] = "live" }, token);
            using var tail = await live;
            await Docs.EnsureSuccessAsync(tail);
            await PrivateNoStoreAsync(tail);
        }
        finally
        {
            await bound.CancelAsync();
            try
            {
                using var completed = await live;
            }
            catch (OperationCanceledException) when (bound.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<JsonElement> SealedChunkAsync(HttpClient origin, string shape, string handle, string firstPage, string snapshot, string region, CancellationToken token)
    {
        var objects = await Converge.UntilAsync<JsonElement[]>(async () =>
        {
            using var manifest = await origin.GetAsync(Target($"{shape}/manifest?handle={handle}&offset={firstPage}"), token);
            if (!await ReadyAsync(manifest))
            {
                return [];
            }

            var body = await manifest.Content.ReadFromJsonAsync<JsonElement>(token);
            await Assert.That(body.GetProperty("handle").GetString()).IsEqualTo(handle);
            return [.. body.GetProperty("objects").EnumerateArray().Select(entry => entry.Clone())];
        }, listed => listed.Length >= 2 && listed[1].GetProperty("expires_at").GetDateTimeOffset() - DateTimeOffset.UtcNow >= TimeSpan.FromSeconds(15), $"{region}'s manifest of {firstPage} and chunk {snapshot}", token);
        await Assert.That(objects[0].GetProperty("offset").GetString()).IsEqualTo(firstPage);
        await Assert.That(objects[0].GetProperty("next_offset").GetString()).IsEqualTo(snapshot);
        await Assert.That(objects[1].GetProperty("offset").GetString()).IsEqualTo(snapshot);
        return objects[1];
    }

    private static async Task<bool> ReadyAsync(HttpResponseMessage response)
    {
        var ready = response.StatusCode != HttpStatusCode.ServiceUnavailable || response.Headers.RetryAfter is null;
        if (ready)
        {
            await Docs.EnsureSuccessAsync(response);
        }

        await PrivateNoStoreAsync(response);
        return ready;
    }

    private static Uri Target(string target) => new(target, UriKind.RelativeOrAbsolute);

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static async Task PrivateNoStoreAsync(HttpResponseMessage response) =>
        await Assert.That(response.Headers.CacheControl is { Private: true, NoStore: true, Public: false, MaxAge: null, SharedMaxAge: null }).IsTrue()
            .Because($"an origin {(int)response.StatusCode} answered Cache-Control: {response.Headers.CacheControl}");
}
