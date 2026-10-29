using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Slow.Management;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class BackupConfigurationIsolationTests
{
    [Test]
    public async Task A_configuration_deadline_preserves_system_and_platform_shipping_and_restore_without_releasing_the_lookup()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var shipping = CancellationTokenSource.CreateLinkedTokenSource(token);
        var s3 = new FakeS3();
        var hook = new UsageCommitHook();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(s3, time, hook);
        var customer = await api.CreateManagedDatabaseAsync("blocked");
        using var customerData = api.DataClient(await api.Server.IssueAsync(customer.Organization, ["documents:read", "documents:write"], token));
        var faker = new Faker();
        var before = faker.Lorem.Sentence(12);
        var during = faker.Lorem.Sentence(12);
        await SeedAsync(customerData, customer, before, token);
        await SetCustomerAsync(api, customer, token);
        var first = new ConfigurationGate();
        var retry = new ConfigurationGate();
        var gate = first;
        var reads = 0;
        hook.AfterGet = async (collection, id, cancellationToken) =>
        {
            if (collection == "projects/" + customer.Project + "/backup_destination" && id == "current")
            {
                Interlocked.Increment(ref reads);
                await gate.WaitAsync(cancellationToken);
            }
        };
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var passes = new List<Task>(5);
        try
        {
            var firstPass = backup.RunOnceAsync(shipping.Token);
            passes.Add(firstPass);
            await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(firstPass.IsCompleted).IsFalse();
            await Assert.That(first.Cancelled.Task.IsCompleted).IsFalse();

            // Create the unrelated file while admission is blocked, so it can only ship after the lookup expires.
            var healthy = await api.CreateManagedDatabaseAsync("healthy");
            using var data = api.DataClient(await api.Server.IssueAsync(healthy.Organization, ["documents:read", "documents:write"], token));
            await SeedAsync(data, healthy, before, token);
            time.Advance(TimeSpan.FromSeconds(1));
            await firstPass.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(first.Cancelled.Task.IsCompleted).IsTrue();
            await Assert.That(first.Active).IsEqualTo(0);
            await Assert.That(first.Released.Task.IsCompleted).IsFalse();
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith("v1/_system/nodes/local/snapshots/", StringComparison.Ordinal))).IsTrue();
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal))).IsFalse();
            await Assert.That(s3.CustomerObjects.Keys.Any(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal))).IsFalse();

            // A new pass in the backoff interval must not start another catalog lookup.
            var backoffPass = backup.RunOnceAsync(shipping.Token);
            passes.Add(backoffPass);
            await backoffPass.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(Volatile.Read(ref reads)).IsEqualTo(1);

            gate = retry;
            time.Advance(TimeSpan.FromSeconds(10));
            var snapshotPass = backup.RunOnceAsync(shipping.Token);
            passes.Add(snapshotPass);
            await retry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            time.Advance(TimeSpan.FromSeconds(1));
            await snapshotPass.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(retry.Cancelled.Task.IsCompleted).IsTrue();
            await Assert.That(retry.Active).IsEqualTo(0);
            await Assert.That(Volatile.Read(ref reads)).IsEqualTo(2);
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(healthy) + "/snapshots/", StringComparison.Ordinal))).IsTrue();

            time.Advance(TimeSpan.FromSeconds(1));
            await PutAsync(data, healthy, "during", during, token);
            // This catalog write guarantees fresh system WAL after its completed snapshot job.
            await api.CreateOrganizationAsync("System WAL " + Guid.NewGuid().ToString("N"));
            var walPass = backup.RunOnceAsync(shipping.Token);
            passes.Add(walPass);
            await walPass.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(Volatile.Read(ref reads)).IsEqualTo(2);
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith("v1/_system/nodes/local/wal/", StringComparison.Ordinal))).IsTrue();
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(healthy) + "/wal/", StringComparison.Ordinal))).IsTrue();
            await AssertRestoredAsync(api, data, healthy, "platform", "platform-live", [("before", before), ("during", during)], token);
            await Assert.That(first.Released.Task.IsCompleted || retry.Released.Task.IsCompleted).IsFalse();
            await Assert.That(first.Active + retry.Active).IsEqualTo(0);
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal))).IsFalse();
            await Assert.That(s3.CustomerObjects.Keys.Any(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal))).IsFalse();

            // Recovery rereads the real customer-only configuration before it can upload anything.
            hook.AfterGet = null;
            time.Advance(TimeSpan.FromSeconds(10));
            var recoveryPass = backup.RunOnceAsync(shipping.Token);
            passes.Add(recoveryPass);
            await recoveryPass.WaitAsync(TimeSpan.FromSeconds(30), token);
            await AssertRestoredAsync(api, customerData, customer, "customer", "customer-recovered", [("before", before)], token);
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal))).IsFalse();
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            hook.AfterGet = null;
            first.Released.TrySetResult();
            retry.Released.TrySetResult();
            await shipping.CancelAsync();
            await Task.WhenAll(passes).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancellation_joins_a_blocked_configuration_lookup_before_recovery_or_shutdown_cleanup(bool shutdown)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var shipping = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var stop = new CancellationTokenSource();
        var s3 = new FakeS3();
        var hook = new UsageCommitHook();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(s3, time, hook);
        var customer = await api.CreateManagedDatabaseAsync("blocked");
        using var data = api.DataClient(await api.Server.IssueAsync(customer.Organization, ["documents:read", "documents:write"], token));
        var text = new Faker().Lorem.Sentence(12);
        await SeedAsync(data, customer, text, token);
        await SetCustomerAsync(api, customer, token);
        var gate = new ConfigurationGate();
        hook.AfterGet = async (collection, id, cancellationToken) =>
        {
            if (collection == "projects/" + customer.Project + "/backup_destination" && id == "current")
            {
                await gate.WaitAsync(cancellationToken);
            }
        };
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var opened = time.GetUtcNow();
        Task? pass = null;
        Task? stopping = null;
        try
        {
            pass = backup.RunOnceAsync(shipping.Token);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(gate.Active).IsEqualTo(1);
            await Assert.That(gate.Cancelled.Task.IsCompleted).IsFalse();
            if (shutdown)
            {
                // An expired stop token skips final uploads, but cannot skip cancelling and joining the active read.
                await stop.CancelAsync();
                stopping = ((IHostedService)backup).StopAsync(stop.Token);
            }
            else
            {
                await shipping.CancelAsync();
            }

            await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            try
            {
                await pass.WaitAsync(TimeSpan.FromSeconds(30), token);
            }
            catch (OperationCanceledException) when (pass.IsCanceled && !token.IsCancellationRequested)
            {
                // The service/caller cancelled this pass; the test and its resource owner remain live.
            }

            await Assert.That(pass.IsCompleted).IsTrue();
            await Assert.That(gate.Active).IsEqualTo(0);
            await Assert.That(gate.Released.Task.IsCompleted).IsFalse();
            await Assert.That(time.GetUtcNow()).IsEqualTo(opened);
            hook.AfterGet = null;
            if (stopping is not null)
            {
                await stopping.WaitAsync(TimeSpan.FromSeconds(30), token);
                var status = await api.Server.Client.GetFromJsonAsync(ManagementApi.Uri("/v1/backup"), TerraBaseJsonContext.Default.BackupStatus, token);
                await Assert.That(status!.Files).IsEmpty();
                await Assert.That(Directory.EnumerateFiles(Path.Combine(api.Server.DataDirectory, ".backup"))).IsEmpty();
            }
            else
            {
                pass = backup.RunOnceAsync(token);
                await pass.WaitAsync(TimeSpan.FromSeconds(30), token);
                await AssertRestoredAsync(api, data, customer, "customer", "cancel-recovered", [("before", text)], token);
            }

            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(customer) + "/", StringComparison.Ordinal))).IsFalse();
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            hook.AfterGet = null;
            gate.Released.TrySetResult();
            await shipping.CancelAsync();
            if (pass is not null)
            {
                await pass.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            if (stopping is not null)
            {
                await stopping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private static async Task<ManagementApi> StartAsync(FakeS3 s3, FakeTimeProvider time, UsageCommitHook hook)
    {
        var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            hook.Install(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        return api;
    }

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

    private static async Task AssertRestoredAsync(ManagementApi api, HttpClient data, ManagedDatabaseRef database, string destination, string name,
        (string Name, string Text)[] documents, CancellationToken token)
    {
        using var window = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time?destination=" + destination), token);
        window.EnsureSuccessStatusCode();
        var bounds = await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
        using var response = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new
        {
            pointInTime = bounds.GetProperty("latest").GetDateTimeOffset(),
            destination,
            name,
        }, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var restoredBody = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
        await Assert.That(restoredBody.GetProperty("branchedFrom").GetString()).IsEqualTo(database.Id);
        var restored = new ManagedDatabaseRef(database.Organization, database.Project, restoredBody.GetProperty("id").GetString()!, name);
        foreach (var (documentName, text) in documents)
        {
            using var read = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(restored, "people", documentName)), token);
            read.EnsureSuccessStatusCode();
            var stored = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            await Assert.That(stored.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
        }
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private sealed class ConfigurationGate
    {
        private int _active;
        public int Active => Volatile.Read(ref _active);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _active);
            Entered.TrySetResult();
            try
            {
                await Released.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
