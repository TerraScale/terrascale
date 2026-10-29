using System.Collections.Concurrent;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupDiscoveryShutdownTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Shutdown_cancels_blocked_discovery_and_joins_customer_shipping_before_returning_taps(bool manualDiscovery)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var shutdown = new CancellationTokenSource();
        var s3 = new FakeS3();
        using var body = new StalledSnapshotBody();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        TrackedBackup? taps = null;
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            services.AddSingleton<IWalBackup>(provider => taps = new TrackedBackup(provider.GetRequiredService<TursoStorageEngine>()));
            // Observe shutdown's cancellation failures here, while allowing factory disposal to rejoin its cleanup.
            var host = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationFactory?.Method.ReturnType == typeof(ContinuousBackup));
            services[services.IndexOf(host)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                new BackupLifetime(provider.GetRequiredService<ContinuousBackup>(), manualDiscovery));
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<ContinuousBackup>();
        var tracked = taps!;
        var passes = new List<Task>(3);
        Task? stopping = null;
        try
        {
            // Fence startup discovery before creating the customer, so its first tap uses the configured destination.
            await backup.RunOnceAsync(token);
            var customer = await CreateDatabaseAsync(api, token);
            await SetCustomerAsync(api, customer, token);
            using var data = api.DataClient(await api.Server.IssueAsync(customer.Organization, ["documents:read", "documents:write"], token));
            using (var collection = await data.PostAsJsonAsync(ManagementApi.Uri(customer.Data + "/collections"), new { name = "people" }, token))
            {
                collection.EnsureSuccessStatusCode();
            }

            using (var write = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(customer, "people", "before")),
                new { text = new Faker().Lorem.Sentence() }, token))
            {
                write.EnsureSuccessStatusCode();
            }

            var customerSnapshots = File(customer) + "/snapshots/";
            s3.CustomerContentOverride = key =>
            {
                if (!key.StartsWith(customerSnapshots, StringComparison.Ordinal) || body.Requested() != 1)
                {
                    return null;
                }

                var content = new StreamContent(body);
                content.Headers.ContentLength = s3.CustomerObjects[key].LongLength;
                return content;
            };
            time.Advance(TimeSpan.FromSeconds(10));
            if (manualDiscovery)
            {
                passes.Add(backup.RunOnceAsync(token));
            }

            await body.Entered.Task.WaitAsync(token);
            await Assert.That(s3.CustomerObjects.Keys.Any(key => key.StartsWith(customerSnapshots, StringComparison.Ordinal))).IsTrue();
            await Assert.That(body.Cancelled.Task.IsCompleted).IsFalse();

            var unseen = await CreateAsync(api.Client, customer.ProjectPath + "/databases", "unseen", token);
            var newDatabase = new ManagedDatabaseRef(customer.Organization, customer.Project, unseen, "unseen");
            using (var collection = await data.PostAsJsonAsync(ManagementApi.Uri(newDatabase.Data + "/collections"), new { name = "people" }, token))
            {
                collection.EnsureSuccessStatusCode();
            }

            tracked.Block(unseen);
            time.Advance(TimeSpan.FromSeconds(10));
            if (manualDiscovery)
            {
                passes.Add(backup.RunOnceAsync(token));
            }

            await tracked.Entered.Task.WaitAsync(token);
            // This caller waits behind the blocked discovery and must also observe service cancellation.
            var waiting = backup.RunOnceAsync(token);
            passes.Add(waiting);
            await Assert.That(waiting.IsCompleted).IsFalse();
            await Assert.That(tracked.Taps.Count).IsEqualTo(2);
            await Assert.That(tracked.Release.Task.IsCompleted).IsFalse();

            stopping = backup.StopAsync(shutdown.Token);
            await shutdown.CancelAsync();
            await tracked.Cancelled.Task.WaitAsync(token);
            await body.Cancelled.Task.WaitAsync(token);
            try
            {
                await stopping.WaitAsync(token);
            }
            catch (AggregateException error) when (stopping.IsCompleted && error.InnerExceptions.All(failure => failure is OperationCanceledException))
            {
                // Final shipping may already have started with the supplied shutdown token before it was cancelled.
            }

            foreach (var pass in passes)
            {
                try
                {
                    await pass.WaitAsync(token);
                }
                catch (OperationCanceledException) when (pass.IsCanceled && !token.IsCancellationRequested)
                {
                    // A closed scheduler may return without work; owned jobs and blocked discovery are cancelled.
                }

                await Assert.That(pass.IsCompleted).IsTrue();
            }

            await Assert.That(token.IsCancellationRequested).IsFalse();
            await Assert.That(tracked.Exited.Task.IsCompleted).IsTrue();
            await Assert.That(tracked.Release.Task.IsCompleted).IsFalse();
            await Assert.That(body.Released.Task.IsCompleted).IsFalse();
            await Assert.That(body.Disposed.Task.IsCompleted).IsTrue();
            await Assert.That(tracked.Taps.Count).IsEqualTo(2);
            await Assert.That(tracked.Taps.All(tap => tap.Returned && tap.Active == 0)).IsTrue();
            await Assert.That(backup.Status.Files).IsEmpty();
            await Assert.That(Directory.EnumerateFiles(Path.Combine(api.Server.DataDirectory, ".backup"))).IsEmpty();
            await Assert.That(manualDiscovery || backup.ExecuteTask!.IsCompleted).IsTrue();
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            // Release both fixture barriers on failure before joining service or host cleanup.
            tracked.Release.TrySetResult();
            body.Released.TrySetResult(0);
            s3.CustomerContentOverride = null;
            await shutdown.CancelAsync();
            stopping ??= backup.StopAsync(shutdown.Token);
            await stopping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await Task.WhenAll(passes).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (var tap in tracked.Taps.Where(tap => !tap.Returned))
            {
                await tap.DisposeAsync();
            }
        }
    }

    private static async Task<ManagedDatabaseRef> CreateDatabaseAsync(ManagementApi api, CancellationToken token)
    {
        var organization = await CreateAsync(api.Client, "/v1/management/organizations", "Shutdown " + Guid.NewGuid().ToString("N"), token);
        var project = await CreateAsync(api.Client, $"/v1/management/organizations/{organization}/projects", "backups", token);
        var database = await CreateAsync(api.Client, $"/v1/management/organizations/{organization}/projects/{project}/databases", "main", token);
        return new(organization, project, database, "main");
    }

    private static async Task<string> CreateAsync(HttpClient client, string path, string name, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(path), new { name }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("id").GetString()!;
    }

    private static async Task SetCustomerAsync(ManagementApi api, ManagedDatabaseRef database, CancellationToken token)
    {
        var destination = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["endpoint"] = FakeS3.CustomerEndpoint,
            ["region"] = "test-region-1",
            ["bucket"] = FakeS3.CustomerBucket,
            ["accessKeyId"] = FakeS3.CustomerAccessKey,
            ["secretKey"] = FakeS3.CustomerSecretKey,
            ["mode"] = "customer",
        };
        using var response = await api.Client.PutAsJsonAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), destination, token);
        response.EnsureSuccessStatusCode();
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private sealed class BackupLifetime(ContinuousBackup backup, bool manual) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => manual ? Task.CompletedTask : backup.StartAsync(cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken) =>
            await backup.StopAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private sealed class TrackedBackup(IWalBackup engine) : IWalBackup
    {
        private string? _blocked;

        public ConcurrentBag<TrackedTap> Taps { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Block(string database) => Volatile.Write(ref _blocked, database);

        public async ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
        {
            if (database is not null && database.DatabaseId == Volatile.Read(ref _blocked))
            {
                Entered.TrySetResult();
                try
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
                finally
                {
                    Exited.TrySetResult();
                }
            }

            if (await engine.OpenTapAsync(database, cancellationToken) is not { } tap)
            {
                return null;
            }

            var tracked = new TrackedTap(tap);
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

    private sealed class TrackedTap(IWalTap tap) : IWalTap
    {
        private int _active;
        private volatile bool _returned;

        public bool Returned => _returned;
        public int Active => Volatile.Read(ref _active);

        public async ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _active);
            try
            {
                return await tap.ReadAsync(destination, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public async ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _active);
            try
            {
                return await tap.SnapshotAsync(destination, snapshotPath, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Active != 0)
            {
                throw new InvalidOperationException("a tap was returned before its operation completed");
            }

            await tap.DisposeAsync();
            _returned = true;
        }
    }

    private sealed class StalledSnapshotBody : MemoryStream
    {
        private int _requests;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;

        public int Requested() => Interlocked.Increment(ref _requests);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                return await Released.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Disposed.TrySetResult();
            }
        }
    }
}
