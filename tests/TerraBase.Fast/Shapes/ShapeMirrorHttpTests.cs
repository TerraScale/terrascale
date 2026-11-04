using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

/// <summary>
/// The shape mirror over the real table shape read path: every shape service, flight, sealer, snapshot store, retention
/// pass, crypto and mirror is the server's own registration, and only the platform bucket is the in-memory fake. Objects
/// appear only through the services' seal callbacks, are listed by the manifest route, and open with the key route's key.
/// </summary>
public sealed class ShapeMirrorHttpTests
{
    [Test]
    public async Task Real_http_snapshot_and_chunk_reads_are_mirrored_by_the_service_callbacks_and_decrypt_to_the_origin_bytes()
    {
        await using var host = await MirrorHost.StartAsync();
        await host.PutAsync(new { status = "open" });
        var shape = await host.BootstrapAsync();
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

        // Once mirrored, listing the offsets again costs the origin no payload read: T1 readers are free at the origin.
        var before = await host.ScrapeAsync();
        var again = await host.ManifestAsync(shape, shape.FirstPage, count: 2);
        var after = await host.ScrapeAsync();
        await Assert.That(again.Select(entry => entry.GetProperty("sha256").GetString())).IsEquivalentTo(objects.Select(entry => entry.GetProperty("sha256").GetString()));
        await Assert.That(Delta(before, after, "terrabase_storage_reads_total", "path=\"shape_body\"")).IsEqualTo(0d)
            .Because("a listed offset is answered from the mirror's metadata");
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
    private sealed record Shape(string Base, string Handle, string ShapeId, string FirstPage, string Snapshot, byte[] PageBytes)
    {
        public Uri Uri(string offset, bool live = false) =>
            new($"{Base}/shape?handle={Handle}&offset={offset}{(live ? "&live=true" : "")}", UriKind.Relative);

        public Uri ManifestUri(string offset, string? handle = null) =>
            new($"{Base}/shape/manifest?handle={handle ?? Handle}&offset={offset}", UriKind.Relative);
    }

    /// <summary>One stock server whose platform bucket is the in-memory fake; nothing in the shape slice is replaced.</summary>
    private sealed class MirrorHost(DatabaseApi api, FakeS3 s3) : IAsyncDisposable
    {
        public DatabaseApi Api { get; } = api;

        public FakeS3 S3 { get; } = s3;

        public CancellationToken Cancellation { get; } = TUnit.Core.TestContext.Current!.Execution.CancellationToken;

        private string Base => $"{Api.DatabasePath()}/tables/notes";

        public static async Task<MirrorHost> StartAsync(FakeS3? shapes = null, FakeS3? backups = null)
        {
            shapes ??= new FakeS3();
            backups ??= shapes;
            var api = await DatabaseApi.StartAsync(backups.Configure, services =>
            {
                backups.RoutePlatform(services);
                shapes.RouteShapes(services);
                ManualBackupHosting.Configure(services);
            });
            var created = await api.SendRawAsync(HttpMethod.Post, api.DatabasePath() + "/sql/execute",
                """{"statements":[{"sql":"CREATE TABLE notes (id INTEGER PRIMARY KEY, status TEXT, pad TEXT)"}]}""", "application/json");
            created.Status.ShouldBe(HttpStatusCode.OK);
            return new MirrorHost(api, shapes);
        }

        public ValueTask DisposeAsync() => Api.DisposeAsync();

        public async Task PutAsync(object row)
        {
            var body = JsonSerializer.Serialize(new { statements = new[] { new { sql = "INSERT INTO notes (status, pad) SELECT json_extract(?1, '$.status'), json_extract(?1, '$.pad')", args = new object[] { JsonSerializer.Serialize(row) } } } });
            (await Api.SendRawAsync(HttpMethod.Post, Api.DatabasePath() + "/sql/execute", body, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        }

        public Task<HttpResponseMessage> GetAsync(Uri target) => Api.Client.GetAsync(target, Cancellation);

        public Task<string> ScrapeAsync() => Api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), Cancellation);

        /// <summary>offset=-1, then the 307's single snapshot page, whose next_offset is the decimal snapshot offset.</summary>
        public async Task<Shape> BootstrapAsync()
        {
            var target = $"{Base}/shape?offset=-1";
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

            return new Shape(Base, handle, json.RootElement.GetProperty("shape_id").GetString()!, firstPage, snapshot, bytes);
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
            using var platform = Api.Server.Services.GetRequiredService<IHttpClientFactory>().CreateClient("shapes-s3");
            var bytes = await platform.GetByteArrayAsync(new Uri(entry.GetProperty("url").GetString()!), Cancellation);
            Require(bytes.Length == entry.GetProperty("size").GetInt64(), "the object has the listed size");
            Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == entry.GetProperty("sha256").GetString(), "the object has the listed sha256");
            var epoch = entry.GetProperty("epoch").GetString()!;
            var keyId = entry.GetProperty("key_id").GetString()!;
            using var key = await GetAsync(new Uri($"{Base}/shape/keys?handle={shape.Handle}&epoch={epoch}&key_id={keyId}", UriKind.Relative));
            key.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var released = JsonDocument.Parse(await key.Content.ReadAsByteArrayAsync());
            return ShapeObjects.Open(bytes, shape.Handle, entry.GetProperty("offset").GetString()!, long.Parse(epoch, CultureInfo.InvariantCulture), keyId,
                Base64Url.DecodeFromChars(released.RootElement.GetProperty("key").GetString()!));
        }
    }
}
