using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

public sealed class CustomerBackupIsolationTests
{
    [Test]
    [Arguments(1, "deadline")]
    [Arguments(2, "deadline")]
    [Arguments(1, "cancellation")]
    [Arguments(2, "shutdown")]
    public async Task Platform_shipping_and_restore_continue_while_customer_admission_is_stalled(int stalledCustomers, string completion)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var shipping = CancellationTokenSource.CreateLinkedTokenSource(token);
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var customers = new List<ManagedDatabaseRef>();
        var clients = new List<HttpClient>();
        var bodies = new Dictionary<string, StalledSnapshotBody>(StringComparer.Ordinal);
        var passes = new List<Task>();
        Task? stopping = null;
        var faker = new Faker();
        var before = faker.Lorem.Sentence(12);
        var during = faker.Lorem.Sentence(12);
        var recovered = faker.Lorem.Sentence(12);
        try
        {
            for (var index = 0; index < stalledCustomers; index++)
            {
                var database = await CreateDatabaseAsync(api, organization: null, token);
                customers.Add(database);
                var data = api.DataClient(await api.Server.IssueAsync(database.Organization, ["documents:read", "documents:write"], token));
                clients.Add(data);
                await SetCustomerAsync(api, database, token);
                await CreateCollectionAsync(data, database, token);
                await PutAsync(data, database, "before", before, token);
                bodies.Add(File(database) + "/snapshots/", new StalledSnapshotBody());
            }

            await Assert.That(customers.Select(database => database.Organization).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(stalledCustomers);
            var prefixes = customers.SelectMany(database => new[] { File(database) + "/", $"v1/keys/{database.Organization}/{database.Project}/" }).ToArray();
            var foreignKey = $"v1/{Guid.NewGuid():D}/{Guid.NewGuid():D}/{Guid.NewGuid():D}/snapshots/unrelated";
            byte[] foreignContent = [1, 2, 3];
            s3.SeedObject(customer: true, foreignKey, foreignContent);
            var foreignRequests = 0;
            s3.ObjectRequested = (customer, method, key) =>
            {
                if (customer && key.Length > 0 && (method == HttpMethod.Get || method == HttpMethod.Delete)
                    && !prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    Interlocked.Increment(ref foreignRequests);
                }
            };
            s3.CustomerContentOverride = key =>
            {
                var body = bodies.FirstOrDefault(pair => key.StartsWith(pair.Key, StringComparison.Ordinal)).Value;
                if (body is null || body.Requested() != 1)
                {
                    return null;
                }

                var content = new StreamContent(body);
                content.Headers.ContentLength = s3.CustomerObjects[key].LongLength;
                return content;
            };

            time.Advance(TimeSpan.FromSeconds(10));
            var firstPass = backup.RunOnceAsync(shipping.Token);
            passes.Add(firstPass);
            await Task.WhenAll(bodies.Values.Select(body => body.Entered.Task)).WaitAsync(token);
            var stalledAt = time.GetUtcNow();
            await Assert.That(firstPass.IsCompleted).IsFalse();

            // Admit a new platform file after two distinct organizations can already occupy all customer capacity.
            var healthy = await CreateDatabaseAsync(api, customers[0].Organization, token);
            var healthyData = clients[0];
            await CreateCollectionAsync(healthyData, healthy, token);
            await PutAsync(healthyData, healthy, "before", before, token);
            var platformSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            s3.BeforeGet = (key, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (key.StartsWith(File(healthy) + "/snapshots/", StringComparison.Ordinal))
                {
                    // The restore drill reads only after this snapshot has been accepted into the platform bucket.
                    platformSnapshot.TrySetResult();
                }

                return Task.CompletedTask;
            };
            time.Advance(TimeSpan.FromSeconds(10));
            var snapshotPass = backup.RunOnceAsync(shipping.Token);
            passes.Add(snapshotPass);
            await platformSnapshot.Task.WaitAsync(token);
            await snapshotPass.WaitAsync(token);
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(healthy) + "/snapshots/", StringComparison.Ordinal))).IsTrue();

            time.Advance(TimeSpan.FromSeconds(1));
            await PutAsync(healthyData, healthy, "during", during, token);
            time.Advance(TimeSpan.FromSeconds(1));
            var walPass = backup.RunOnceAsync(shipping.Token);
            passes.Add(walPass);
            await walPass.WaitAsync(token);
            await Assert.That(s3.Objects.Keys.Any(key => key.StartsWith(File(healthy) + "/wal/", StringComparison.Ordinal))).IsTrue();
            await AssertRestoredAsync(api, healthyData, healthy, "platform", "platform-live", [("before", before), ("during", during)], token);

            // Prove progress while bodies are still blocked, rather than recovery after their fifteen-minute deadlines.
            await Assert.That(time.GetUtcNow() - stalledAt).IsLessThan(TimeSpan.FromMinutes(15));
            await Assert.That(firstPass.IsCompleted).IsFalse();
            foreach (var body in bodies.Values)
            {
                await Assert.That(body.Requests).IsEqualTo(1);
                await Assert.That(body.Cancelled.Task.IsCompleted).IsFalse();
                await Assert.That(body.Disposed.Task.IsCompleted).IsFalse();
            }

            await Assert.That(s3.Objects.Keys.Any(key => customers.Any(database => key.StartsWith(File(database) + "/", StringComparison.Ordinal)))).IsFalse();
            await Assert.That(s3.CustomerObjects.Keys.All(key => key == foreignKey || prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))).IsTrue();
            await Assert.That(Volatile.Read(ref foreignRequests)).IsEqualTo(0);
            await Assert.That(s3.CustomerObjects[foreignKey].SequenceEqual(foreignContent)).IsTrue();
            foreach (var text in new[] { before, during })
            {
                var plain = Encoding.UTF8.GetBytes(text);
                await Assert.That(s3.Objects.Values.Concat(s3.CustomerObjects.Values).Any(content => content.AsSpan().IndexOf(plain) >= 0)).IsFalse();
            }

            s3.CustomerContentOverride = null;
            if (completion == "shutdown")
            {
                // Keep the blocked bodies in place: StopAsync must cancel and join jobs before releasing their files.
                stopping = ((IHostedService)backup).StopAsync(token);
                await Task.WhenAll(bodies.Values.Select(body => body.Cancelled.Task)).WaitAsync(token);
                await stopping.WaitAsync(token);
                await JoinCancelledPassAsync(firstPass, token);
                await Assert.That(backup.Status.Files).IsEmpty();
            }
            else
            {
                if (completion == "cancellation")
                {
                    await shipping.CancelAsync();
                    await Task.WhenAll(bodies.Values.Select(body => body.Cancelled.Task)).WaitAsync(token);
                    await JoinCancelledPassAsync(firstPass, token);
                }
                else
                {
                    time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
                    await Task.WhenAll(bodies.Values.Select(body => body.Cancelled.Task)).WaitAsync(token);
                    await firstPass.WaitAsync(token);
                    foreach (var customer in customers)
                    {
                        await Assert.That(backup.Status.Files.Single(file => file.File == File(customer)).LastDrill!.Passed).IsFalse();
                    }
                }

                for (var index = 0; index < customers.Count; index++)
                {
                    await PutAsync(clients[index], customers[index], "recovered", recovered, token);
                }

                time.Advance(TimeSpan.FromSeconds(10));
                var recoveryPass = backup.RunOnceAsync(token);
                passes.Add(recoveryPass);
                await recoveryPass.WaitAsync(token);
                for (var index = 0; index < customers.Count; index++)
                {
                    await AssertRestoredAsync(api, clients[index], customers[index], "customer", "customer-recovered",
                        [("before", before), ("recovered", recovered)], token);
                }
            }

            await Task.WhenAll(bodies.Values.Select(body => body.Disposed.Task)).WaitAsync(token);
            await Assert.That(shipping.IsCancellationRequested).IsEqualTo(completion == "cancellation");
            await Assert.That(Volatile.Read(ref foreignRequests)).IsEqualTo(0);
            await Assert.That(s3.CustomerObjects[foreignKey].SequenceEqual(foreignContent)).IsTrue();
            await Assert.That(s3.BadSignatures).IsEqualTo(0);
        }
        finally
        {
            s3.CustomerContentOverride = null;
            s3.BeforeGet = null;
            s3.ObjectRequested = null;
            // Release before joining even when the test token has cancelled; host disposal must never inherit the stall.
            foreach (var body in bodies.Values)
            {
                body.Release();
            }

            await shipping.CancelAsync();
            await Task.WhenAll(passes).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (stopping is not null)
            {
                await stopping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            foreach (var client in clients)
            {
                client.Dispose();
            }

            foreach (var body in bodies.Values)
            {
                body.Dispose();
            }
        }
    }

    private static async Task JoinCancelledPassAsync(Task pass, CancellationToken token)
    {
        try
        {
            await pass.WaitAsync(token);
        }
        catch (OperationCanceledException) when (pass.IsCanceled && !token.IsCancellationRequested)
        {
            // Job cancellation may propagate through its owning manual pass; the test itself is still live.
        }
    }

    private static async Task<ManagedDatabaseRef> CreateDatabaseAsync(ManagementApi api, string? organization, CancellationToken token)
    {
        organization ??= await CreateAsync(api.Client, "/v1/management/organizations", "Isolation organization " + Guid.NewGuid().ToString("N"), token);
        var project = await CreateAsync(api.Client, $"/v1/management/organizations/{organization}/projects", "Isolation project " + Guid.NewGuid().ToString("N"), token);
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
        await AssertPublicAsync(await response.Content.ReadAsStringAsync(token));
    }

    private static async Task CreateCollectionAsync(HttpClient data, ManagedDatabaseRef database, CancellationToken token)
    {
        using var response = await data.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/collections"), new { name = "people" }, token);
        response.EnsureSuccessStatusCode();
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
        var windowJson = await window.Content.ReadAsStringAsync(token);
        await AssertPublicAsync(windowJson);
        using var bounds = JsonDocument.Parse(windowJson);
        var latest = bounds.RootElement.GetProperty("latest").GetDateTimeOffset();
        using var response = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/point-in-time/restore"),
            new { pointInTime = latest, destination, name }, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var json = await response.Content.ReadAsStringAsync(token);
        await AssertPublicAsync(json);
        using var body = JsonDocument.Parse(json);
        await Assert.That(body.RootElement.GetProperty("branchedFrom").GetString()).IsEqualTo(database.Id);
        var restored = new ManagedDatabaseRef(database.Organization, database.Project, body.RootElement.GetProperty("id").GetString()!, name);
        foreach (var (documentName, documentText) in documents)
        {
            using var read = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(restored, "people", documentName)), token);
            read.EnsureSuccessStatusCode();
            var stored = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            await Assert.That(stored.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(documentText);
        }
    }

    private static async Task AssertPublicAsync(string json)
    {
        await Assert.That(json.Contains(FakeS3.CustomerAccessKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains(FakeS3.CustomerSecretKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("wrappedKey", StringComparison.Ordinal)).IsFalse();
    }

    private static string File(ManagedDatabaseRef database) => $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";

    private sealed class StalledSnapshotBody : MemoryStream
    {
        private readonly TaskCompletionSource<int> _body = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests => Volatile.Read(ref _requests);
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
                return await _body.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public void Release() => _body.TrySetResult(0);

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
