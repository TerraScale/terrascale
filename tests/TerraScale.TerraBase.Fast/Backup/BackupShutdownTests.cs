using System.Collections.Concurrent;
using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupShutdownTests
{
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Cancelling_the_final_upload_returns_every_tap_and_allows_the_data_directory_to_reopen()
    {
        using var oci = new FakeOci();
        using var shutdown = new CancellationTokenSource();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var directory = Directory.CreateTempSubdirectory("terrabase-backup-shutdown-").FullName;
        var blocked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        ManagementApi? api = null;
        TrackedBackup? taps = null;
        ContinuousBackup? backup = null;
        Task? stop = null;
        try
        {
            api = await ManagementApi.StartAsync(settings =>
            {
                oci.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                oci.Route(services);
                services.AddSingleton<IWalBackup>(provider => taps = new TrackedBackup(provider.GetRequiredService<SqliteStorageEngine>()));
                // Factory disposal rejoins StopAsync. The test observes its failure directly; the host must still dispose.
                var registration = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationFactory?.Method.ReturnType == typeof(ContinuousBackup));
                services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                    new BackupLifetime(provider.GetRequiredService<ContinuousBackup>()));
            }, time);
            backup = api.Server.Services.GetRequiredService<ContinuousBackup>();
            var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
            var database = await api.CreateManagedDatabaseAsync("main");
            var other = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "other");
            using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, database, "people");
            await ManagementApi.CreateCollectionAsync(data, other, "people");
            time.Advance(TimeSpan.FromSeconds(10));
            await backup.RunOnceAsync(deadline.Token);
            await Assert.That(backup.Status.Files.Select(file => file.File)).IsEquivalentTo(
                ["v1/_system", File(database), File(other)]);
            await Assert.That(backup.Status.Files.All(file => file.Generation is not null)).IsTrue();

            oci.BeforePut = async (key, token) =>
            {
                if (key.Contains("/wal/", StringComparison.Ordinal) || key.Contains("/snapshots/", StringComparison.Ordinal))
                {
                    // A queued timer pass may upload first: cancellation releases it, leaving the final pass to retry.
                    if (backup!.ExecuteTask!.IsCompleted)
                    {
                        blocked.TrySetResult(key);
                    }

                    await release.Task.WaitAsync(token);
                }
            };
            // Commit catalog data too, so the system file also needs a final upload.
            await api.CreateProjectAsync(database.Organization, "last-project");
            var text = new Faker().Lorem.Sentence();
            foreach (var target in new[] { database, other })
            {
                using var written = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(target, "people", "last")), new { text }, deadline.Token);
                written.EnsureSuccessStatusCode();
            }

            stop = backup.StopAsync(shutdown.Token);
            var key = await blocked.Task.WaitAsync(deadline.Token);
            await Assert.That(oci.Objects.ContainsKey(key)).IsFalse();
            await shutdown.CancelAsync();
            Exception? failure = null;
            try
            {
                await stop.WaitAsync(deadline.Token);
            }
            catch (Exception error) when (stop.IsCompleted && error is OperationCanceledException or AggregateException)
            {
                failure = error;
            }

            // Check before the fallback cleanup: the old code leaves taps rented, even though admissions are zero.
            var trackedTaps = taps!.Taps;
            await Assert.That(trackedTaps.Count).IsEqualTo(3);
            await Assert.That(trackedTaps.All(tap => tap.Returned)).IsTrue();
            await Assert.That(backup.Status.Files).IsEmpty();
            await Assert.That(failure).IsTypeOf<AggregateException>();
            await Assert.That(((AggregateException)failure!).InnerExceptions.All(error => error is OperationCanceledException)).IsTrue();
            await engine.CleanupCompletion.WaitAsync(deadline.Token);
            await Assert.That(engine.Io.Admitted).IsEqualTo(0);
            await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsFalse();

            var reopened = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
            try
            {
                using var health = await reopened.Client.GetAsync(ManagementApi.Uri("/healthz"), deadline.Token);
                await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
            finally
            {
                // Bounded like every other wait here: a disposal regression fails the test instead of hanging the runner.
                await reopened.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            }
        }
        finally
        {
            await shutdown.CancelAsync();
            release.TrySetResult();
            oci.BeforePut = null;
            stop ??= backup?.StopAsync(shutdown.Token);
            await CleanupAsync(stop, taps, api, directory);
        }
    }

    [Test]
    public async Task A_failing_tap_disposal_still_returns_every_other_tap_and_lets_the_engine_close()
    {
        using var oci = new FakeOci();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-backup-tap-failure-").FullName;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        ManagementApi? api = null;
        TrackedBackup? taps = null;
        Task? stop = null;
        try
        {
            api = await ManagementApi.StartAsync(settings =>
            {
                oci.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                oci.Route(services);
                // The first tap shutdown disposes returns its session and then throws.
                services.AddSingleton<IWalBackup>(provider =>
                    taps = new TrackedBackup(provider.GetRequiredService<SqliteStorageEngine>(), failFirstDisposal: true));
                var registration = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationFactory?.Method.ReturnType == typeof(ContinuousBackup));
                services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                    new BackupLifetime(provider.GetRequiredService<ContinuousBackup>()));
            }, time);
            var backup = api.Server.Services.GetRequiredService<ContinuousBackup>();
            var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
            var database = await api.CreateManagedDatabaseAsync("main");
            var other = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "other");
            using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, database, "people");
            await ManagementApi.CreateCollectionAsync(data, other, "people");
            time.Advance(TimeSpan.FromSeconds(10));
            await backup.RunOnceAsync(token);
            await Assert.That(backup.Status.Files.Count).IsEqualTo(3);

            stop = backup.StopAsync(CancellationToken.None);
            var failure = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(token));

            // The failure is reported, and shutdown went on to return the remaining taps rather than stopping at the first.
            await Assert.That(failure!.InnerExceptions.Any(error => error is InvalidOperationException { Message: TrackedTap.DisposalFailure })).IsTrue();
            var trackedTaps = taps!.Taps;
            await Assert.That(trackedTaps.Count).IsEqualTo(3);
            await Assert.That(trackedTaps.All(tap => tap.Returned)).IsTrue();
            await Assert.That(backup.Status.Files).IsEmpty();
            await engine.CleanupCompletion.WaitAsync(token);
            await Assert.That(engine.Io.Admitted).IsEqualTo(0);
            await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsFalse();
        }
        finally
        {
            await CleanupAsync(stop, taps, api, directory);
        }
    }

    // A timed-out wait ends only the wait. Taps are released only once shutdown has finished with them, the server's
    // disposal is joined within its own deadline, and the directory goes only when both are done. A disposal that failed
    // or did not finish is still reported.
    private static async Task CleanupAsync(Task? stop, TrackedBackup? taps, ManagementApi? api, string directory)
    {
        // Every operation that outlives its deadline is reported once server cleanup has been attempted.
        var unfinished = new List<string>();
        if (stop is not null)
        {
            await stop.WaitAsync(CleanupDeadline).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!stop.IsCompleted)
            {
                unfinished.Add("continuous backup shutdown");
            }
        }

        // Release leaked keepalives on failure too: the regression must never hang the factory's engine drain.
        if (unfinished.Count == 0 && taps is not null)
        {
            foreach (var tap in taps.Taps)
            {
                var released = tap.DisposeAsync().AsTask();
                await released.WaitAsync(CleanupDeadline).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (!released.IsCompleted)
                {
                    unfinished.Add("a WAL tap's disposal");
                }
            }
        }

        Task? disposing = null;
        if (api is not null)
        {
            disposing = api.DisposeAsync().AsTask();
            await disposing.WaitAsync(CleanupDeadline).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!disposing.IsCompleted)
            {
                unfinished.Add("the test server's disposal");
            }
        }

        // Disposal can also finish after the engine's shutdown grace while SQLite cleanup still owns the directory.
        if (unfinished.Count == 0 && !TerraBaseTestServer.IsDataDirectoryOwned(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        if (unfinished.Count != 0)
        {
            throw new TimeoutException($"still running after {CleanupDeadline.TotalSeconds} s: {string.Join(", ", unfinished)}");
        }

        if (disposing is not null)
        {
            await disposing;
        }
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";

    private sealed class BackupLifetime(ContinuousBackup backup) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => backup.StartAsync(cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken) =>
            await backup.StopAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private sealed class TrackedBackup(IWalBackup engine, bool failFirstDisposal = false) : IWalBackup
    {
        private int _opened;

        public ConcurrentBag<TrackedTap> Taps { get; } = [];

        public async ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
        {
            if (await engine.OpenTapAsync(database, cancellationToken) is not { } tap)
            {
                return null;
            }

            var tracked = new TrackedTap(tap, failFirstDisposal && Interlocked.Increment(ref _opened) == 1);
            Taps.Add(tracked);
            return tracked;
        }

        public ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken) =>
            engine.RollForwardAsync(file, segments, cancellationToken);

        public ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken) =>
            engine.ImportDatabaseAsync(database, file, cancellationToken);

        public ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken) => engine.ChecksumAsync(file, cancellationToken);
    }

    private sealed class TrackedTap(IWalTap tap, bool failDisposal) : IWalTap
    {
        public const string DisposalFailure = "the tap failed after returning its session";

        private readonly Lock _gate = new();
        private Task? _disposal;
        private volatile bool _returned;

        // The engine took the tap's session back, even when this tap's disposal then failed.
        public bool Returned => _returned;

        public ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken) => tap.ReadAsync(destination, cancellationToken);

        public ValueTask<WalRead> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken) =>
            tap.SnapshotAsync(destination, snapshotPath, cancellationToken);

        // Shutdown and the test's fallback cleanup can both dispose a tap: they share one disposal, so its session returns once.
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                return new(_disposal ??= DisposeOnceAsync());
            }
        }

        private async Task DisposeOnceAsync()
        {
            await tap.DisposeAsync();
            _returned = true;
            if (failDisposal)
            {
                throw new InvalidOperationException(DisposalFailure);
            }
        }
    }
}
