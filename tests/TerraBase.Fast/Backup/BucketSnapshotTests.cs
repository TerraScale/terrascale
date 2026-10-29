using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BucketSnapshotTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    // The snapshot check reads a response header of its HeadBucket. The S3 client reads its response event twice per
    // response (a null check, then the call): subscribing a reader per request made a request completing at the same
    // time call a handler that was just removed (NullReferenceException in ProcessResponseHandlers), failing that pass.
    [Test]
    public async Task The_system_shipper_keeps_one_bucket_snapshot_a_day_for_fourteen_days()
    {
        var time = new FakeTimeProvider(Start);
        var s3 = new FakeS3 { Time = time };
        s3.SeedSnapshot(Start.AddDays(-20));
        s3.SeedSnapshot(Start.AddDays(-15));
        s3.SeedSnapshot(Start.AddDays(-13));
        await using var api = await StartAsync(s3, time);
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;

        // _system's retention runs an hour after shipping starts: it takes the day's snapshot and drops the two older
        // than fourteen days.
        var taken = await PassAsync(api, time, TimeSpan.FromHours(1), token);
        var afterFirst = s3.Snapshots;
        // The next hourly pass finds a snapshot younger than a day.
        await PassAsync(api, time, TimeSpan.FromHours(1), token);
        var sameDay = s3.Snapshots;
        // A day later it takes another, and the one seeded at -13 days has aged past fourteen.
        var nextDay = await PassAsync(api, time, TimeSpan.FromDays(1), token);
        var metrics = await api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token);

        await Assert.That(afterFirst.Select(snapshot => snapshot.At)).IsEquivalentTo([Start.AddDays(-13), taken]);
        await Assert.That(sameDay).IsEquivalentTo(afterFirst);
        await Assert.That(s3.Snapshots.Select(snapshot => snapshot.At)).IsEquivalentTo([taken, nextDay]);
        await Assert.That(s3.BucketDeletes).IsEqualTo(0);
        await Assert.That(metrics).Contains("terrabase_backup_bucket_snapshots_total{otel_scope_name=\"TerraScale.TerraBase\",result=\"taken\"} 2");
        await Assert.That(metrics).Contains("terrabase_backup_bucket_snapshots_total{otel_scope_name=\"TerraScale.TerraBase\",result=\"deleted\"} 3");
        await Assert.That(metrics).Contains($"terrabase_backup_bucket_snapshot_seconds{{otel_scope_name=\"TerraScale.TerraBase\"}} {nextDay.ToUnixTimeSeconds()}");
    }

    private static async Task<ManagementApi> StartAsync(FakeS3 s3, FakeTimeProvider time)
    {
        var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        // Discovery runs every ten seconds; this pass ships _system's first snapshot.
        time.Advance(TimeSpan.FromSeconds(10));
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        return api;
    }

    private static async Task<DateTimeOffset> PassAsync(ManagementApi api, FakeTimeProvider time, TimeSpan advance, CancellationToken token)
    {
        time.Advance(advance);
        await api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(token);
        return time.GetUtcNow();
    }
}
