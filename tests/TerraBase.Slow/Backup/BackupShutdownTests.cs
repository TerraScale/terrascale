using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class BackupShutdownTests
{
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(30);

    [Test]
    public async Task A_failed_customer_upload_then_detachment_flushes_a_final_platform_snapshot_without_waiting_for_backoff()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await SetCustomerAsync(api, database, token);
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var text = new Faker().Lorem.Sentence();
        await PutAsync(data, database, "before", text, token);
        var backup = api.Server.Services.GetRequiredService<ContinuousBackup>();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        await PutAsync(data, database, "final", text, token);
        s3.RejectCustomerWrites = key => key.StartsWith(File(database) + "/wal/", StringComparison.Ordinal);
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);
        var failed = backup.Status.Files.Single(file => file.File == File(database));
        await Assert.That(failed.LastError).IsNotNull();
        await Assert.That(failed.Generation).IsNull();
        await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))).IsFalse();
        using var detached = await api.Client.DeleteAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), token);
        await Assert.That(detached.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var at = time.GetUtcNow();
        await backup.StopAsync(token);
        await Assert.That(time.GetUtcNow()).IsEqualTo(at);
        await Assert.That(s3.Objects.Keys.Count(key => key.StartsWith(File(database) + "/snapshots/", StringComparison.Ordinal))).IsEqualTo(1);
        await AssertRestoredAsync(api, data, database, at, "failed-detached", text, token);
        await Assert.That(backup.Status.Files).IsEmpty();
        await Assert.That(Directory.EnumerateFiles(Path.Combine(api.Server.DataDirectory, ".backup"))).IsEmpty();
        await Assert.That(s3.BadSignatures).IsEqualTo(0);
    }

    [Test]
    public async Task Multiple_cached_customer_configuration_deadlines_cannot_precede_healthy_final_wal_and_restore()
    {
        const int customers = 3;
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
        var directory = Directory.CreateTempSubdirectory("terrabase-final-configurations-").FullName;
        var hook = new UsageCommitHook();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Enumerable.Range(0, customers).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var exited = Enumerable.Range(0, customers).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        ManagementApi? api = null;
        TrackedBackup? taps = null;
        ContinuousBackup? backup = null;
        Task? stop = null;
        try
        {
            api = await ManagementApi.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                s3.Route(services);
                hook.Install(services);
                services.AddSingleton<IWalBackup>(provider => taps = new TrackedBackup(provider.GetRequiredService<TursoStorageEngine>()));
                var registration = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationFactory?.Method.ReturnType == typeof(ContinuousBackup));
                services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                    new BackupLifetime(provider.GetRequiredService<ContinuousBackup>(), manual: true));
            }, time);
            Directory.CreateDirectory(Path.Combine(directory, ".backup"));
            backup = api.Server.Services.GetRequiredService<ContinuousBackup>();
            var customerCollections = new HashSet<string>(StringComparer.Ordinal);
            var text = new Faker().Lorem.Sentence();
            for (var index = 0; index < customers; index++)
            {
                var customer = await api.CreateManagedDatabaseAsync("customer-" + index);
                await SetCustomerAsync(api, customer, token);
                using var data = await api.CreateClientAsync(customer.Organization, "documents:write");
                using var collection = await data.PostAsJsonAsync(ManagementApi.Uri(customer.Data + "/collections"), new { name = "people" }, token);
                collection.EnsureSuccessStatusCode();
                await PutAsync(data, customer, "before", text, token);
                customerCollections.Add("projects/" + customer.Project + "/backup_destination");
            }

            var healthy = await api.CreateManagedDatabaseAsync("healthy");
            using var healthyData = await api.CreateClientAsync(healthy.Organization, "documents:read", "documents:write");
            using (var collection = await healthyData.PostAsJsonAsync(ManagementApi.Uri(healthy.Data + "/collections"), new { name = "people" }, token))
            {
                collection.EnsureSuccessStatusCode();
            }

            await PutAsync(healthyData, healthy, "before", text, token);
            time.Advance(TimeSpan.FromSeconds(10));
            await backup.RunOnceAsync(token);
            await backup.RunOnceAsync(token);
            await Assert.That(backup.Status.Files.Count).IsEqualTo(customers + 2);
            await Assert.That(backup.Status.Files.All(file => file.Generation is not null && file.LastDrill is not null)).IsTrue();
            var snapshot = backup.Status.Files.Single(file => file.File == File(healthy));
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(healthy) + "/wal/", StringComparison.Ordinal))).IsFalse();
            var customerObjects = s3.CustomerObjects.Count;

            time.Advance(TimeSpan.FromSeconds(1));
            var committed = new Faker().Lorem.Sentence();
            await PutAsync(healthyData, healthy, "final", committed, token);
            var reads = 0;
            var healthyReads = 0;
            hook.AfterGet = async (collection, id, cancellationToken) =>
            {
                if (id != "current")
                {
                    return;
                }

                if (collection == "projects/" + healthy.Project + "/backup_destination")
                {
                    Interlocked.Increment(ref healthyReads);
                }
                else if (customerCollections.Contains(collection))
                {
                    var index = Interlocked.Increment(ref reads) - 1;
                    entered[index].TrySetResult();
                    try
                    {
                        await release.Task.WaitAsync(cancellationToken);
                    }
                    finally
                    {
                        exited[index].TrySetResult();
                    }
                }
            };
            var budget = api.Server.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
            shutdown.CancelAfter(budget);
            var elapsed = Stopwatch.StartNew();
            stop = backup.StopAsync(shutdown.Token);
            await entered[0].Task.WaitAsync(shutdown.Token);
            await Assert.That(Volatile.Read(ref healthyReads)).IsEqualTo(1);
            var finalWal = s3.Objects.Keys.Single(key => key.StartsWith(File(healthy) + "/wal/", StringComparison.Ordinal));
            var finalAt = BackupLayout.ParseSegment(finalWal)!.At;
            await Assert.That(finalAt).IsGreaterThan(snapshot.LastShippedAt!.Value);
            await Assert.That(s3.Objects.Keys.Count(key => key.StartsWith(File(healthy) + "/snapshots/", StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(shutdown.IsCancellationRequested).IsFalse();
            await Assert.That(elapsed.Elapsed).IsLessThan(budget);

            for (var index = 0; index < customers; index++)
            {
                await entered[index].Task.WaitAsync(shutdown.Token);
                await Assert.That(stop.IsCompleted).IsFalse();
                await Assert.That(taps!.Taps.All(tap => !tap.Returned)).IsTrue();
                time.Advance(TimeSpan.FromSeconds(1));
                await exited[index].Task.WaitAsync(shutdown.Token);
            }

            var failure = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(shutdown.Token));
            await Assert.That(failure!.InnerExceptions.Count).IsEqualTo(customers);
            await Assert.That(failure!.InnerExceptions.All(error => error is OperationCanceledException)).IsTrue();
            await Assert.That(Volatile.Read(ref reads)).IsEqualTo(customers);
            await Assert.That(Volatile.Read(ref healthyReads)).IsEqualTo(1);
            await Assert.That(release.Task.IsCompleted).IsFalse();
            await Assert.That(s3.CustomerObjects.Count).IsEqualTo(customerObjects);
            await Assert.That(taps!.Taps.All(tap => tap.Returned)).IsTrue();
            await Assert.That(backup.Status.Files).IsEmpty();
            await Assert.That(Directory.EnumerateFiles(Path.Combine(directory, ".backup"))).IsEmpty();
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
            // The WAL was stored before the first customer lookup; restoring it must not spend the shutdown budget.
            await AssertRestoredAsync(api, healthyData, healthy, finalAt, "final-config-wal", committed, token);
        }
        finally
        {
            hook.AfterGet = null;
            release.TrySetResult();
            await shutdown.CancelAsync();
            stop ??= backup?.StopAsync(shutdown.Token);
            await CleanupAsync(stop, taps, api, directory);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Final_customer_io_waits_until_platform_wal_and_a_cached_detachment_are_restorable(bool detach)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
        var directory = Directory.CreateTempSubdirectory("terrabase-final-customer-").FullName;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        ManagementApi? api = null;
        TrackedBackup? taps = null;
        ContinuousBackup? backup = null;
        Task? stop = null;
        try
        {
            api = await ManagementApi.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                s3.Route(services);
                services.AddSingleton<IWalBackup>(provider => taps = new TrackedBackup(provider.GetRequiredService<TursoStorageEngine>()));
                var registration = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationFactory?.Method.ReturnType == typeof(ContinuousBackup));
                services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                    new BackupLifetime(provider.GetRequiredService<ContinuousBackup>(), manual: true));
            }, time);
            Directory.CreateDirectory(Path.Combine(directory, ".backup"));
            backup = api.Server.Services.GetRequiredService<ContinuousBackup>();
            var customer = await api.CreateManagedDatabaseAsync("customer");
            var healthy = await api.CreateManagedDatabaseAsync("healthy");
            var stalled = detach ? await api.CreateManagedDatabaseAsync("stalled") : null;
            using var customerData = await api.CreateClientAsync(customer.Organization, "documents:write");
            using var healthyData = await api.CreateClientAsync(healthy.Organization, "documents:read", "documents:write");
            using var stalledData = stalled is null ? null : await api.CreateClientAsync(stalled.Organization, "documents:write");
            await SetCustomerAsync(api, customer, token);
            await ManagementApi.CreateCollectionAsync(customerData, customer, "people");
            await ManagementApi.CreateCollectionAsync(healthyData, healthy, "people");
            var text = new Faker().Lorem.Sentence();
            await PutAsync(customerData, customer, "before", text, token);
            await PutAsync(healthyData, healthy, "before", text, token);
            if (stalled is not null)
            {
                await SetCustomerAsync(api, stalled, token);
                await ManagementApi.CreateCollectionAsync(stalledData!, stalled, "people");
                await PutAsync(stalledData!, stalled, "before", text, token);
            }

            time.Advance(TimeSpan.FromSeconds(10));
            var initial = backup.RunOnceAsync(token);
            await initial;
            var snapshot = backup.Status.Files.Single(file => file.File == File(healthy));
            await Assert.That(snapshot.Generation).IsNotNull();
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(healthy) + "/wal/", StringComparison.Ordinal))).IsFalse();
            var customerObjects = s3.CustomerObjects.Count;
            if (detach)
            {
                // Keep the ten-second shipping cache fresh: final refresh must still notice this committed detachment.
                using var removed = await api.Client.DeleteAsync(ManagementApi.Uri(customer.ProjectPath + "/backup-destination"), token);
                await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            time.Advance(TimeSpan.FromSeconds(1));
            var committed = new Faker().Lorem.Sentence();
            await PutAsync(healthyData, healthy, "final", committed, token);
            await PutAsync(customerData, customer, "final", committed, token);
            if (stalled is not null)
            {
                await PutAsync(stalledData!, stalled, "final", committed, token);
            }

            s3.BeforeCustomerWrite = async (key, cancellationToken) =>
            {
                if (!key.Contains("/wal/", StringComparison.Ordinal) && !key.Contains("/snapshots/", StringComparison.Ordinal))
                {
                    return;
                }

                await Assert.That(initial.IsCompletedSuccessfully).IsTrue();
                entered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    await joinRelease.Task;
                    throw;
                }
                finally
                {
                    exited.TrySetResult();
                }
            };
            var budget = api.Server.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
            shutdown.CancelAfter(budget);
            var elapsed = Stopwatch.StartNew();
            stop = backup.StopAsync(shutdown.Token);
            await entered.Task.WaitAsync(shutdown.Token);
            var finalWal = s3.Objects.Keys.Single(key => key.StartsWith(File(healthy) + "/wal/", StringComparison.Ordinal));
            var finalAt = BackupLayout.ParseSegment(finalWal)!.At;
            await Assert.That(finalAt).IsGreaterThan(snapshot.LastShippedAt!.Value);
            await Assert.That(s3.Objects.Keys.Count(key => key.StartsWith(File(healthy) + "/snapshots/", StringComparison.Ordinal))).IsEqualTo(1);
            await AssertRestoredAsync(api, healthyData, healthy, finalAt, "final-wal", committed, shutdown.Token);
            if (detach)
            {
                var detachedAt = backup.Status.Files.Single(file => file.File == File(customer)).LastShippedAt!.Value;
                using var detachedData = await api.CreateClientAsync(customer.Organization, "documents:read");
                await AssertRestoredAsync(api, detachedData, customer, detachedAt, "detached", committed, shutdown.Token);
                await Assert.That(s3.CustomerObjects.Count).IsEqualTo(customerObjects);
            }

            await Assert.That(shutdown.IsCancellationRequested).IsFalse();
            await Assert.That(elapsed.Elapsed).IsLessThan(budget);
            await Assert.That(stop.IsCompleted).IsFalse();
            await shutdown.CancelAsync();
            await cancelled.Task.WaitAsync(token);
            await Assert.That(stop.IsCompleted).IsFalse();
            await Assert.That(taps!.Taps.All(tap => !tap.Returned)).IsTrue();
            joinRelease.TrySetResult();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(token));
            await Assert.That(failure!.InnerExceptions.All(error => error is OperationCanceledException)).IsTrue();
            await Assert.That(exited.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(taps.Taps.All(tap => tap.Returned)).IsTrue();
            await Assert.That(backup.Status.Files).IsEmpty();
            await Assert.That(Directory.EnumerateFiles(Path.Combine(directory, ".backup"))).IsEmpty();
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            release.TrySetResult();
            joinRelease.TrySetResult();
            await shutdown.CancelAsync();
            s3.BeforeCustomerWrite = null;
            stop ??= backup?.StopAsync(shutdown.Token);
            await CleanupAsync(stop, taps, api, directory);
        }
    }

    [Test]
    public async Task An_already_expired_stop_token_joins_active_customer_io_before_disposing_any_tap()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        using var shutdown = new CancellationTokenSource();
        var directory = Directory.CreateTempSubdirectory("terrabase-expired-customer-stop-").FullName;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        ManagementApi? api = null;
        TrackedBackup? taps = null;
        ContinuousBackup? backup = null;
        Task? pass = null;
        Task? stop = null;
        try
        {
            api = await ManagementApi.StartAsync(settings =>
            {
                s3.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                s3.Route(services);
                services.AddSingleton<IWalBackup>(provider => taps = new TrackedBackup(provider.GetRequiredService<TursoStorageEngine>()));
                var registration = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationFactory?.Method.ReturnType == typeof(ContinuousBackup));
                services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                    new BackupLifetime(provider.GetRequiredService<ContinuousBackup>(), manual: true));
            }, time);
            Directory.CreateDirectory(Path.Combine(directory, ".backup"));
            backup = api.Server.Services.GetRequiredService<ContinuousBackup>();
            var customer = await api.CreateManagedDatabaseAsync("customer");
            await SetCustomerAsync(api, customer, token);
            s3.BeforeCustomerWrite = async (key, cancellationToken) =>
            {
                if (!key.StartsWith(File(customer) + "/snapshots/", StringComparison.Ordinal))
                {
                    return;
                }

                entered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    await joinRelease.Task;
                    throw;
                }
                finally
                {
                    exited.TrySetResult();
                }
            };
            time.Advance(TimeSpan.FromSeconds(10));
            pass = backup.RunOnceAsync(token);
            await entered.Task.WaitAsync(token);
            await shutdown.CancelAsync();
            stop = backup.StopAsync(shutdown.Token);
            await cancelled.Task.WaitAsync(token);
            await Assert.That(stop.IsCompleted).IsFalse();
            await Assert.That(taps!.Taps.All(tap => !tap.Returned)).IsTrue();
            joinRelease.TrySetResult();
            await stop.WaitAsync(token);
            await pass.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await Assert.That(pass.IsCompleted).IsTrue();
            await Assert.That(exited.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(taps.Taps.Count).IsEqualTo(2);
            await Assert.That(taps.Taps.All(tap => tap.Returned)).IsTrue();
            await Assert.That(backup.Status.Files).IsEmpty();
            await Assert.That(Directory.EnumerateFiles(Path.Combine(directory, ".backup"))).IsEmpty();
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal))).IsFalse();
            // The authenticated lineage certificate is published before the snapshot. A cancelled snapshot can leave
            // that metadata behind, but no customer snapshot or WAL bytes may reach the destination.
            var remaining = s3.CustomerObjects.Keys.Where(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal)).ToArray();
            await Assert.That(remaining.Length).IsEqualTo(1);
            await Assert.That(remaining[0].StartsWith(File(customer) + "/generations/", StringComparison.Ordinal)).IsTrue();
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            release.TrySetResult();
            joinRelease.TrySetResult();
            await shutdown.CancelAsync();
            s3.BeforeCustomerWrite = null;
            stop ??= backup?.StopAsync(shutdown.Token);
            if (pass is not null)
            {
                await pass.WaitAsync(CleanupDeadline).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await CleanupAsync(stop, taps, api, directory);
        }
    }

    [Test]
    public async Task Cancelling_the_final_upload_returns_every_tap_and_allows_the_data_directory_to_reopen()
    {
        var s3 = new FakeS3();
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
                s3.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                s3.Route(services);
                services.AddSingleton<IWalBackup>(provider => taps = new TrackedBackup(provider.GetRequiredService<TursoStorageEngine>()));
                // Factory disposal rejoins StopAsync. The test observes its failure directly; the host must still dispose.
                var registration = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationFactory?.Method.ReturnType == typeof(ContinuousBackup));
                services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                    new BackupLifetime(provider.GetRequiredService<ContinuousBackup>()));
            }, time);
            backup = api.Server.Services.GetRequiredService<ContinuousBackup>();
            var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
            await WaitForSnapshotsAsync(api, deadline.Token, "v1/_system/nodes/local");
            var database = await api.CreateManagedDatabaseAsync("main");
            var other = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "other");
            using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, database, "people");
            await ManagementApi.CreateCollectionAsync(data, other, "people");
            time.Advance(TimeSpan.FromSeconds(10));
            await WaitForSnapshotsAsync(api, deadline.Token, "v1/_system/nodes/local", File(database), File(other));
            await Assert.That(backup.Status.Files.Select(file => file.File)).IsEquivalentTo(
                ["v1/_system/nodes/local", File(database), File(other)]);
            await Assert.That(backup.Status.Files.All(file => file.Generation is not null)).IsTrue();

            s3.BeforePut = async (key, token) =>
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
            await Assert.That(s3.Objects.ContainsKey(key)).IsFalse();
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
            s3.BeforePut = null;
            stop ??= backup?.StopAsync(shutdown.Token);
            await CleanupAsync(stop, taps, api, directory);
        }
    }

    [Test]
    public async Task A_failing_tap_disposal_still_returns_every_other_tap_and_lets_the_engine_close()
    {
        var s3 = new FakeS3();
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
                s3.Configure(settings);
                settings["data-directory"] = directory;
            }, services =>
            {
                s3.Route(services);
                // The first tap shutdown disposes returns its session and then throws.
                services.AddSingleton<IWalBackup>(provider =>
                    taps = new TrackedBackup(provider.GetRequiredService<TursoStorageEngine>(), failFirstDisposal: true));
                var registration = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationFactory?.Method.ReturnType == typeof(ContinuousBackup));
                services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                    new BackupLifetime(provider.GetRequiredService<ContinuousBackup>()));
            }, time);
            var backup = api.Server.Services.GetRequiredService<ContinuousBackup>();
            var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
            await WaitForSnapshotsAsync(api, token, "v1/_system/nodes/local");
            var database = await api.CreateManagedDatabaseAsync("main");
            var other = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "other");
            using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, database, "people");
            await ManagementApi.CreateCollectionAsync(data, other, "people");
            time.Advance(TimeSpan.FromSeconds(10));
            await WaitForSnapshotsAsync(api, token, "v1/_system/nodes/local", File(database), File(other));
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

    private static async Task WaitForSnapshotsAsync(ManagementApi api, CancellationToken cancellationToken, params string[] files)
    {
        // The real timer owns these jobs; a skip-busy manual pass cannot join their snapshot uploads and drills.
        while (true)
        {
            var status = await api.Server.Client.GetFromJsonAsync(ManagementApi.Uri("/v1/backup"), TerraBaseJsonContext.Default.BackupStatus, cancellationToken);
            if (files.All(prefix => status!.Files.Any(file => file.File == prefix && file.Generation is not null && file.LastDrill is not null)))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
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

        // Disposal can also finish after the engine's shutdown grace while Turso cleanup still owns the directory.
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

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private static async Task SetCustomerAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken token)
    {
        using var response = await api.Client.PutAsJsonAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), new
        {
            endpoint = FakeS3.CustomerEndpoint,
            region = "test-region-1",
            bucket = FakeS3.CustomerBucket,
            accessKeyId = FakeS3.CustomerAccessKey,
            secretKey = FakeS3.CustomerSecretKey,
            mode = "customer",
        }, token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string name, string text, CancellationToken token)
    {
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", name)), new { text }, token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssertRestoredAsync(ManagementApi api, HttpClient data, ManagedDatabaseRef database, DateTimeOffset at, string name, string expected, CancellationToken token)
    {
        using var restored = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime = at, name }, token);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var read = await data.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{name}/collections/people/documents/by-key?id={ManagementApi.DocumentId("final")}"), token);
        read.EnsureSuccessStatusCode();
        var document = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
        await Assert.That(document.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(expected);
    }

    private sealed class BackupLifetime(ContinuousBackup backup, bool manual = false) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => manual ? Task.CompletedTask : backup.StartAsync(cancellationToken);

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

        public LogCut CutLog(Stream segment, DateTimeOffset at, long? previous) => engine.CutLog(segment, at, previous);

        public DateTimeOffset? ReadMark(Stream segment) => engine.ReadMark(segment);

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

        public ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken) =>
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
