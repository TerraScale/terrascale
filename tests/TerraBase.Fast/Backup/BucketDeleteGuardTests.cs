using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BucketDeleteGuardTests
{
    // P0 2026-11-03: every Tigris bucket of both Fly organizations was deleted at once. TerraBase's only DeleteBucket
    // call is snapshot expiry, which must always carry X-Tigris-Snapshot-Version: without the header the same request
    // deletes the bucket itself. Any version that is not a number is refused before a request leaves the process, and
    // the refusal is typed so no caller can mistake it for a transport failure and retry it.
    [Test]
    public async Task The_shipper_never_sends_a_bucket_delete_for_a_non_numeric_snapshot_version(CancellationToken cancellationToken)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 1, 0, 0, TimeSpan.Zero));
        var s3 = new FakeS3 { Time = time };
        s3.SeedSnapshot(time.GetUtcNow().AddDays(-20));
        var settings = new Dictionary<string, string>();
        s3.Configure(settings);
        var services = new ServiceCollection();
        s3.Route(services);
        await using var provider = services.BuildServiceProvider();
        using var node = new S3ObjectStore(new Uri(settings["backup-endpoint"]), settings["backup-region"], FakeS3.Bucket,
            settings["backup-access-key-id"], settings["backup-secret-key"], provider.GetRequiredService<IHttpClientFactory>(),
            S3ObjectStore.HttpClientName, time);

        foreach (var version in new[] { "", " 1791000000000000000", "1791000000000000000;", "../../backups", "none" })
        {
            await Assert.That(() => node.DeleteSnapshotAsync(version, cancellationToken)).Throws<BucketDeleteForbiddenException>();
        }

        await Assert.That(s3.BucketDeletes).IsEqualTo(0);
        await Assert.That(s3.Snapshots).HasSingleItem();
    }
}
