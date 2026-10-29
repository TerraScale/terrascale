using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Features.Replication;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;
using TerraBase.Slow.Backup;
using TerraBase.Testing;

namespace TerraBase.Slow.Shapes;

/// <summary>
/// Free reads tier T1 against a real S3-compatible bucket (<c>make terrabase-test-slow</c>, the bucket-scoped CI
/// credentials): the server's own mirror uploads sealed TBS1 objects, the manifest lists them, an anonymous client fetches
/// each presigned URL straight from the bucket, and the bytes open with the key route's key to exactly what the origin served.
/// Only endpoint, bucket and keys name the provider; the same test proves Tigris and R2, and on R2 also the CDN tier T2.
/// </summary>
public sealed class S3ShapeMirrorTests
{
    private const string Collection = "items";
    private static readonly TimeSpan MirrorDeadline = TimeSpan.FromMinutes(1);

    [Test]
    public async Task Sealed_shape_objects_are_fetched_from_the_bucket_by_presigned_url_and_open_to_the_origin_bytes(CancellationToken cancellationToken)
    {
        var faker = new Faker();
        var prefix = $"ci/{EntityIds.New()}/";
        using var s3 = TigrisCiBucket.Client();
        ManagementApi? api = null;
        string? handle = null;
        var failedStartCleanup = Task.CompletedTask;
        try
        {
            api = await ManagementApi.StartAsync(TigrisCiBucket.Configure,
                services: services =>
                {
                    services.AddSingleton<IObjectStore>(provider => new TigrisBackupTests.PrefixedStore(provider.GetRequiredService<S3ObjectStore>(), prefix));
                    services.AddSingleton(provider =>
                    {
                        var settings = provider.GetRequiredService<BackupSettings>();
                        return new ShapeStorageSettings
                        {
                            Endpoint = settings.Endpoint,
                            Region = settings.Region,
                            Bucket = settings.Bucket,
                            AccessKeyId = settings.AccessKeyId,
                            SecretKey = settings.SecretKey,
                            CloudflareApiToken = settings.CloudflareApiToken ?? string.Empty,
                            Cdn = TigrisCiBucket.R2 ? new Uri($"https://shapes-{settings.Bucket}.terrascale.tech/") : null,
                        };
                    });
                },
                cancellationToken: cancellationToken, failedStartCleanup: cleanup => failedStartCleanup = cleanup);
            // The catalog gate opens once this machine's catalog anchor is proven in the bucket.
            var gate = api.Server.Services.GetRequiredService<ICatalogGate>();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gate.IsOpen), MirrorDeadline);
            var database = await api.CreateManagedDatabaseAsync("main");
            using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, database, Collection, cancellationToken);
            var shape = $"{database.Data}/collections/{Collection}/shape";
            await PutAsync(data, database, faker, cancellationToken);

            using var bootstrap = await data.GetAsync(ManagementApi.Uri($"{shape}?offset=-1"), cancellationToken);
            await Assert.That(bootstrap.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
            handle = bootstrap.Headers.GetValues("TB-Shape-Handle").Single();
            var location = bootstrap.Headers.Location!.OriginalString;
            var firstPage = location.Split('&').Single(part => part.StartsWith("offset=", StringComparison.Ordinal))["offset=".Length..];
            using var page = await data.GetAsync(ManagementApi.Uri(location), cancellationToken);
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var pageBytes = await page.Content.ReadAsByteArrayAsync(cancellationToken);
            var snapshot = page.Headers.GetValues("TB-Shape-Next-Offset").Single();

            // A write after the snapshot closes chunk S; reading it seals it, and the sealer callback mirrors it.
            await PutAsync(data, database, faker, cancellationToken);
            using var chunk = await data.GetAsync(ManagementApi.Uri($"{shape}?handle={handle}&offset={snapshot}"), cancellationToken);
            await Assert.That(chunk.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(chunk.Headers.GetValues("TB-Shape-Up-To-Date").Single()).IsEqualTo("false");
            var chunkBytes = await chunk.Content.ReadAsByteArrayAsync(cancellationToken);

            var objects = await ManifestAsync(data, $"{shape}/manifest?handle={handle}&offset={firstPage}", count: 2, cancellationToken);
            await Assert.That(objects[0].GetProperty("offset").GetString()).IsEqualTo(firstPage);
            await Assert.That(objects[1].GetProperty("offset").GetString()).IsEqualTo(snapshot);

            // Anonymous: the presigned URL alone authorizes the bucket read, and the object carries its shared-cache policy.
            using var anonymous = new HttpClient();
            byte[][] opened = new byte[objects.Length][];
            for (var index = 0; index < 2; index++)
            {
                var entry = objects[index];
                using var fetched = await anonymous.GetAsync(new Uri(entry.GetProperty("url").GetString()!), cancellationToken);
                await Assert.That(fetched.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await CacheControlAssert.EqualAsync(fetched.Headers.CacheControl?.ToString(), ShapeObjectStore.CacheControl);
                var bytes = await fetched.Content.ReadAsByteArrayAsync(cancellationToken);
                await Assert.That(bytes.LongLength).IsEqualTo(entry.GetProperty("size").GetInt64());
                await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(entry.GetProperty("sha256").GetString());
                var epoch = entry.GetProperty("epoch").GetString()!;
                var keyId = entry.GetProperty("key_id").GetString()!;
                using var key = await data.GetAsync(ManagementApi.Uri($"{shape}/keys?handle={handle}&epoch={epoch}&key_id={keyId}"), cancellationToken);
                await Assert.That(key.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var released = await key.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                opened[index] = ShapeObjects.Open(bytes, handle, entry.GetProperty("offset").GetString()!, long.Parse(epoch, CultureInfo.InvariantCulture),
                    keyId, Base64Url.DecodeFromChars(released.GetProperty("key").GetString()!));
            }

            await Assert.That(opened[0].SequenceEqual(pageBytes)).IsTrue().Because("the bucket's snapshot page opens to the page the origin served");
            await Assert.That(opened[1].SequenceEqual(chunkBytes)).IsTrue().Because("the bucket's chunk opens to the chunk the origin served");

            // Free reads T2: an R2 bucket links its objects by their path on its Cloudflare custom domain, and a repeated read
            // is answered from Cloudflare's cache. Any other bucket keeps presigned links (T1).
            var link = new Uri(objects[1].GetProperty("url").GetString()!);
            if (TigrisCiBucket.R2)
            {
                await Assert.That(link.GetLeftPart(UriPartial.Path)).IsEqualTo($"https://shapes-{TigrisCiBucket.Bucket}.terrascale.tech/{link.AbsolutePath[1..]}");
                await Assert.That(link.Query).IsEmpty().Because("the CDN key is the path alone, the same for every reader and epoch");
                await CachedAsync(anonymous, link, cancellationToken);
            }
            else
            {
                await Assert.That(link.Query).Contains("X-Amz-Signature=");
            }
        }
        finally
        {
            // The mirror and shipper stop with the server, before this invocation's objects are deleted.
            try
            {
                if (api is not null)
                {
                    try
                    {
                        await api.DisposeAsync();
                    }
                    finally
                    {
                        await api.CleanupCompletion;
                    }
                }
                else
                {
                    await failedStartCleanup;
                }
            }
            finally
            {
                var owned = await TigrisBackupTests.ListAsync(s3, prefix, CancellationToken.None);
                if (handle is not null)
                {
                    owned.AddRange(await TigrisBackupTests.ListAsync(s3, $"v1/shapes/{handle}/", CancellationToken.None));
                }

                foreach (var key in owned)
                {
                    await s3.DeleteObjectAsync(TigrisCiBucket.Bucket, key, CancellationToken.None);
                }
            }
        }
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, Faker faker, CancellationToken cancellationToken) =>
        (await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, Collection, EntityIds.New().ToString())),
            new { text = faker.Lorem.Sentence() }, cancellationToken)).EnsureSuccessStatusCode();

    // Cloudflare caches on the first read at a location; poll until a read is a HIT, so a cache that never serves fails.
    private static async Task CachedAsync(HttpClient anonymous, Uri link, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MirrorDeadline);
        while (true)
        {
            using var response = await anonymous.GetAsync(link, deadline.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            if (response.Headers.TryGetValues("cf-cache-status", out var status) && status.Single() == "HIT")
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
        }
    }

    // The mirror uploads in the background; poll the manifest until it lists the expected objects.
    private static async Task<JsonElement[]> ManifestAsync(HttpClient data, string target, int count, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MirrorDeadline);
        while (true)
        {
            using var response = await data.GetAsync(ManagementApi.Uri(target), deadline.Token);
            response.EnsureSuccessStatusCode();
            var objects = (await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("objects").EnumerateArray().Select(entry => entry.Clone()).ToArray();
            if (objects.Length >= count)
            {
                return objects;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
        }
    }
}
