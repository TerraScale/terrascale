using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class CustomerBackupUnicodeTests
{
    [Test]
    public async Task Utf8_ordered_BMP_and_supplementary_junk_cross_customer_pages_without_breaking_drills_retention_or_restore()
    {
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var saved = await api.Client.PutAsJsonAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), new
        {
            endpoint = FakeS3.CustomerEndpoint,
            region = "test-region-1",
            bucket = FakeS3.CustomerBucket,
            accessKeyId = FakeS3.CustomerAccessKey,
            secretKey = FakeS3.CustomerSecretKey,
            mode = "customer",
        }, cancellationToken);
        saved.EnsureSuccessStatusCode();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence(12);
        using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "kept")), new { text }, cancellationToken);
        written.EnsureSuccessStatusCode();
        var file = $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";
        var prefix = file + "/snapshots/";
        var bmp = prefix + "\uE000-junk-";
        var supplementary = prefix + "\U00010000-junk-";
        // The real ASCII snapshot plus 249 BMP keys fill page one; supplementary keys start the next page.
        foreach (var (stem, count) in new[] { (bmp, 249), (supplementary, 251) })
        {
            for (var index = 0; index < count; index++)
            {
                s3.SeedObject(customer: true, stem + index.ToString("D3", CultureInfo.InvariantCulture), []);
            }
        }

        var junkOperations = 0;
        s3.ObjectRequested = (customer, method, key) =>
        {
            if (customer && (method == HttpMethod.Get || method == HttpMethod.Delete)
                && (key.StartsWith(bmp, StringComparison.Ordinal) || key.StartsWith(supplementary, StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref junkOperations);
            }
        };
        s3.ObservedListPrefix = prefix;
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(cancellationToken);
        using var status = await api.Server.Client.GetAsync(ManagementApi.Uri("/v1/backup"), cancellationToken);
        status.EnsureSuccessStatusCode();
        var backedUp = (await status.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("files")
            .EnumerateArray().Single(item => item.GetProperty("file").GetString() == file);
        await Assert.That(backedUp.GetProperty("lastError").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(backedUp.GetProperty("lastDrill").GetProperty("passed").GetBoolean()).IsTrue();
        await Assert.That(backedUp.GetProperty("sizeBytes").GetInt64()).IsGreaterThan(0);

        var listed = s3.ListRequests;
        using var window = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
        window.EnsureSuccessStatusCode();
        var latest = (await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)).GetProperty("latest").GetDateTimeOffset();
        await Assert.That(s3.ListRequests - listed).IsEqualTo(3);
        listed = s3.ListRequests;
        using var restoredResponse = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = latest, name = "unicode-restored", destination = "customer" }, cancellationToken);
        await Assert.That(restoredResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(s3.ListRequests - listed).IsEqualTo(3);
        var restored = await restoredResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var restoredDatabase = new ManagedDatabaseRef(database.Organization, database.Project,
            restored.GetProperty("id").GetString()!, restored.GetProperty("name").GetString()!);
        using var document = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(restoredDatabase, "people", "kept")), cancellationToken);
        await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await document.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken))
            .GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
        await Assert.That(junkOperations).IsEqualTo(0);
        await Assert.That(s3.CustomerObjects.Keys.Count(key => key.StartsWith(bmp, StringComparison.Ordinal)
            || key.StartsWith(supplementary, StringComparison.Ordinal))).IsEqualTo(500);

        // UTF-16 ordering is decreasing on the S3 wire and must still be rejected.
        s3.CustomerListingOverride = (requested, keys) => requested == prefix ? [supplementary + "000", bmp + "000"] : keys;
        using var decreasing = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time?destination=customer"), cancellationToken);
        await Assert.That(decreasing.IsSuccessStatusCode).IsFalse();
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }
}
