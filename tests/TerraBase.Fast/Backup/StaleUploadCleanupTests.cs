using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class StaleUploadCleanupTests
{
    // A multipart upload whose abort failed holds its parts in the bucket. The shipper of a file aborts the ones under the
    // file's own prefix once they are older than any live upload can be, at startup and then daily, and nothing else.
    [Test]
    public async Task The_shipper_aborts_only_stale_multipart_uploads_under_its_own_file(CancellationToken cancellationToken)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var s3 = new FakeS3 { Time = time, FailAbort = _ => true };
        var settings = new Dictionary<string, string>();
        s3.Configure(settings);
        var services = new ServiceCollection();
        s3.Route(services);
        await using var provider = services.BuildServiceProvider();
        using var node = new S3ObjectStore(new Uri(settings["backup-endpoint"]), settings["backup-region"], FakeS3.Bucket,
            settings["backup-access-key-id"], settings["backup-secret-key"], provider.GetRequiredService<IHttpClientFactory>(),
            S3ObjectStore.HttpClientName, time);
        const string old = "v1/_system/nodes/local/snapshots/old";
        const string fresh = "v1/_system/nodes/local/snapshots/fresh";
        var foreign = $"v1/{Ulid.NewUlid()}/{Ulid.NewUlid()}/{Ulid.NewUlid()}/snapshots/old";
        await LeaveUploadAsync(s3, node, old, cancellationToken);
        await LeaveUploadAsync(s3, node, foreign, cancellationToken);
        time.Advance(TimeSpan.FromHours(2));
        await LeaveUploadAsync(s3, node, fresh, cancellationToken);
        s3.FailAbort = _ => false;
        // The server starts an hour past the old uploads' bound, while the fresh one is an hour inside it.
        time.Advance(S3ObjectStore.StaleUpload - TimeSpan.FromHours(1));

        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        // Discovery ships _system's first snapshot; the next pass runs its first retention.
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(cancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(cancellationToken);
        var afterStart = s3.PendingUploadKeys;
        var abortedAtStart = s3.AbortedUploads;
        // A day later the fresh upload is past the bound too; another file's upload is never this shipper's.
        time.Advance(TimeSpan.FromDays(1));
        await backup.RunOnceAsync(cancellationToken);

        await Assert.That(afterStart).IsEquivalentTo([fresh, foreign]);
        await Assert.That(abortedAtStart).IsEqualTo(1);
        await Assert.That(s3.PendingUploadKeys).IsEquivalentTo([foreign]);
        await Assert.That(s3.AbortedUploads).IsEqualTo(2);
        await Assert.That(s3.CompletedUploads).IsEqualTo(0);
    }

    // A multipart upload (one byte more than a part, from a sparse file) cancelled at its first part, whose abort fails.
    private static async Task LeaveUploadAsync(FakeS3 s3, S3ObjectStore store, string key, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Ulid.NewUlid()}.sealed");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        file.SetLength((128L << 20) + 1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        s3.BeforePart = async (_, _, request) =>
        {
            await stop.CancelAsync();
            await Task.Delay(Timeout.Infinite, request);
        };
        var pending = s3.PendingUploads;
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.PutAsync(key, file, stop.Token));
        await Assert.That(s3.PendingUploads).IsEqualTo(pending + 1);
    }
}
