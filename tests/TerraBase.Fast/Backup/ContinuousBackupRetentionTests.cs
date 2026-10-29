using System.Globalization;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class ContinuousBackupRetentionTests
{
    [Test]
    public async Task Retention_keeps_each_plans_window_and_the_daily_drill_rebuilds_the_database_from_the_bucket()
    {
        var faker = new Faker();
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var hobby = await api.CreateManagedDatabaseAsync("main");
        var pro = await api.CreateManagedDatabaseAsync("main");
        await api.SetPlanAsync(api.User, pro.Organization, "pro");
        using var data = await api.CreateClientAsync(hobby.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, hobby, "people");
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(hobby, "people", "first-day")), new { text = faker.Lorem.Sentence(8) })).EnsureSuccessStatusCode();

        // Two daily snapshots, each checked by a drill, then an hour on: hobby keeps a day plus the snapshot before it.
        for (var day = 0; day < 2; day++)
        {
            time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
            await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        }

        time.Advance(TimeSpan.FromHours(2));
        await backup.RunOnceAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var hobbySnapshots = Snapshots(s3, hobby);
        var proSnapshots = Snapshots(s3, pro);
        var (oldestAt, oldestPosition) = hobbySnapshots[0];
        var segmentsBefore = s3.Objects.Keys.Count(key => key.StartsWith(File(hobby) + "/wal/", StringComparison.Ordinal)
            && long.Parse(key.Split('/')[^1].Split('-')[0], CultureInfo.InvariantCulture) < oldestPosition);
        using var window = JsonDocument.Parse(await api.Client.GetStringAsync(Uri($"{hobby.Path}/point-in-time")));
        var hobbyRecord = await RecordAsync(api, hobby);
        var proRecord = await RecordAsync(api, pro);
        var status = backup.Status.Files.Single(file => file.File == File(hobby));

        await Assert.That(hobbySnapshots.Count).IsEqualTo(2);
        await Assert.That(proSnapshots.Count).IsEqualTo(3);
        await Assert.That(segmentsBefore).IsEqualTo(0);
        // Retention keeps the snapshot before the window; the window itself is the plan's day.
        await Assert.That(oldestAt).IsLessThan(time.GetUtcNow() - TimeSpan.FromDays(1));
        await Assert.That(window.RootElement.GetProperty("earliest").GetDateTimeOffset()).IsEqualTo(time.GetUtcNow() - TimeSpan.FromDays(1));
        await Assert.That(hobbyRecord.GetProperty("retentionDays").GetInt32()).IsEqualTo(1);
        await Assert.That(proRecord.GetProperty("retentionDays").GetInt32()).IsEqualTo(7);
        await Assert.That(hobbyRecord.GetProperty("lastDrill").GetProperty("passed").GetBoolean()).IsTrue();
        await Assert.That(hobbyRecord.GetProperty("sizeBytes").GetInt64()).IsGreaterThan(0);
        await Assert.That(status.LastDrill!.Passed).IsTrue();
    }

    [Test]
    public async Task Expiry_cancels_and_joins_an_active_drill_before_deleting_its_inputs_while_other_files_ship()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services => ManualShipping(s3, services), time);
        var backup = (ContinuousBackup)api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var reading = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        s3.BeforeGet = async (key, cancellationToken) =>
        {
            if (key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref held, 1, 0) == 0)
            {
                reading.TrySetResult(key);
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    // Even after cancellation, deletion must join the reader before removing its input.
                    await release.Task.WaitAsync(token);
                    throw;
                }
            }
        };
        backup.RetentionWaiting = file =>
        {
            if (file == File(database))
            {
                expiring.TrySetResult();
            }
        };
        time.Advance(TimeSpan.FromSeconds(10));
        var first = backup.RunOnceAsync(token);
        Task? second = null;
        Task? third = null;
        string baseline;
        try
        {
            baseline = await reading.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await backup.WaitForShippingAsync(File(database), token);
            (await data.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "during")), new { text = "after snapshot" }, token)).EnsureSuccessStatusCode();
            time.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(2));
            second = backup.RunOnceAsync(token);
            // Establish expiry ownership before adding the independent upload. Manual passes also join drills;
            // admitting a fleet of new files first can fill their joins behind the deliberately held drill.
            await Task.WhenAll(expiring.Task, cancelled.Task).WaitAsync(TimeSpan.FromSeconds(30), token);
            var other = await api.CreateManagedDatabaseAsync("other");
            s3.BeforePut = (key, _) =>
            {
                if (key.StartsWith(File(other) + "/snapshots/", StringComparison.Ordinal))
                {
                    uploading.TrySetResult();
                }
                return Task.CompletedTask;
            };
            time.Advance(TimeSpan.FromSeconds(10));
            third = backup.RunOnceAsync(token);
            try
            {
                await Task.WhenAll(expiring.Task, cancelled.Task, uploading.Task).WaitAsync(TimeSpan.FromSeconds(30), token);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"expiry={expiring.Task.Status}; cancellation={cancelled.Task.Status}; upload={uploading.Task.Status}; backup={JsonSerializer.Serialize(backup.Status)}", error);
            }
            await Assert.That(s3.Objects.ContainsKey(baseline)).IsTrue();
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second ?? Task.CompletedTask, third ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(60), token);
        }
        await Assert.That(s3.Objects.ContainsKey(baseline)).IsFalse();
        var record = await RecordAsync(api, database);
        await Assert.That(record.GetProperty("lastDrill").GetProperty("passed").GetBoolean()).IsTrue();
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    // Snapshot keys are <ms>-<generation>-<position>, oldest first.
    private static List<(DateTimeOffset At, long Position)> Snapshots(FakeS3 s3, ManagedDatabaseRef database) =>
        [.. s3.Objects.Keys.Where(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(key => key.Split('/')[^1].Split('-'))
            .Select(parts => (DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[0], CultureInfo.InvariantCulture)), long.Parse(parts[2], CultureInfo.InvariantCulture)))];

    // The backup record the shipper keeps in the system catalog, under the database's state.
    private static async Task<JsonElement> RecordAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await using (transaction)
        {
            var document = await transaction.GetAsync($"databases/{database.Id}/backup_status", "current", CancellationToken.None);
            return JsonDocument.Parse(document!.Json).RootElement.Clone();
        }
    }

    private static void ManualShipping(FakeS3 s3, IServiceCollection services)
    {
        s3.Route(services);
        ManualBackupHosting.Configure(services);
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
