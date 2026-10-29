using static TerraBase.Testing.StoragePoolHelpers;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Storage;

public sealed partial class StoragePoolTests
{
    [Test]
    public async Task Closed_engine_reports_unavailable_storage_admission()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        await engine.DisposeAsync();
        await Assert.That(engine.IoAdmittedCount).IsNull();
        using var readiness = await server.Client.GetAsync(new Uri("/readyz", UriKind.Relative));
        await Assert.That(readiness.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_refused_creation_intent_leaves_no_artifacts_or_reservation(bool copy)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var management = await ManagementApi.StartAsync();
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        var source = await tenant.ContextAsync(management.Server, "source", token);
        var target = await tenant.ContextAsync(management.Server, "target", token);
        var engine = management.Server.Services.GetRequiredService<IStorageEngine>();
        await Assert.That(await engine.DeleteDatabaseAsync(target, token)).IsTrue();
        var directory = Path.Combine(management.Server.DataDirectory, tenant.OrganizationId, tenant.ProjectId);
        var before = Directory.GetFiles(directory);
        var intents = 0;
        await Assert.That(await engine.CreateDatabaseWithIntentAsync(target, copy ? source : null, _ =>
        {
            intents++;
            return ValueTask.FromResult(false);
        }, token)).IsFalse();
        await Assert.That(intents).IsEqualTo(1);
        await Assert.That(Directory.GetFiles(directory)).IsEquivalentTo(before);
        await Assert.That(await engine.OpenDatabaseAsync(target, token)).IsNull();

        await Assert.That(await engine.CreateDatabaseWithIntentAsync(target, copy ? source : null, _ =>
        {
            intents++;
            return ValueTask.FromResult(true);
        }, token)).IsTrue();
        await Assert.That(intents).IsEqualTo(2);
        await engine.CompleteDatabaseIntentAsync(target, token);
        await Assert.That(await engine.OpenDatabaseAsync(target, token)).IsNotNull();
        using var restored = await tenant.Data.GetAsync(tenant.Database("target") + "/collections", token);
        restored.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Lifecycle_intents_reuse_the_maintenance_slot_with_a_one_slot_queue()
    {
        await using var management = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "1";
        });
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        using var owner = management.As(tenant.User);
        var source = await management.CreateDatabaseAsync(tenant.OrganizationId, tenant.ProjectId, "source", owner);
        await StorageFixture.CreateCollectionAsync(tenant, "source", "items", TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var databases = $"/v1/management/organizations/{tenant.OrganizationId}/projects/{tenant.ProjectId}/databases";
        using var copied = await owner.PostAsJsonAsync(new Uri(databases + "/" + source + "/branches", UriKind.Relative), new { name = "copy" });
        copied.EnsureSuccessStatusCode();
        var copy = (await copied.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString();
        using var deletedCopy = await owner.DeleteAsync(new Uri(databases + "/" + copy, UriKind.Relative));
        deletedCopy.EnsureSuccessStatusCode();
        using var deletedSource = await owner.DeleteAsync(new Uri(databases + "/" + source, UriKind.Relative));
        deletedSource.EnsureSuccessStatusCode();
        var engine = management.Server.Services.GetRequiredService<TursoStorageEngine>();
        await Assert.That(engine.Io.Admitted).IsEqualTo(0);
        using var health = await management.Server.Client.GetAsync(new Uri("/readyz", UriKind.Relative));
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Maintenance_waiters_count_toward_admission_and_cancellation_releases_their_slot(int capacity)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken, deadline.Token);
        using var release = new ManualResetEventSlim();
        await using var management = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = capacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }, time: new FakeTimeProvider(DateTimeOffset.UtcNow));
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        var source = await tenant.ContextAsync(management.Server, "source", deadline.Token);
        await StorageFixture.CreateCollectionAsync(tenant, "source", "items", deadline.Token);
        var server = management.Server;
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var pressure = server.Services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenDatabaseAsync(source, deadline.Token);
        var system = await engine.OpenSystemDatabaseAsync(deadline.Token);
        var target = source with { DatabaseId = EntityIds.New().ToString() };
        var cold = source with { DatabaseId = EntityIds.New().ToString() };
        // An unopened file, as an earlier server left it, exercises first-open bootstrap behind the gate.
        await DocumentFiles.WriteAsync(Path.Combine(server.DataDirectory, cold.OrganizationId, cold.ProjectId!, cold.DatabaseId + ".db"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerAdmissionHeld = false;
        var pending = new List<Task>();
        var responses = new List<Task<HttpResponseMessage>>();
        Exception? primaryFailure = null;
        try
        {
            // A counted fixture admission holds the sole worker, leaving one or two maintenance slots.
            engine.Io.Admit();
            blockerAdmissionHeld = true;
            var worker = engine.Io.RunAsync(() =>
            {
                entered.SetResult();
                release.Wait();
            }, CancellationToken.None);
            pending.Add(worker);
            await entered.Task.WaitAsync(deadline.Token);
            var copy = engine.CopyDatabaseAsync(source, target, deadline.Token).AsTask();
            pending.Add(copy);
            await WaitUntilAsync(() => engine.Io.Admitted == 2 && engine.Io.Queued == 1, deadline.Token);
            await Assert.That(pressure.IoAdmittedCount).IsEqualTo(2);

            Task? cancelled = null;
            if (capacity == 3)
            {
                var waiter = engine.CreateDatabaseAsync(cold, cancellation.Token).AsTask();
                cancelled = AssertFailureAsync<OperationCanceledException>(() => waiter);
                pending.Add(cancelled);
            }
            await Assert.That(engine.Io.Admitted).IsEqualTo(capacity);
            await Assert.That(pressure.IoAdmittedCount).IsEqualTo(capacity);
            Func<CancellationToken, Task>[] operations =
            [
                token => engine.CreateDatabaseAsync(cold, token).AsTask(),
                token => engine.CopyDatabaseAsync(source, cold, token).AsTask(),
                token => engine.DeleteDatabaseAsync(cold, token).AsTask(),
                token => engine.OpenDatabaseAsync(cold, token).AsTask(),
                token => engine.CreateDatabaseWithIntentAsync(cold, null, _ => ValueTask.FromResult(true), token).AsTask(),
                token => engine.RecordDatabaseDeleteIntentAsync(cold, _ => ValueTask.FromResult(true), token).AsTask(),
                token => engine.CompleteDatabaseIntentAsync(cold, token).AsTask(),
            ];
            foreach (var operation in operations)
            {
                var refused = AssertFailureAsync<StorageBusyException>(() => operation(deadline.Token));
                pending.Add(refused);
                await refused.WaitAsync(deadline.Token);
                await Assert.That(engine.Io.Admitted).IsEqualTo(capacity);
            }
            await Assert.That(ReferenceEquals(await engine.OpenDatabaseAsync(source, deadline.Token), store)).IsTrue();
            await Assert.That(ReferenceEquals(await engine.OpenSystemDatabaseAsync(deadline.Token), system)).IsTrue();
            await Assert.That(engine.Io.Admitted).IsEqualTo(capacity);

            var request = tenant.Data.PutAsJsonAsync(new Uri(
                $"/v1/projects/{tenant.ProjectId}/databases/source/collections/items/documents/by-key?id={StorageFixture.Id("busy")}", UriKind.Relative), new { n = 1 }, deadline.Token);
            responses.Add(request);
            using var busy = await request;
            await Assert.That(busy.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(busy.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            if (cancelled is not null)
            {
                cancellation.Cancel();
                await cancelled.WaitAsync(deadline.Token);
                await Assert.That(engine.Io.Admitted).IsEqualTo(2);
                await Assert.That(pressure.IoAdmittedCount).IsEqualTo(2);
                await Assert.That(copy.IsCompleted).IsFalse();
                await Assert.That(engine.Io.Queued).IsEqualTo(1);
            }

            release.Set();
            await worker.WaitAsync(deadline.Token);
            engine.Io.Release();
            blockerAdmissionHeld = false;
            await Assert.That(await copy.WaitAsync(deadline.Token)).IsTrue();
            await Assert.That(engine.Io.Admitted).IsEqualTo(0);
            await Assert.That(pressure.IoAdmittedCount).IsEqualTo(0);
            using var healthy = await tenant.Data.PutAsJsonAsync(new Uri(
                $"/v1/projects/{tenant.ProjectId}/databases/source/collections/items/documents/by-key?id={StorageFixture.Id("healthy")}", UriKind.Relative), new { n = 2 }, deadline.Token);
            healthy.EnsureSuccessStatusCode();
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            release.Set();
            try
            {
                await JoinCleanupAsync(pending.Concat(responses.Select(DisposeResponseAsync))
                    .Append(cancellation.CancelAsync()).Append(deadline.CancelAsync()), primaryFailure);
            }
            finally
            {
                if (blockerAdmissionHeld)
                {
                    engine.Io.Release();
                }
            }
        }

        static async Task AssertFailureAsync<T>(Func<Task> operation) where T : Exception
        {
            await Assert.ThrowsAsync<T>(operation);
        }
    }

    [Test]
    public async Task A_saturated_storage_pool_answers_503_and_recovers()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        const int Rejected = 4;
        // One Turso thread and two admitted transactions. Freeze periodic catalog work so only the test takes slots.
        await using var management = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "2";
        }, StorageTransactionFixture.ConfigurePool, time: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var server = management.Server;
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        await management.SetPlanAsync(tenant.User, tenant.OrganizationId, "enterprise");
        var client = tenant.Data;
        using var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/busy", UriKind.Relative), null, deadline.Token);
        created.EnsureSuccessStatusCode();
        await StorageFixture.CreateCollectionAsync(tenant, "busy", "items", deadline.Token);
        var document = new Uri($"/v1/projects/{tenant.ProjectId}/databases/busy/collections/items/documents/by-key?id={StorageFixture.Id("a")}", UriKind.Relative);
        using var written = await client.PutAsJsonAsync(document, new { n = 1 }, deadline.Token);
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var io = engine.Io;

        var requests = new List<Task<HttpResponseMessage>>();
        Exception? primaryFailure = null;
        try
        {
            Task<HttpResponseMessage> queued;
            HttpResponseMessage[] rejected;
            // Release the held worker before joining requests, including assertion-failure cleanup.
            await using (await StorageFixture.HoldWorkerAsync(engine, deadline.Token))
            {
                await WaitUntilAsync(() => io.Admitted == 1 && io.Running == 1, deadline.Token);
                queued = client.GetAsync(document, deadline.Token);
                requests.Add(queued);
                await WaitUntilAsync(() => io.Admitted == 2, deadline.Token);
                var extra = Enumerable.Range(0, Rejected).Select(_ => client.GetAsync(document, deadline.Token)).ToList();
                requests.AddRange(extra);
                rejected = await Task.WhenAll(extra).WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            }

            using var read = await queued.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await Assert.That(rejected.Count(response => response.StatusCode == HttpStatusCode.ServiceUnavailable)).IsEqualTo(Rejected);
            await Assert.That(rejected.Count(response => response.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(1))).IsEqualTo(Rejected);
            await Assert.That(read.StatusCode).IsNotEqualTo(HttpStatusCode.ServiceUnavailable);
            using var after = await client.GetAsync(document, deadline.Token);
            await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(io.Admitted).IsEqualTo(0);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            // Join every request after the held worker is released, even if cancellation won.
            await JoinCleanupAsync(requests.Select(DisposeResponseAsync)
                .Append(deadline.CancelAsync()), primaryFailure);
        }
    }

    [Test]
    public async Task Evicting_an_idle_connection_closes_it_on_a_storage_thread_and_keeps_its_data()
    {
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        // Keep periodic catalog writes from returning a session between the fixture's controlled evictions.
        await using var management = await ManagementApi.StartAsync(
            services: StorageTransactionFixture.ConfigurePool, time: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var server = management.Server;
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        var client = tenant.Data;
        using var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/victim", UriKind.Relative), null, operationTimeout.Token);
        created.EnsureSuccessStatusCode();
        await StorageFixture.CreateCollectionAsync(tenant, "victim", "items", operationTimeout.Token);
        using var written = await client.PutAsJsonAsync(
            new Uri($"/v1/projects/{tenant.ProjectId}/databases/victim/collections/items/documents/by-key?id={StorageFixture.Id("a")}", UriKind.Relative), new { padding = new string('x', 128 * 1024) }, operationTimeout.Token);
        written.EnsureSuccessStatusCode();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var victim = await tenant.ContextAsync(management.Server, "victim", operationTimeout.Token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(victim, operationTimeout.Token))!;
        var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = engine.Pool.Rent(store)!;
        session.Connection.StateChange += (_, change) =>
        {
            if (change.CurrentState == System.Data.ConnectionState.Closed)
            {
                closed.TrySetResult(Thread.CurrentThread.Name);
            }
        };
        // Observe the existing session, then put it back without opening or closing anything.
        engine.Pool.Return(store, session);
        for (var i = 0; i < engine.Pool.MaxIdle; i++)
        {
            using var next = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/eviction-{i}", UriKind.Relative), null, operationTimeout.Token);
            next.EnsureSuccessStatusCode();
        }

        await Assert.That(await closed.Task.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken)).IsEqualTo("terrabase-turso");
        // The data is available again through the real listener after eviction.
        using var read = await client.GetAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/victim/collections/items/documents/by-key?id={StorageFixture.Id("a")}", UriKind.Relative), operationTimeout.Token);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Cancelled_queued_requests_release_admissions_and_recover_while_a_worker_is_blocked()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await using var management = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "2";
        }, StorageTransactionFixture.ConfigurePool, time: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var server = management.Server;
        using var tenant = await management.CreateTenantAsync("documents:read", "documents:write");
        await management.SetPlanAsync(tenant.User, tenant.OrganizationId, "enterprise");
        var client = tenant.Data;
        using var created = await client.PutAsync(new Uri($"/v1/projects/{tenant.ProjectId}/databases/busy", UriKind.Relative), null, deadline.Token);
        created.EnsureSuccessStatusCode();
        await StorageFixture.CreateCollectionAsync(tenant, "busy", "items", deadline.Token);
        var document = new Uri($"/v1/projects/{tenant.ProjectId}/databases/busy/collections/items/documents/by-key?id={StorageFixture.Id("a")}", UriKind.Relative);
        using var written = await client.PutAsJsonAsync(document, new { n = 1 }, deadline.Token);
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var io = engine.Io;
        var requests = new List<Task<HttpResponseMessage>>();
        Exception? primaryFailure = null;
        try
        {
            Task<HttpResponseMessage> recovered;
            // Release the held worker before joining requests, including assertion-failure cleanup.
            await using (await StorageFixture.HoldWorkerAsync(engine, deadline.Token))
            {
                await WaitUntilAsync(() => io.Admitted == 1 && io.Running == 1, deadline.Token);
                for (var i = 0; i < 8; i++)
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    var read = client.GetAsync(document, cancellation.Token);
                    requests.Add(read);
                    try
                    {
                        await WaitUntilAsync(() => io.Admitted == 2 && io.Queued == 1, deadline.Token);
                        await cancellation.CancelAsync();
                        await Assert.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken));
                        await WaitUntilAsync(() => io.Admitted == 1 && io.Queued == 0, deadline.Token);
                        await Assert.That(io.Running).IsEqualTo(1);
                    }
                    finally
                    {
                        cancellation.Cancel();
                    }
                }

                recovered = client.GetAsync(document, deadline.Token);
                requests.Add(recovered);
                await WaitUntilAsync(() => io.Admitted == 2 && io.Queued == 1, deadline.Token);
            }

            using var healthy = await recovered.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await Assert.That(healthy.StatusCode).IsNotEqualTo(HttpStatusCode.ServiceUnavailable);
            using var after = await client.GetAsync(document, deadline.Token);
            await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(io.Admitted).IsEqualTo(0);
            await Assert.That(io.Queued).IsEqualTo(0);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            // Join every request after the held worker is released, even if cancellation won.
            await JoinCleanupAsync(requests.Select(DisposeResponseAsync)
                .Append(deadline.CancelAsync()), primaryFailure);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

}
