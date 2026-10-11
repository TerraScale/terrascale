using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

/// <summary>
/// Continuous backup and point-in-time restore against the real Tigris dev bucket (<c>make terrabase-tigris-e2e</c>).
/// Runs only when the bucket variables are set: <c>set -a; . ~/.config/terrascale/tigris-dev.env; set +a</c>.
/// </summary>
public sealed class TigrisBackupTests
{
    private static readonly TimeSpan ShippingDeadline = TimeSpan.FromMinutes(2);

    [Test]
    [TigrisDevBucket]
    public async Task A_database_shipped_to_Tigris_restores_identically_after_its_documents_are_wiped(CancellationToken cancellationToken)
    {
        var faker = new Faker();
        var started = DateTimeOffset.UtcNow;
        var api = await ManagementApi.StartAsync(TigrisDevBucketAttribute.Configure, cancellationToken: cancellationToken);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        using var s3 = TigrisDevBucketAttribute.Client();
        ManagedDatabaseRef? database = null;
        try
        {
            database = await api.CreateManagedDatabaseAsync("main");
            using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, database, "people", cancellationToken);
            var written = Enumerable.Range(0, 50).ToDictionary(i => $"p{i:D3}", _ => faker.Lorem.Paragraph(4));
            foreach (var (id, text) in written)
            {
                (await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", id)), new { text }, cancellationToken)).EnsureSuccessStatusCode();
            }

            var shipped = await ShippedAsync(backup, database, DateTimeOffset.UtcNow, cancellationToken);
            foreach (var id in written.Keys)
            {
                (await data.DeleteAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", id)), cancellationToken)).EnsureSuccessStatusCode();
            }

            using var wiped = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", written.Keys.First())), cancellationToken);
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

            var objects = await ListAsync(s3, $"v1/{database.Organization}/{database.Project}/{database.Id}/", cancellationToken);
            var plain = Encoding.UTF8.GetBytes(written.Values.First());
            var sealedOnly = true;
            foreach (var key in objects)
            {
                using var obj = await s3.GetObjectAsync(TigrisDevBucketAttribute.Bucket, key, cancellationToken);
                using var buffer = new MemoryStream();
                await obj.ResponseStream.CopyToAsync(buffer, cancellationToken);
                sealedOnly &= buffer.GetBuffer().AsSpan(0, (int)buffer.Length).StartsWith("TBK1"u8) && buffer.ToArray().AsSpan().IndexOf(plain) < 0;
            }

            await Assert.That(wiped.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(restore.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(read.Count(pair => pair.Value != written[pair.Key])).IsEqualTo(0);
            await Assert.That(objects.Any(key => key.Contains("/snapshots/", StringComparison.Ordinal))).IsTrue();
            await Assert.That(sealedOnly).IsTrue();
        }
        finally
        {
            // Stop the node first: its shipper keeps uploading until shutdown.
            var generation = backup.Status.Files.SingleOrDefault(status => status.File == "v1/_system")?.Generation;
            await api.DisposeAsync();
            if (database is not null)
            {
                await CleanUpAsync(s3, database, generation, started);
            }
        }
    }

    // Waits for the background shipper (no manual pass) to have read everything written before `after`.
    private static async Task<DateTimeOffset> ShippedAsync(IContinuousBackup backup, ManagedDatabaseRef database, DateTimeOffset after, CancellationToken cancellationToken)
    {
        var file = $"v1/{database.Organization}/{database.Project}/{database.Id}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ShippingDeadline);
        while (true)
        {
            if (backup.Status.Files.SingleOrDefault(status => status.File == file) is { LastShippedAt: { } at, PendingBytes: 0, LastError: null } && at >= after)
            {
                return at;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
    }

    private static async Task<List<string>> ListAsync(AmazonS3Client s3, string prefix, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = TigrisDevBucketAttribute.Bucket, Prefix = prefix };
        ListObjectsV2Response page;
        do
        {
            page = await s3.ListObjectsV2Async(request, cancellationToken);
            keys.AddRange((page.S3Objects ?? []).Select(item => item.Key));
            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);
        return keys;
    }

    // The run's own objects only: its organization, its node's system generation, and system keys written during it.
    private static async Task CleanUpAsync(AmazonS3Client s3, ManagedDatabaseRef database, string? generation, DateTimeOffset started)
    {
        var keys = await ListAsync(s3, $"v1/{database.Organization}/", CancellationToken.None);
        keys.AddRange(await ListAsync(s3, $"v1/keys/{database.Organization}/", CancellationToken.None));
        if (generation is not null)
        {
            keys.AddRange((await ListAsync(s3, "v1/_system/snapshots/", CancellationToken.None)).Where(key => key.Contains($"-{generation}-", StringComparison.Ordinal)));
            keys.AddRange(await ListAsync(s3, $"v1/_system/wal/{generation}/", CancellationToken.None));
        }

        var request = new ListObjectsV2Request { BucketName = TigrisDevBucketAttribute.Bucket, Prefix = "v1/keys/_system/" };
        var systemKeys = await s3.ListObjectsV2Async(request);
        keys.AddRange((systemKeys.S3Objects ?? []).Where(item => item.LastModified >= started.UtcDateTime.AddMinutes(-1)).Select(item => item.Key));
        foreach (var key in keys)
        {
            await s3.DeleteObjectAsync(TigrisDevBucketAttribute.Bucket, key);
        }
    }
}

/// <summary>Skips the test unless the Tigris dev bucket variables are set.</summary>
internal sealed class TigrisDevBucketAttribute() : SkipAttribute("set the Tigris dev bucket variables (~/.config/terrascale/tigris-dev.env)")
{
    private static readonly string[] Variables = ["AWS_ENDPOINT_URL_S3", "AWS_REGION", "BUCKET_NAME", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY"];

    public static string Bucket => Variable("BUCKET_NAME");

    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(Variables.Any(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))));

    public static void Configure(Dictionary<string, string> settings)
    {
        settings["backup-endpoint"] = Variable("AWS_ENDPOINT_URL_S3");
        settings["backup-region"] = Variable("AWS_REGION");
        settings["backup-bucket"] = Bucket;
        settings["backup-access-key-id"] = Variable("AWS_ACCESS_KEY_ID");
        settings["backup-secret-key"] = Variable("AWS_SECRET_ACCESS_KEY");
        settings["master-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public static AmazonS3Client Client() => new(Variable("AWS_ACCESS_KEY_ID"), Variable("AWS_SECRET_ACCESS_KEY"), new AmazonS3Config
    {
        ServiceURL = Variable("AWS_ENDPOINT_URL_S3"),
        AuthenticationRegion = Variable("AWS_REGION"),
        ForcePathStyle = true,
    });

    private static string Variable(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;
}
