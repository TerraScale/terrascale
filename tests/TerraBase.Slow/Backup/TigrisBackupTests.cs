using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class TigrisBackupTests
{
    private static readonly TimeSpan ShippingDeadline = TimeSpan.FromMinutes(2);

    [Test]
    public async Task A_database_shipped_to_Tigris_restores_identically_after_its_documents_are_wiped(CancellationToken cancellationToken)
    {
        var faker = new Faker();
        var prefix = $"ci/{EntityIds.New()}/";
        using var s3 = TigrisCiBucket.Client();
        ManagementApi? api = null;
        var failedStartCleanup = Task.CompletedTask;
        try
        {
            api = await ManagementApi.StartAsync(TigrisCiBucket.Configure,
                services: services => services.AddSingleton<IObjectStore>(provider =>
                    new PrefixedStore(provider.GetRequiredService<S3ObjectStore>(), prefix)),
                cancellationToken: cancellationToken, failedStartCleanup: cleanup => failedStartCleanup = cleanup);
            var database = await api.CreateManagedDatabaseAsync("main");
            using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, database, "people", cancellationToken);
            await ShippedAsync(api, database, null, cancellationToken);
            var written = Enumerable.Range(0, 50).ToDictionary(i => $"p{i:D3}", _ => faker.Lorem.Paragraph(4));
            foreach (var (id, text) in written)
            {
                (await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", id)), new { text }, cancellationToken)).EnsureSuccessStatusCode();
            }
            var writtenAt = DateTimeOffset.UtcNow;
            var shipped = DateTimeOffset.FromUnixTimeSeconds(writtenAt.ToUnixTimeSeconds() + 1);
            while (shipped - DateTimeOffset.UtcNow is { Ticks: > 0 } wait)
            {
                await Task.Delay(wait + TimeSpan.FromMilliseconds(1), cancellationToken);
            }
            foreach (var id in written.Keys)
            {
                (await data.DeleteAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", id)), cancellationToken)).EnsureSuccessStatusCode();
            }

            using var wiped = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", written.Keys.First())), cancellationToken);
            await ShippedAsync(api, database, shipped, cancellationToken);
            using var restore = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/point-in-time/restore"), new { pointInTime = shipped, name = "main-restored" }, cancellationToken);
            var restored = database with { Name = "main-restored" };
            var read = new Dictionary<string, string?>();
            foreach (var id in written.Keys)
            {
                using var response = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(restored, "people", id)), cancellationToken);
                read[id] = response.IsSuccessStatusCode
                    ? (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString()
                    : response.StatusCode.ToString();
            }

            var objects = await ListAsync(s3, $"{prefix}v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local/", cancellationToken);
            var plain = Encoding.UTF8.GetBytes(written.Values.First());
            var sealedOnly = true;
            foreach (var key in objects)
            {
                using var obj = await s3.GetObjectAsync(TigrisCiBucket.Bucket, key, cancellationToken);
                using var buffer = new MemoryStream();
                await obj.ResponseStream.CopyToAsync(buffer, cancellationToken);
                sealedOnly &= buffer.GetBuffer().AsSpan(0, (int)buffer.Length).StartsWith("TBK1"u8) && buffer.ToArray().AsSpan().IndexOf(plain) < 0;
            }

            await Assert.That(wiped.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(restore.StatusCode).IsEqualTo(HttpStatusCode.Created)
                .Because(await restore.Content.ReadAsStringAsync(cancellationToken));
            await Assert.That(read.Count(pair => pair.Value != written[pair.Key])).IsEqualTo(0);
            await Assert.That(objects.Any(key => key.Contains("/snapshots/", StringComparison.Ordinal))).IsTrue();
            await Assert.That(sealedOnly).IsTrue();
        }
        finally
        {
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
                foreach (var key in await ListAsync(s3, prefix, CancellationToken.None))
                {
                    await s3.DeleteObjectAsync(TigrisCiBucket.Bucket, key, CancellationToken.None);
                }
            }
        }
    }

    private static async Task ShippedAsync(ManagementApi api, ManagedDatabaseRef database, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ShippingDeadline);
        while (true)
        {
            using var window = await api.Client.GetAsync(ManagementApi.Uri($"{database.Path}/point-in-time"), deadline.Token);
            if (window.StatusCode != HttpStatusCode.NotFound)
            {
                window.EnsureSuccessStatusCode();
                if (at is null || (await window.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("latest").GetDateTimeOffset() >= at)
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
    }

    internal static async Task<List<string>> ListAsync(AmazonS3Client s3, string prefix, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = TigrisCiBucket.Bucket, Prefix = prefix };
        ListObjectsV2Response page;
        do
        {
            page = await s3.ListObjectsV2Async(request, cancellationToken);
            foreach (var item in page.S3Objects ?? [])
            {
                if (!item.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Tigris listing returned an object outside this test's prefix");
                }

                keys.Add(item.Key);
            }
            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);
        return keys;
    }

    internal sealed class PrefixedStore(IObjectStore inner, string prefix) : IObjectStore
    {
        public Task PutAsync(string key, Stream content, CancellationToken cancellationToken) =>
            inner.PutAsync(prefix + key, content, cancellationToken);

        public Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken) =>
            inner.ReadAsync(prefix + key, read, cancellationToken);

        public async IAsyncEnumerable<StoredObject> ListAsync(string keyPrefix, string? startAfter,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in inner.ListAsync(prefix + keyPrefix, startAfter is null ? null : prefix + startAfter, cancellationToken))
            {
                yield return item with { Key = item.Key.StartsWith(prefix, StringComparison.Ordinal) ? item.Key[prefix.Length..] : string.Empty };
            }
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(prefix + key, cancellationToken);
    }
}
internal static class TigrisCiBucket
{
    public static string Bucket => Variable("BUCKET_NAME") is var name
        && (name == "ts-terrabase-ci" || (name.StartsWith("ts-", StringComparison.Ordinal) && name.EndsWith("-test", StringComparison.Ordinal)))
        ? name : throw new InvalidOperationException("BUCKET_NAME must name the private ts-terrabase-ci bucket or a ts-*-test bucket");
    public static bool R2 => new Uri(Variable("AWS_ENDPOINT_URL_S3")).Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase);

    public static void Configure(Dictionary<string, string> settings)
    {
        settings["backup-endpoint"] = Variable("AWS_ENDPOINT_URL_S3");
        settings["backup-region"] = Variable("AWS_REGION");
        settings["backup-bucket"] = Bucket;
        settings["backup-access-key-id"] = Variable("AWS_ACCESS_KEY_ID");
        settings["backup-secret-key"] = Variable("AWS_SECRET_ACCESS_KEY");
        settings["master-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        if (R2)
        {
            settings["cloudflare-api-token"] = Variable("CLOUDFLARE_API_TOKEN");
        }
    }

    public static AmazonS3Client Client() => new(Variable("AWS_ACCESS_KEY_ID"), Variable("AWS_SECRET_ACCESS_KEY"), new AmazonS3Config
    {
        ServiceURL = Variable("AWS_ENDPOINT_URL_S3"),
        AuthenticationRegion = Variable("AWS_REGION"),
        ForcePathStyle = true,
    });

    private static string Variable(string name) => Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
        ? value : throw new InvalidOperationException($"required Tigris CI credential {name} is missing");
}
