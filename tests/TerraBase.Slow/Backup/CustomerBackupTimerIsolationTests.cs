using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CustomerBackupTimerIsolationTests
{
    [Test]
    public async Task The_hosted_timer_ships_a_platform_siblings_snapshot_and_WAL_while_both_customer_slots_are_stalled()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        using var timer = new TimerTime(time);
        TrackedBackup? taps = null;
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            services.AddSingleton<IWalBackup>(provider => taps = new TrackedBackup(provider.GetRequiredService<TursoStorageEngine>()));
            // Keep the real hosted service; only its timer gets individually acknowledged fake-clock ticks.
            services.AddSingleton<ContinuousBackup>(provider => ActivatorUtilities.CreateInstance<ContinuousBackup>(provider, timer));
        }, time);
        var backup = api.Server.Services.GetRequiredService<ContinuousBackup>();
        var customers = new List<ManagedDatabaseRef>();
        var clients = new List<HttpClient>();
        // Each customer file's first snapshot PUT stalls inside its shipping job, holding that organization's customer slot.
        var bodies = new Dictionary<string, StalledPut>(StringComparer.Ordinal);
        var snapshotEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var walEntered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? stopping = null;
        try
        {
            await taps!.StartupEntered.Task.WaitAsync(token);
            var faker = new Faker();
            var before = faker.Lorem.Sentence(12);
            var during = faker.Lorem.Sentence(12);
            for (var index = 0; index < 2; index++)
            {
                var database = await CreateDatabaseAsync(api, organization: null, token);
                customers.Add(database);
                var data = api.DataClient(await api.Server.IssueAsync(database.Organization, ["documents:read", "documents:write"], token));
                clients.Add(data);
                using var destination = await api.Client.PutAsJsonAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), new
                {
                    endpoint = FakeS3.CustomerEndpoint,
                    region = "test-region-1",
                    bucket = FakeS3.CustomerBucket,
                    accessKeyId = FakeS3.CustomerAccessKey,
                    secretKey = FakeS3.CustomerSecretKey,
                    mode = "customer",
                }, token);
                destination.EnsureSuccessStatusCode();
                await AssertPublicAsync(await destination.Content.ReadAsStringAsync(token));
                await SeedAsync(data, database, before, token);
                bodies.Add(File(database) + "/snapshots/", new StalledPut());
            }

            await Assert.That(customers[0].Organization).IsNotEqualTo(customers[1].Organization);
            s3.BeforeCustomerWrite = async (key, cancellationToken) =>
            {
                var body = bodies.FirstOrDefault(pair => key.StartsWith(pair.Key, StringComparison.Ordinal)).Value;
                if (body is not null && body.Requested() == 1)
                {
                    await body.WaitAsync(cancellationToken);
                }
            };
            taps.ReleaseStartup.TrySetResult();
            await Task.WhenAll(bodies.Values.Select(body => body.Entered.Task)).WaitAsync(token);
            var stalledAt = time.GetUtcNow();
            var stalledTimestamp = time.GetTimestamp();

            // A fresh platform project in an already busy organization must be discovered by ExecuteAsync's timer.
            var healthy = await CreateDatabaseAsync(api, customers[0].Organization, token);
            var healthyData = clients[0];
            await SeedAsync(healthyData, healthy, before, token);
            s3.BeforeGet = async (key, cancellationToken) =>
            {
                if (key.StartsWith(File(healthy) + "/snapshots/", StringComparison.Ordinal))
                {
                    snapshotEntered.TrySetResult();
                    await releaseSnapshot.Task.WaitAsync(cancellationToken);
                }
            };
            s3.BeforePut = async (key, cancellationToken) =>
            {
                if (key.StartsWith(File(healthy) + "/wal/", StringComparison.Ordinal))
                {
                    walEntered.TrySetResult(key);
                    await releaseWal.Task.WaitAsync(cancellationToken);
                }
            };

            await timer.TickAsync(TimeSpan.FromSeconds(10), token);
            await snapshotEntered.Task.WaitAsync(token);
            var snapshotJob = await CaptureJobAsync(backup, File(healthy), token);
            releaseSnapshot.TrySetResult();
            await snapshotJob.WaitAsync(token);
            await Assert.That(s3.Objects.Keys.Count(key => key.StartsWith(File(healthy) + "/snapshots/", StringComparison.Ordinal))).IsEqualTo(1);
            // The restore drill runs beside shipping; wait for its result.
            BackupFileStatus snapshot;
            while ((snapshot = await StatusAsync(api, healthy, token)).LastDrill is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), token);
            }

            await Assert.That(snapshot.LastDrill!.Passed).IsTrue();

            await PutAsync(healthyData, healthy, "during", during, token);
            await timer.TickAsync(TimeSpan.FromSeconds(1), token);
            var walKey = await walEntered.Task.WaitAsync(token);
            releaseWal.TrySetResult();
            BackupFileStatus shipped;
            do
            {
                shipped = await StatusAsync(api, healthy, token);
                await Assert.That(shipped.LastError).IsNull();
            }
            while (shipped.LastShippedAt is null || shipped.LastShippedAt <= snapshot.LastShippedAt);

            await Assert.That(s3.Objects.ContainsKey(walKey)).IsTrue();
            await Assert.That(shipped.Generation).IsEqualTo(snapshot.Generation);
            await RestoreAsync(api, healthyData, healthy, snapshot.LastShippedAt!.Value, before, during, token);

            await Assert.That(timer.Ticks).IsEqualTo(2);
            await Assert.That(time.GetUtcNow() - stalledAt).IsLessThan(TimeSpan.FromMinutes(15));
            await Assert.That(time.GetElapsedTime(stalledTimestamp)).IsLessThan(TimeSpan.FromMinutes(15));
            await Assert.That(backup.ExecuteTask!.IsCompleted).IsFalse();
            foreach (var customer in customers)
            {
                var tap = taps.Taps[File(customer)];
                var body = bodies[File(customer) + "/snapshots/"];
                // The stalled job read its backlog once before its snapshot; a second job on the busy file would read again.
                await Assert.That(tap.Snapshots).IsEqualTo(1);
                await Assert.That(tap.Reads).IsEqualTo(1);
                await Assert.That(tap.Returned).IsFalse();
                await Assert.That(body.Requests).IsEqualTo(1);
                await Assert.That(body.Cancelled.Task.IsCompleted).IsFalse();
                await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal))).IsFalse();
            }

            // Leave the current uploads blocked; shutdown must cancel and join them before returning their taps.
            stopping = backup.StopAsync(token);
            await Task.WhenAll(bodies.Values.Select(body => body.Cancelled.Task)).WaitAsync(token);
            await stopping.WaitAsync(token);
            await Assert.That(backup.ExecuteTask!.IsCompleted).IsTrue();
            await Assert.That(taps.Taps.Values.All(tap => tap.Returned)).IsTrue();
            var stopped = await api.Server.Client.GetFromJsonAsync(ManagementApi.Uri("/v1/backup"), TerraBaseJsonContext.Default.BackupStatus, token);
            await Assert.That(stopped!.Files).IsEmpty();
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            s3.BeforeCustomerWrite = null;
            s3.BeforeGet = null;
            s3.BeforePut = null;
            taps!.ReleaseStartup.TrySetResult();
            releaseSnapshot.TrySetResult();
            releaseWal.TrySetResult();
            foreach (var body in bodies.Values)
            {
                body.Release();
            }

            // The test token may already have expired. Release every fixture gate, then join before host disposal.
            stopping ??= backup.StopAsync(CancellationToken.None);
            await stopping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    private static async Task<Task> CaptureJobAsync(ContinuousBackup backup, string prefix, CancellationToken token)
    {
        // LastDrill is published before RecordAsync/finally; the next tick needs this exact snapshot job to finish.
        var gate = (SemaphoreSlim)typeof(ContinuousBackup).GetField("_pass", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backup)!;
        await gate.WaitAsync(token);
        try
        {
            var files = (IDictionary)typeof(ContinuousBackup).GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backup)!;
            var file = files[prefix]!;
            var job = file.GetType().GetProperty("Job")!.GetValue(file)!;
            return (Task)job.GetType().GetProperty("Completion")!.GetValue(job)!;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<BackupFileStatus> StatusAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken token)
    {
        var status = await api.Server.Client.GetFromJsonAsync(ManagementApi.Uri("/v1/backup"), TerraBaseJsonContext.Default.BackupStatus, token);
        return status!.Files.Single(file => file.File == File(database));
    }

    private static async Task<ManagedDatabaseRef> CreateDatabaseAsync(ManagementApi api, string? organization, CancellationToken token)
    {
        organization ??= await CreateAsync(api.Client, "/v1/management/organizations", "Timer organization " + Guid.NewGuid().ToString("N"), token);
        var project = await CreateAsync(api.Client, $"/v1/management/organizations/{organization}/projects", "Timer project " + Guid.NewGuid().ToString("N"), token);
        var database = await CreateAsync(api.Client, $"/v1/management/organizations/{organization}/projects/{project}/databases", "main", token);
        return new(organization, project, database, "main");
    }

    private static async Task<string> CreateAsync(HttpClient client, string path, string name, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(path), new { name }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("id").GetString()!;
    }

    private static async Task SeedAsync(HttpClient data, ManagedDatabaseRef database, string text, CancellationToken token)
    {
        using var response = await data.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/collections"), new { name = "people" }, token);
        response.EnsureSuccessStatusCode();
        await PutAsync(data, database, "before", text, token);
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string name, string text, CancellationToken token)
    {
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", name)), new { text }, token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task RestoreAsync(ManagementApi api, HttpClient data, ManagedDatabaseRef database, DateTimeOffset snapshotAt,
        string before, string during, CancellationToken token)
    {
        using var window = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time?destination=platform"), token);
        window.EnsureSuccessStatusCode();
        var bounds = await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
        var latest = bounds.GetProperty("latest").GetDateTimeOffset();
        await Assert.That(latest).IsGreaterThan(snapshotAt);
        using var response = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = latest, destination = "platform", name = "timer-restored" }, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var json = await response.Content.ReadAsStringAsync(token);
        await AssertPublicAsync(json);
        using var body = JsonDocument.Parse(json);
        await Assert.That(body.RootElement.GetProperty("branchedFrom").GetString()).IsEqualTo(database.Id);
        var restored = new ManagedDatabaseRef(database.Organization, database.Project, body.RootElement.GetProperty("id").GetString()!, "timer-restored");
        foreach (var (name, text) in new[] { ("before", before), ("during", during) })
        {
            using var read = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(restored, "people", name)), token);
            read.EnsureSuccessStatusCode();
            var stored = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            await Assert.That(stored.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
        }
    }

    private static async Task AssertPublicAsync(string json)
    {
        await Assert.That(json.Contains(FakeS3.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains(FakeS3.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("wrappedKey", StringComparison.Ordinal)).IsFalse();
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private sealed class TimerTime(FakeTimeProvider time) : TimeProvider, IDisposable
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _fired;
        private ITimer? _timer;
        private int _ticks;

        public int Ticks => Volatile.Read(ref _ticks);
        public override long TimestampFrequency => time.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
        public override long GetTimestamp() => time.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period == Timeout.InfiniteTimeSpan)
            {
                return time.CreateTimer(callback, state, dueTime, period);
            }

            if (_timer is not null || dueTime != TimeSpan.FromSeconds(1) || period != TimeSpan.FromSeconds(1))
            {
                throw new InvalidOperationException("expected the continuous backup service's one-second timer");
            }

            _timer = time.CreateTimer(value =>
            {
                callback(value);
                Interlocked.Increment(ref _ticks);
                _fired?.TrySetResult();
            }, state, dueTime, period);
            _ready.TrySetResult();
            return _timer;
        }

        public async Task TickAsync(TimeSpan amount, CancellationToken token)
        {
            await _ready.Task.WaitAsync(token);
            _fired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Move the discovery wall clock without queuing ten ticks; keep the real one-second timer period.
            time.AdjustTime(time.GetUtcNow() + amount - TimeSpan.FromSeconds(1));
            time.Advance(TimeSpan.FromSeconds(1));
            await _fired.Task.WaitAsync(token);
        }

        public void Dispose() => _timer?.Dispose();
    }

    private sealed class TrackedBackup(IWalBackup engine) : IWalBackup
    {
        public TaskCompletionSource StartupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStartup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string, TrackedTap> Taps { get; } = new(StringComparer.Ordinal);

        public async ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
        {
            if (database is null)
            {
                StartupEntered.TrySetResult();
                await ReleaseStartup.Task.WaitAsync(cancellationToken);
            }

            if (await engine.OpenTapAsync(database, cancellationToken) is not { } tap)
            {
                return null;
            }

            var tracked = new TrackedTap(tap);
            Taps[BackupLayout.File(database, "local")] = tracked;
            return tracked;
        }

        public ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken) =>
            engine.RollForwardAsync(file, segments, cancellationToken);

        public LogCut CutLog(Stream segment, DateTimeOffset at, long? previous) => engine.CutLog(segment, at, previous);

        public DateTimeOffset? ReadMark(Stream segment) => engine.ReadMark(segment);

        public ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken) =>
            engine.ImportDatabaseAsync(database, file, cancellationToken);

        public ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken) => engine.ChecksumAsync(file, cancellationToken);
    }

    private sealed class TrackedTap(IWalTap tap) : IWalTap
    {
        private int _reads;
        private int _snapshots;
        private volatile bool _returned;

        public int Reads => Volatile.Read(ref _reads);
        public int Snapshots => Volatile.Read(ref _snapshots);
        public bool Returned => _returned;

        public ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return tap.ReadAsync(destination, cancellationToken);
        }

        public ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _snapshots);
            return tap.SnapshotAsync(destination, snapshotPath, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await tap.DisposeAsync();
            _returned = true;
        }
    }

    private sealed class StalledPut
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests => Volatile.Read(ref _requests);

        public int Requested() => Interlocked.Increment(ref _requests);

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public void Release() => _release.TrySetResult();
    }
}
