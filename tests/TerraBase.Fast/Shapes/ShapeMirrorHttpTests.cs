using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Shapes;
using TerraBase.Fast.Documents;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

/// <summary>
/// The shape mirror (FR-22) over the real FR-20 read path: every shape service, flight, sealer, snapshot store, retention
/// pass, crypto and mirror is the server's own registration, and only the platform bucket is the in-memory fake. Objects
/// appear only through the services' seal callbacks, are listed by the manifest route, and open with the key route's key.
/// </summary>
public sealed class ShapeMirrorHttpTests
{
    private const string Where = """{"eq":["/status","open"]}""";
    private const int LargeBody = 600_000;

    [Test]
    public async Task Real_http_snapshot_and_chunk_reads_are_mirrored_by_the_service_callbacks_and_decrypt_to_the_origin_bytes()
    {
        await using var host = await MirrorHost.StartAsync();
        await host.PutAsync(new { status = "open" });
        var shape = await host.BootstrapAsync(where: null);
        await host.PutAsync(new { status = "open" });
        using var chunk = await host.GetAsync(shape.Uri(shape.Snapshot));
        await Assert.That(chunk.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Header(chunk, "TB-Shape-Up-To-Date")).IsEqualTo("false").Because("a write after the snapshot closes chunk S");
        var chunkBytes = await chunk.Content.ReadAsByteArrayAsync();

        // The snapshot callback mirrors snap.S.0 and the sealer callback mirrors chunk S; the manifest chains both.
        var objects = await host.ManifestAsync(shape, shape.FirstPage, count: 2);
        await Assert.That(objects[0].GetProperty("offset").GetString()).IsEqualTo(shape.FirstPage);
        await Assert.That(objects[0].GetProperty("next_offset").GetString()).IsEqualTo(shape.Snapshot);
        await Assert.That(objects[1].GetProperty("offset").GetString()).IsEqualTo(shape.Snapshot);
        await Assert.That(objects[1].GetProperty("next_offset").GetString()).IsEqualTo(Header(chunk, "TB-Shape-Next-Offset"));
        await Assert.That((await host.OpenAsync(shape, objects[0])).SequenceEqual(shape.PageBytes)).IsTrue()
            .Because("the mirrored snapshot page decrypts to the page the origin served");
        await Assert.That((await host.OpenAsync(shape, objects[1])).SequenceEqual(chunkBytes)).IsTrue()
            .Because("the mirrored chunk decrypts to the chunk the origin served");
    }

    [Test]
    public async Task A_refused_upload_leaves_the_manifest_empty_and_the_origin_serving_the_same_chunk()
    {
        await using var host = await MirrorHost.StartAsync();
        host.S3.FailPut = key => key.StartsWith("v1/shapes/", StringComparison.Ordinal);
        await host.PutAsync(new { status = "open" });
        var shape = await host.BootstrapAsync(where: null);
        var chunkKey = ShapeObjectStore.Key(shape.ShapeId, shape.Handle, shape.Snapshot);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.S3.FailPut = key =>
        {
            if (key == chunkKey)
            {
                attempted.TrySetResult();
            }
            return key.StartsWith("v1/shapes/", StringComparison.Ordinal);
        };
        await host.PutAsync(new { status = "open" });
        using var first = await host.GetAsync(shape.Uri(shape.Snapshot));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var origin = await first.Content.ReadAsByteArrayAsync();
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(30), host.Cancellation);

        using (var manifest = await host.GetAsync(shape.ManifestUri(shape.Snapshot)))
        {
            await Assert.That(manifest.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var wire = JsonDocument.Parse(await manifest.Content.ReadAsByteArrayAsync());
            await Assert.That(wire.RootElement.GetProperty("objects").GetArrayLength()).IsEqualTo(0)
                .Because("a refused upload is never listed, so clients fall back to the origin");
        }

        using var again = await host.GetAsync(shape.Uri(shape.Snapshot));
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await again.Content.ReadAsByteArrayAsync()).SequenceEqual(origin)).IsTrue();
        await Assert.That(host.S3.Objects.ContainsKey(chunkKey)).IsFalse();
    }

    [Test]
    public async Task Parked_readers_with_the_mirror_on_share_one_payload_read_and_the_woken_chunk_is_mirrored()
    {
        await using var host = await MirrorHost.StartAsync();
        await host.PutAsync(new { status = "open" });
        var shape = await host.BootstrapAsync(where: null);
        // The snapshot mirror reads its stored pages in the background; it finishes before the wave's baseline.
        _ = await host.ManifestAsync(shape, shape.FirstPage, count: 1);

        var before = await host.ScrapeAsync();
        const int Readers = 4;
        List<Task<HttpResponseMessage>> parked = [];
        for (var reader = 0; reader < Readers; reader++)
        {
            parked.Add(host.GetAsync(shape.Uri(shape.Snapshot, live: true)));
            await TerraBaseTestServer.EventuallyAsync(async _ =>
                parked.Any(task => task.IsCompleted) || Total(await host.ScrapeAsync(), "terrabase_shape_requests_waiting", null) >= parked.Count);
            await Assert.That(parked.Any(task => task.IsCompleted)).IsFalse().Because("no reader answers before the commit");
        }

        await host.PutAsync(new { status = "open" });
        var answers = await Task.WhenAll(parked);
        var after = await host.ScrapeAsync();
        List<string> bodies = [];
        foreach (var answer in answers)
        {
            using (answer)
            {
                await Assert.That(answer.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(Header(answer, "TB-Shape-Up-To-Date")).IsEqualTo("true");
                bodies.Add(await answer.Content.ReadAsStringAsync());
            }
        }

        await Assert.That(bodies.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1);
        await Assert.That(Delta(before, after, "terrabase_shape_seals_total", null)).IsEqualTo(1d).Because("one commit wakes one shared seal");
        await Assert.That(Delta(before, after, "terrabase_storage_reads_total", "path=\"shape_body\"")).IsEqualTo(1d)
            .Because("the mirror takes the seal's retained bytes, so the wake still reads one stored payload");

        // No non-live read touched S since it closed, so only the sealer callback can have uploaded it.
        var objects = await host.ManifestAsync(shape, shape.Snapshot, count: 1);
        using var closed = await host.GetAsync(shape.Uri(shape.Snapshot));
        await Assert.That(closed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await host.OpenAsync(shape, objects[0])).SequenceEqual(await closed.Content.ReadAsByteArrayAsync())).IsTrue();
    }

    [Test]
    public async Task A_filtered_manifest_and_key_follow_the_filtered_handle_and_release_every_read_lease()
    {
        await using var host = await MirrorHost.StartAsync();
        await host.PutAsync(new { status = "open" });
        await host.PutAsync(new { status = "closed" });
        var shape = await host.BootstrapAsync(Where);
        var unfiltered = await host.BootstrapAsync(where: null);
        await Assert.That(shape.Handle).IsNotEqualTo(unfiltered.Handle);
        // About 600 KB, so one leaked read lease is far larger than the server's background reservations.
        await host.PutAsync(new { status = "open", pad = new string('x', LargeBody) });
        using (var chunk = await host.GetAsync(shape.Uri(shape.Snapshot)))
        {
            await Assert.That(chunk.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var objects = await host.ManifestAsync(shape, shape.Snapshot, count: 1);
            await Assert.That((await host.OpenAsync(shape, objects[0])).SequenceEqual(await chunk.Content.ReadAsByteArrayAsync())).IsTrue()
                .Because("the key route releases the filtered handle's key, which opens its mirrored chunk");
        }

        // The filtered handle without its where names another lineage.
        using (var stale = await host.GetAsync(unfiltered.ManifestUri(shape.Snapshot, handle: shape.Handle)))
        {
            await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(Header(stale, "TB-Shape-Handle")).IsEqualTo(unfiltered.Handle);
            using var error = JsonDocument.Parse(await stale.Content.ReadAsByteArrayAsync());
            await Assert.That(error.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("must_refetch");
        }

        var baseline = await host.SettledReservedBytesAsync();
        for (var request = 0; request < 4; request++)
        {
            using var listed = await host.GetAsync(shape.ManifestUri(shape.Snapshot));
            await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await TerraBaseTestServer.EventuallyAsync(async token => await host.Api.ReservedBytesAsync(token) < baseline + LargeBody,
            TimeSpan.FromSeconds(30));
    }

    private static void Require(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidDataException(because);
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static double Delta(string before, string after, string name, string? label) => Total(after, name, label) - Total(before, name, label);

    private static double Total(string scrape, string name, string? label) => scrape.Split('\n')
        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#') && line[..line.IndexOfAny(['{', ' '])] == name
            && (label is null || line.Contains(label, StringComparison.Ordinal)))
        .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture));

    /// <summary>One bootstrapped lineage: its handle, the snapshot's first page as served, and the decimal snapshot offset.</summary>
    private sealed record Shape(string Base, string? Where, string Handle, string ShapeId, string FirstPage, string Snapshot, byte[] PageBytes)
    {
        public Uri Uri(string offset, bool live = false) =>
            new($"{Base}/shape?handle={Handle}&offset={offset}{(live ? "&live=true" : "")}{Filter}", UriKind.Relative);

        public Uri ManifestUri(string offset, string? handle = null) =>
            new($"{Base}/shape/manifest?handle={handle ?? Handle}&offset={offset}{Filter}", UriKind.Relative);

        private string Filter => Where is null ? "" : "&where=" + System.Uri.EscapeDataString(Where);
    }

    /// <summary>One stock server whose platform bucket is the in-memory fake; nothing in the shape slice is replaced.</summary>
    private sealed class MirrorHost(DocumentApi api, FakeS3 s3) : IAsyncDisposable
    {
        public DocumentApi Api { get; } = api;

        public FakeS3 S3 { get; } = s3;

        public CancellationToken Cancellation { get; } = TUnit.Core.TestContext.Current!.Execution.CancellationToken;

        private string Base => $"{Api.Collections}/{DocumentApi.Collection}";

        public static async Task<MirrorHost> StartAsync()
        {
            var s3 = new FakeS3();
            var api = await DocumentApi.StartAsync(s3.Configure, services =>
            {
                s3.RoutePlatform(services);
                ManualBackupHosting.Configure(services);
            });
            return new MirrorHost(api, s3);
        }

        public ValueTask DisposeAsync() => Api.DisposeAsync();

        public async Task PutAsync(object document) =>
            (await Api.PutAsync(DocumentApi.NewId(), document)).Status.ShouldBe(HttpStatusCode.Created);

        public Task<HttpResponseMessage> GetAsync(Uri target) => Api.Client.GetAsync(target, Cancellation);

        public Task<string> ScrapeAsync() => Api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), Cancellation);

        /// <summary>offset=-1, then the 307's single snapshot page, whose next_offset is the decimal snapshot offset.</summary>
        public async Task<Shape> BootstrapAsync(string? where)
        {
            var target = $"{Base}/shape?offset=-1" + (where is null ? "" : "&where=" + Uri.EscapeDataString(where));
            using var bootstrap = await GetAsync(new Uri(target, UriKind.Relative));
            bootstrap.StatusCode.ShouldBe(HttpStatusCode.TemporaryRedirect);
            var handle = Header(bootstrap, "TB-Shape-Handle")!;
            var location = bootstrap.Headers.Location!.OriginalString;
            var firstPage = location.Split('&').Single(part => part.StartsWith("offset=", StringComparison.Ordinal))["offset=".Length..];
            using var page = await GetAsync(new Uri(location, UriKind.Relative));
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            var bytes = await page.Content.ReadAsByteArrayAsync();
            using var json = JsonDocument.Parse(bytes);
            var snapshot = json.RootElement.GetProperty("next_offset").GetString()!;
            if (snapshot.StartsWith("snap.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("the test's snapshot must fit one page");
            }

            return new Shape(Base, where, handle, json.RootElement.GetProperty("shape_id").GetString()!, firstPage, snapshot, bytes);
        }

        /// <summary>Polls the manifest route until it lists <paramref name="count"/> objects from <paramref name="offset"/>.</summary>
        public async Task<JsonElement[]> ManifestAsync(Shape shape, string offset, int count)
        {
            JsonElement[] objects = [];
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var response = await Api.Client.GetAsync(shape.ManifestUri(offset), token);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                using var wire = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
                objects = [.. wire.RootElement.GetProperty("objects").EnumerateArray().Select(entry => entry.Clone())];
                return objects.Length >= count;
            }, TimeSpan.FromSeconds(30));
            return objects;
        }

        /// <summary>Fetches the presigned object, checks its size and digest, and opens it with the key route's epoch key.</summary>
        public async Task<byte[]> OpenAsync(Shape shape, JsonElement entry)
        {
            using var platform = Api.Server.Services.GetRequiredService<IHttpClientFactory>().CreateClient("backup-s3");
            var bytes = await platform.GetByteArrayAsync(new Uri(entry.GetProperty("url").GetString()!), Cancellation);
            Require(bytes.Length == entry.GetProperty("size").GetInt64(), "the object has the listed size");
            Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == entry.GetProperty("sha256").GetString(), "the object has the listed sha256");
            var epoch = entry.GetProperty("epoch").GetString()!;
            var keyId = entry.GetProperty("key_id").GetString()!;
            using var key = await GetAsync(new Uri($"{Base}/shape/keys?handle={shape.Handle}&epoch={epoch}&key_id={keyId}", UriKind.Relative));
            key.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var released = JsonDocument.Parse(await key.Content.ReadAsByteArrayAsync());
            return Open(bytes, shape.Handle, entry.GetProperty("offset").GetString()!, long.Parse(epoch, CultureInfo.InvariantCulture), keyId,
                Base64Url.DecodeFromChars(released.RootElement.GetProperty("key").GetString()!));
        }

        /// <summary>The reserved-bytes gauge once two consecutive scrapes agree.</summary>
        public async Task<double> SettledReservedBytesAsync()
        {
            var previous = -1d;
            var current = 0d;
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                (previous, current) = (current, await Api.ReservedBytesAsync(token));
                return previous == current;
            }, TimeSpan.FromSeconds(30));
            return current;
        }
    }

    // TBS1 (shape-reads.md section 12): header, then length-word framed AES-GCM; a bad tag throws, failing the test.
    private static byte[] Open(byte[] bytes, string handle, string offset, long epoch, string keyId, byte[] epochKey)
    {
        const int HeaderLength = ShapeCrypto.HeaderBytes;
        const int Tag = 16;
        Require(bytes.AsSpan(0, 4).SequenceEqual("TBS1"u8), "the object is TBS1");
        Require((long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(4)) == epoch, "the header names the listed epoch");
        Require(Encoding.ASCII.GetString(bytes, 12, keyId.Length) == keyId, "the header names the listed key id");
        var objectKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, epochKey, 32, bytes[(12 + keyId.Length)..HeaderLength],
            Encoding.UTF8.GetBytes("TBS1 object\0" + handle + "\0" + offset));
        using var aes = new AesGcm(objectKey, Tag);
        var plain = new List<byte>();
        var nonce = new byte[12];
        var associated = new byte[HeaderLength + 8 + 4];
        bytes.AsSpan(0, HeaderLength).CopyTo(associated);
        for (int at = HeaderLength, index = 0; ; index++)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            var length = (int)(word & ~(1u << 31));
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), (ulong)index);
            BinaryPrimitives.WriteUInt64BigEndian(associated.AsSpan(HeaderLength), (ulong)index);
            BinaryPrimitives.WriteUInt32BigEndian(associated.AsSpan(HeaderLength + 8), word);
            var piece = new byte[length];
            aes.Decrypt(nonce, bytes.AsSpan(at + 4, length), bytes.AsSpan(at + 4 + length, Tag), piece, associated);
            plain.AddRange(piece);
            at += 4 + length + Tag;
            if ((word & (1u << 31)) != 0)
            {
                Require(at == bytes.Length, "nothing follows the last frame");
                return [.. plain];
            }
        }
    }
}
