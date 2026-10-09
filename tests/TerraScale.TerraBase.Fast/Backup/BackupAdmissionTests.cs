using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Fast.Management;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupAdmissionTests
{
    [Test]
    public async Task Eight_stalled_customer_key_reads_preserve_platform_restore_and_key_refresh_capacity()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var requests = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var oci = new FakeOci();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(oci, time);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var platform = await api.CreateManagedDatabaseAsync("platform");
        using (var plan = await api.Client.PutAsJsonAsync(ManagementApi.Uri($"/v1/management/organizations/{platform.Organization}/plan"), new { plan = "pro" }, token))
        {
            plan.EnsureSuccessStatusCode();
        }

        using var data = api.DataClient(await api.Server.IssueAsync(platform.Organization, ["documents:read", "documents:write"], token));
        var before = new Faker().Lorem.Sentence(12);
        var after = new Faker().Lorem.Sentence(12);
        await SeedAsync(data, platform, before, token);
        var customers = new List<ManagedDatabaseRef>(3);
        for (var index = 0; index < 3; index++)
        {
            var project = await CreateAsync(api.Client, $"/v1/management/organizations/{platform.Organization}/projects", "Customer " + index, token);
            var id = await CreateAsync(api.Client, $"/v1/management/organizations/{platform.Organization}/projects/{project}/databases", "main", token);
            var database = new ManagedDatabaseRef(platform.Organization, project, id, "main");
            customers.Add(database);
            using var configured = await api.Client.PutAsJsonAsync(ManagementApi.Uri(database.ProjectPath + "/backup-destination"), new
            {
                endpoint = FakeOci.CustomerEndpoint,
                region = "test-region-1",
                bucket = FakeOci.CustomerBucket,
                accessKeyId = FakeOci.CustomerAccessKey,
                secretKey = FakeOci.CustomerSecretKey,
                mode = "customer",
            }, token);
            configured.EnsureSuccessStatusCode();
            await SeedAsync(data, database, before, token);
            // Each pass owns the newly discovered customer job, including its restore drill.
            time.Advance(TimeSpan.FromSeconds(10));
            await backup.RunOnceAsync(token);
        }

        var platformPoint = await LatestAsync(api, platform, "platform", token);
        var customerPoints = new DateTimeOffset[3];
        for (var index = 0; index < customers.Count; index++)
        {
            customerPoints[index] = await LatestAsync(api, customers[index], "customer", token);
        }

        var platformKey = oci.Objects.Keys.Single(key => key.StartsWith(Keys(platform), StringComparison.Ordinal));
        var platformReads = oci.ObserveKey(platformKey);
        // Expire both real platform caches while the pro plan still retains the snapshots.
        time.Advance(TimeSpan.FromHours(25));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodies = new ConcurrentBag<StalledKeyBody>();
        var entered = 0;
        var restores = new List<Task<HttpResponseMessage>>(8);
        oci.CustomerContentOverride = key =>
        {
            if (!key.StartsWith("v1/keys/", StringComparison.Ordinal) || Volatile.Read(ref entered) >= 8)
            {
                return null;
            }

            var body = new StalledKeyBody(oci.CustomerObjects[key], release.Task, () =>
            {
                if (Interlocked.Increment(ref entered) == 8)
                {
                    saturated.TrySetResult();
                }
            });
            bodies.Add(body);
            var content = new StreamContent(body);
            content.Headers.ContentLength = body.Length;
            return content;
        };
        try
        {
            // Two projects use distinct restore key managers, sharing the same aggregate customer admission pool.
            for (var index = 0; index < 8; index++)
            {
                var customer = index % 2;
                restores.Add(RestoreAsync(api, customers[customer], customerPoints[customer], "customer", "stalled-" + index, requests.Token));
            }

            await saturated.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(restores.All(restore => !restore.IsCompleted)).IsTrue();
            using (var excess = await RestoreAsync(api, customers[2], customerPoints[2], "customer", "excess", token))
            {
                await Assert.That(excess.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                await AssertPublicAsync(await excess.Content.ReadAsStringAsync(token));
            }

            await Assert.That(Volatile.Read(ref entered)).IsEqualTo(8);
            using (var restored = await RestoreAsync(api, platform, platformPoint, "platform", "platform-cold", token))
            {
                await AssertRestoredAsync(restored, data, platform, [("before", before)], token);
            }

            await Assert.That(platformReads.Gets).IsEqualTo(1);
            await Assert.That(platformReads.Unwraps).IsEqualTo(1);
            var peerId = Ulid.NewUlid(time.GetUtcNow().AddSeconds(1)).ToString();
            var peerKey = Keys(platform) + peerId;
            oci.StoreObject(peerKey, Encoding.ASCII.GetBytes("wrapped:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
            var peerReads = oci.ObserveKey(peerKey);
            time.Advance(TimeSpan.FromSeconds(61));
            await PutAsync(data, platform, "after", after, token);
            await backup.RunOnceAsync(token);
            await Assert.That(peerReads.Gets).IsEqualTo(1);
            await Assert.That(peerReads.Unwraps).IsEqualTo(1);
            await Assert.That(oci.Objects.Any(item => item.Key.StartsWith(File(platform) + "/", StringComparison.Ordinal)
                && Encoding.ASCII.GetString(item.Value, 4, 26) == peerId)).IsTrue();
            using (var restored = await RestoreAsync(api, platform, await LatestAsync(api, platform, "platform", token), "platform", "platform-refreshed", token))
            {
                await AssertRestoredAsync(restored, data, platform, [("before", before), ("after", after)], token);
            }

            await Assert.That(release.Task.IsCompleted).IsFalse();
            await Assert.That(restores.All(restore => !restore.IsCompleted)).IsTrue();
            release.TrySetResult();
            for (var index = 0; index < restores.Count; index++)
            {
                using var restored = await restores[index].WaitAsync(token);
                await AssertRestoredAsync(restored, data, customers[index % 2], [("before", before)], token);
            }

            await Assert.That(bodies.Count).IsEqualTo(8);
            await Assert.That(bodies.All(body => body.Disposed)).IsTrue();
            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            oci.CustomerContentOverride = null;
            release.TrySetResult();
            await requests.CancelAsync();
            // Join listener requests before disposing the host, including when an assertion fails at saturation.
            await ((Task)Task.WhenAll(restores)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (var restore in restores)
            {
                if (restore.IsCompletedSuccessfully)
                {
                    using var response = await restore;
                }
            }

            foreach (var body in bodies)
            {
                body.Dispose();
            }
        }
    }

    [Test]
    public async Task Fleet_configuration_reads_are_bounded_before_platform_work_and_stop_when_all_jobs_are_busy()
    {
        const int fleet = 12;
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var shipping = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var oci = new FakeOci();
        var hook = new UsageCommitHook();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await StartAsync(oci, time, hook.Install);
        var first = await api.CreateManagedDatabaseAsync("fleet-0");
        using var data = api.DataClient(await api.Server.IssueAsync(first.Organization, ["documents:read", "documents:write"], token));
        var text = new Faker().Lorem.Sentence(12);
        await SeedAsync(data, first, text, token);
        for (var index = 1; index < fleet; index++)
        {
            var name = "fleet-" + index;
            var id = await CreateAsync(api.Client, first.ProjectPath + "/databases", name, token);
            await SeedAsync(data, new(first.Organization, first.Project, id, name), text, token);
        }

        var configurationReads = 0;
        var readsBeforeWork = -1;
        var snapshots = 0;
        var firstWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hook.AfterGet = async (collection, id, cancellationToken) =>
        {
            if (collection == "projects/" + first.Project + "/backup_destination" && id == "current"
                && Interlocked.Increment(ref configurationReads) > 4)
            {
                // The old fleet-wide pre-admission scan blocks here before it can start any platform job.
                await firstWork.Task.WaitAsync(cancellationToken);
            }
        };
        oci.BeforePut = async (key, cancellationToken) =>
        {
            if (!key.Contains("/snapshots/", StringComparison.Ordinal))
            {
                return;
            }

            Interlocked.CompareExchange(ref readsBeforeWork, Volatile.Read(ref configurationReads), -1);
            firstWork.TrySetResult();
            if (Interlocked.Increment(ref snapshots) == 4)
            {
                full.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
        };
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        Task? pass = null;
        try
        {
            time.Advance(TimeSpan.FromSeconds(10));
            pass = backup.RunOnceAsync(shipping.Token);
            await full.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(Volatile.Read(ref readsBeforeWork)).IsBetween(0, 4);
            await Assert.That(backup.Status.Files.Count).IsEqualTo(fleet + 1);
            var admittedReads = Volatile.Read(ref configurationReads);
            await Assert.That(admittedReads).IsLessThan(fleet);
            await Assert.That(pass.IsCompleted).IsFalse();

            // Configuration becomes due again while every tracked job remains blocked in its snapshot PUT.
            time.Advance(TimeSpan.FromSeconds(10));
            await backup.RunOnceAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.That(Volatile.Read(ref configurationReads)).IsEqualTo(admittedReads);
            await Assert.That(Volatile.Read(ref snapshots)).IsEqualTo(4);
            await Assert.That(release.Task.IsCompleted).IsFalse();

            release.TrySetResult();
            await pass.WaitAsync(token);
            using var restored = await RestoreAsync(api, first, await LatestAsync(api, first, "platform", token), "platform", "fleet-restored", token);
            await AssertRestoredAsync(restored, data, first, [("before", text)], token);
            await Assert.That(oci.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            hook.AfterGet = null;
            oci.BeforePut = null;
            firstWork.TrySetResult();
            release.TrySetResult();
            await shipping.CancelAsync();
            if (pass is not null)
            {
                await pass.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private static async Task<ManagementApi> StartAsync(FakeOci oci, FakeTimeProvider time, Action<IServiceCollection>? configure = null)
    {
        var api = await ManagementApi.StartAsync(oci.ConfigureCustomer, services =>
        {
            oci.Route(services);
            ManualBackupHosting.Configure(services);
            configure?.Invoke(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        return api;
    }

    private static async Task<string> CreateAsync(HttpClient client, string path, string name, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(path), new { name }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("id").GetString()!;
    }

    private static async Task SeedAsync(HttpClient data, ManagedDatabaseRef database, string text, CancellationToken token)
    {
        using var collection = await data.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/collections"), new { name = "people" }, token);
        collection.EnsureSuccessStatusCode();
        await PutAsync(data, database, "before", text, token);
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string name, string text, CancellationToken token)
    {
        using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", name)), new { text }, token);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<DateTimeOffset> LatestAsync(ManagementApi api, ManagedDatabaseRef database, string destination, CancellationToken token)
    {
        using var response = await api.Client.GetAsync(ManagementApi.Uri(database.Path + "/point-in-time?destination=" + destination), token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("latest").GetDateTimeOffset();
    }

    private static Task<HttpResponseMessage> RestoreAsync(ManagementApi api, ManagedDatabaseRef database, DateTimeOffset at, string destination, string name, CancellationToken token) =>
        api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"), new { pointInTime = at, destination, name }, token);

    private static async Task AssertRestoredAsync(HttpResponseMessage response, HttpClient data, ManagedDatabaseRef source,
        (string Name, string Text)[] documents, CancellationToken token)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var json = await response.Content.ReadAsStringAsync(token);
        await AssertPublicAsync(json);
        using var body = JsonDocument.Parse(json);
        await Assert.That(body.RootElement.GetProperty("branchedFrom").GetString()).IsEqualTo(source.Id);
        var restored = new ManagedDatabaseRef(source.Organization, source.Project, body.RootElement.GetProperty("id").GetString()!, body.RootElement.GetProperty("name").GetString()!);
        foreach (var (name, text) in documents)
        {
            using var read = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(restored, "people", name)), token);
            read.EnsureSuccessStatusCode();
            await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
        }
    }

    private static async Task AssertPublicAsync(string json)
    {
        await Assert.That(json.Contains(FakeOci.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains(FakeOci.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("ocid1.vaultsecret", StringComparison.Ordinal)).IsFalse();
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}";
    private static string Keys(ManagedDatabaseRef database) => $"v1/keys/{database.Organization}/{database.Project}/";

    private sealed class StalledKeyBody(byte[] content, Task release, Action entered) : MemoryStream(content, writable: false)
    {
        private int _entered;
        private int _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public override bool CanSeek => false;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _entered, 1) == 0)
            {
                entered();
            }

            await release.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Interlocked.Exchange(ref _disposed, 1);
            }
        }
    }
}
