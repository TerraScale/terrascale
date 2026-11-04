using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Shapes;

/// <summary>
/// Free reads (PIVOT F6): sealed shape objects go to a real R2 bucket and are read through Cloudflare's cache on the bucket's
/// <c>shapes-</c> custom domain. Deleting the database destroys the key every object was sealed under (D19).
/// </summary>
public sealed class ShapeCdnTests
{
    private const int Readers = 16;

    [Test]
    public async Task Cdn_reads_fill_each_object_once_per_location_and_a_deleted_database_cannot_open_them(CancellationToken cancellationToken)
    {
        var s3 = new FakeS3(); // Backup data keys; only the shape objects use the real bucket.
        await using var db = await SqlNativeDatabase.StartAsync(services =>
        {
            s3.RoutePlatform(services);
            ManualBackupHosting.Configure(services);
            R2CdnBucket.Route(services);
        }, s3.Configure);
        var shape = new TableShapeClient(db.Reader, db.Database.Data, "notes");
        await db.OkAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)",
            "INSERT INTO notes (body) VALUES ('first'), ('second')");
        await shape.SyncAsync();
        await db.OkAsync("INSERT INTO notes (body) VALUES ('third')", "UPDATE notes SET body = 'second, edited' WHERE id = 2");
        await shape.CatchUpAsync();
        var handle = shape.Handle!;
        using var bucket = R2CdnBucket.Client();
        try
        {
            // The snapshot page and the change chunk after it, once the mirror has published both.
            var bootstrap = await shape.GetAsync(new Uri($"{shape.Path}?offset=-1", UriKind.Relative));
            var first = bootstrap.Location!.OriginalString.Split('?')[1].Split('&').Single(part => part.StartsWith("offset=", StringComparison.Ordinal))[7..];
            JsonElement[] objects = [];
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var manifest = await db.Reader.GetAsync(new Uri($"{shape.Path}/manifest?handle={handle}&offset={first}", UriKind.Relative), token);
                await Assert.That(manifest.StatusCode).IsEqualTo(HttpStatusCode.OK);
                objects = [.. (await manifest.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("objects").EnumerateArray()];
                return objects.Length >= 2 && !objects[^1].GetProperty("offset").GetString()!.StartsWith("snap.", StringComparison.Ordinal);
            }, timeout: TimeSpan.FromMinutes(2));

            using var cdn = new HttpClient();
            var plain = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var ciphertext = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var item in objects)
            {
                var url = new Uri(item.GetProperty("url").GetString()!);
                await Assert.That(url.Host).IsEqualTo(R2CdnBucket.CdnHost);
                var cold = await FetchAsync(cdn, url, item, cancellationToken);
                var warm = await Task.WhenAll(Enumerable.Range(0, Readers).Select(_ => FetchAsync(cdn, url, item, cancellationToken)));
                // A location fills from the bucket, or from its Smart Tiered Cache upper tier, at most once; every later reader there hits.
                await Assert.That(cold.Status).IsNotEqualTo("HIT").Because(url.AbsoluteUri);
                foreach (var location in warm.Append(cold).GroupBy(read => read.Location))
                {
                    await Assert.That(location.Count(read => read.Status != "HIT")).IsLessThanOrEqualTo(1).Because($"{url} at {location.Key}");
                }

                var key = await KeyAsync(db, shape, handle, item, cancellationToken);
                await Assert.That(key).IsNotNull();
                plain[url.AbsoluteUri] = Open(cold.Bytes, handle, item.GetProperty("offset").GetString()!, key!);
                ciphertext[url.AbsoluteUri] = cold.Bytes;
            }

            await Assert.That(plain.Values.Any(bytes => Encoding.UTF8.GetString(bytes).Contains("second, edited", StringComparison.Ordinal))).IsTrue();

            var tenant = new TenantContext(db.Database.Organization, PlanTier.Hobby, db.Database.Project, db.Database.Id);
            using (var deleted = await db.Api.Client.DeleteAsync(ManagementApi.Uri(db.Database.Path), cancellationToken))
            {
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            await db.Api.Server.Services.GetRequiredService<DatabaseBackupPurge>().RunOnceAsync(cancellationToken);
            var keys = db.Api.Server.Services.GetRequiredService<DataKeys>();
            foreach (var item in objects)
            {
                var url = new Uri(item.GetProperty("url").GetString()!);
                // The cache still serves the ciphertext, but no key that opens it exists anywhere.
                var cached = await FetchAsync(cdn, url, item, cancellationToken);
                await Assert.That(cached.Bytes).IsEquivalentTo(ciphertext[url.AbsoluteUri]);
                await Assert.That(await KeyAsync(db, shape, handle, item, cancellationToken)).IsNull();
                await Assert.That(() => (Task)keys.GetAsync(BackupLayout.KeyScope(tenant), item.GetProperty("key_id").GetString()!, cancellationToken))
                    .Throws<DatabaseShreddedException>();
            }
        }
        finally
        {
            await R2CdnBucket.DeleteHandleAsync(bucket, handle, CancellationToken.None);
        }
    }

    private static async Task<CdnRead> FetchAsync(HttpClient cdn, Uri url, JsonElement item, CancellationToken cancellationToken)
    {
        using var response = await cdn.GetAsync(url, cancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(url.AbsoluteUri);
        await Assert.That(response.Headers.CacheControl?.ToString()).IsEqualTo("public, max-age=31536000, immutable");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await Assert.That((long)bytes.Length).IsEqualTo(item.GetProperty("size").GetInt64());
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(item.GetProperty("sha256").GetString());
        var status = response.Headers.TryGetValues("cf-cache-status", out var statuses) ? statuses.Single() : "";
        var ray = response.Headers.TryGetValues("cf-ray", out var rays) ? rays.Single() : "";
        return new CdnRead(status, ray[(ray.LastIndexOf('-') + 1)..], bytes);
    }

    private static async Task<byte[]?> KeyAsync(SqlNativeDatabase db, TableShapeClient shape, string handle, JsonElement item, CancellationToken cancellationToken)
    {
        using var response = await db.Reader.GetAsync(new Uri(
            $"{shape.Path}/keys?handle={handle}&epoch={item.GetProperty("epoch").GetString()}&key_id={item.GetProperty("key_id").GetString()}",
            UriKind.Relative), cancellationToken);
        return response.StatusCode == HttpStatusCode.OK
            ? Base64Url.DecodeFromChars((await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("key").GetString())
            : null;
    }

    /// <summary>The client side of TBS1 (<see cref="ShapeCrypto.Seal"/>): authenticate every frame and return the plaintext.</summary>
    private static byte[] Open(byte[] sealedBytes, string handle, string offset, byte[] epochKey)
    {
        const int header = ShapeCrypto.HeaderBytes;
        const int tag = 16;
        const uint last = 1u << 31;
        var salt = sealedBytes.AsSpan(header - 32, 32).ToArray();
        var objectKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, epochKey, 32, salt, Encoding.UTF8.GetBytes("TBS1 object\0" + handle + "\0" + offset));
        using var aes = new AesGcm(objectKey, tag);
        using var plain = new MemoryStream();
        var associated = new byte[header + 12];
        sealedBytes.AsSpan(0, header).CopyTo(associated);
        Span<byte> nonce = stackalloc byte[12];
        var at = header;
        for (ulong index = 0; ; index++)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(sealedBytes.AsSpan(at));
            var length = (int)(word & ~last);
            nonce.Clear();
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], index);
            BinaryPrimitives.WriteUInt64BigEndian(associated.AsSpan(header), index);
            BinaryPrimitives.WriteUInt32BigEndian(associated.AsSpan(header + 8), word);
            var frame = new byte[length];
            aes.Decrypt(nonce, sealedBytes.AsSpan(at + 4, length), sealedBytes.AsSpan(at + 4 + length, tag), frame, associated);
            plain.Write(frame);
            at += 4 + length + tag;
            if ((word & last) != 0)
            {
                return at == sealedBytes.Length ? plain.ToArray() : throw new InvalidDataException("bytes after the last TBS1 frame");
            }
        }
    }

    private sealed record CdnRead(string Status, string Location, byte[] Bytes);
}

/// <summary>
/// The private <c>ts-terrabase-shapes-test</c> R2 bucket and its <c>shapes-ts-terrabase-shapes-test.terrascale.tech</c> domain, which share the
/// production zone's shape WAF, cache rule and Smart Tiered Cache. Credentials are bucket-scoped S3 keys.
/// </summary>
internal static class R2CdnBucket
{
    private const string Bucket = "ts-terrabase-shapes-test";

    public const string CdnHost = "shapes-" + Bucket + ".terrascale.tech";

    public static void Route(IServiceCollection services)
    {
        var endpoint = new Uri(Variable("R2_CI_ENDPOINT"));
        if (!endpoint.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("R2_CI_ENDPOINT must be the account's R2 S3 endpoint");
        }

        services.AddSingleton(new ShapeStorageSettings
        {
            Endpoint = endpoint,
            Region = "auto",
            Bucket = Bucket,
            Cdn = new Uri("https://" + CdnHost + "/"),
            AccessKeyId = Variable("R2_CI_ACCESS_KEY_ID"),
            SecretKey = Variable("R2_CI_SECRET_ACCESS_KEY"),
            CloudflareApiToken = FakeCloudflare.Token,
        });
        services.AddHttpClient(ShapeObjectStore.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler());
    }

    public static AmazonS3Client Client() => new(Variable("R2_CI_ACCESS_KEY_ID"), Variable("R2_CI_SECRET_ACCESS_KEY"), new AmazonS3Config
    {
        ServiceURL = Variable("R2_CI_ENDPOINT"),
        AuthenticationRegion = "auto",
        ForcePathStyle = true,
    });

    public static async Task DeleteHandleAsync(AmazonS3Client client, string handle, CancellationToken cancellationToken)
    {
        var listed = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Prefix = $"v1/shapes/{handle}/" }, cancellationToken);
        foreach (var item in listed.S3Objects ?? [])
        {
            await client.DeleteObjectAsync(Bucket, item.Key, cancellationToken);
        }
    }

    private static string Variable(string name) => Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
        ? value : throw new InvalidOperationException($"required R2 CI credential {name} is missing");
}
