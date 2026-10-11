using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Management;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class SubscriptionResourceTests
{
    [Test]
    public async Task Cancelling_an_ordinary_shape_request_returns_before_its_shared_worker_finishes_cleanup()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var storage = new UsageCommitHook();
        var serverCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            storage.Install(services);
            var registration = services.Single(service => service.ServiceType == typeof(IShapeService));
            services.Remove(registration);
            services.AddSingleton<ShapeService>();
            services.AddSingleton<IShapeService>(provider => new CancellationObservedShapes(
                provider.GetRequiredService<ShapeService>(), serverCancelled));
        }, time: clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var database = await api.CreateManagedDatabaseAsync("db" + new Faker().Random.AlphaNumeric(8).ToLowerInvariant());
        using var data = await api.CreateClientAsync(database.Organization, ApiKeyScopes.DocumentsWrite);
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        using var shapes = await api.CreateClientAsync(database.Organization, ApiKeyScopes.ShapesRead);
        var route = ManagementApi.Uri($"{database.Data}/collections/items/shape?offset=-1");

        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long waiting = 0;
        long flights = 0;
        long reads = 0;
        var requestWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name is
                "terrabase.shape.requests.waiting" or "terrabase.shape.flights.active" or "terrabase.shape.reads")
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case "terrabase.shape.requests.waiting":
                    if (Interlocked.Add(ref waiting, value) == 1 && value == 1)
                    {
                        requestWaiting.TrySetResult();
                    }
                    break;
                case "terrabase.shape.flights.active":
                    if (Interlocked.Add(ref flights, value) == 0 && value == -1)
                    {
                        workerStopped.TrySetResult();
                    }
                    break;
                case "terrabase.shape.reads":
                    Interlocked.Add(ref reads, value);
                    break;
            }
        });
        listener.Start();

        using var worker = new UsageCommitHook.Gate(fail: false);
        using var cleanup = new UsageCommitHook.Gate(fail: false);
        var workerToken = CancellationToken.None;
        storage.BeforeBegin = async (tenant, cancellationToken) =>
        {
            if (tenant.DatabaseId != database.Id)
            {
                return;
            }

            workerToken = cancellationToken;
            try
            {
                await worker.WaitAsync(cancellationToken);
            }
            finally
            {
                // Cleanup ignores the worker's cancellation, but remains bounded by the test deadline.
                await cleanup.WaitAsync(deadline.Token);
            }
        };
        using var requester = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var ordinary = shapes.GetAsync(route, requester.Token);
        try
        {
            await worker.Entered.Task.WaitAsync(deadline.Token);
            await requestWaiting.Task.WaitAsync(deadline.Token);
            await requester.CancelAsync();
            await cleanup.Entered.Task.WaitAsync(deadline.Token);

            // HttpClient cancels independently of the server. Observe the actual server ReadAsync throwing instead.
            await Assert.That(await serverCancelled.Task.WaitAsync(deadline.Token)).IsFalse();
            await Assert.ThrowsAsync<TaskCanceledException>(() => ordinary.WaitAsync(deadline.Token));
            await Assert.That(workerToken.IsCancellationRequested).IsTrue();
            await Assert.That(Volatile.Read(ref waiting)).IsEqualTo(0);
            await Assert.That(Volatile.Read(ref flights)).IsEqualTo(1);
            await Assert.That(Volatile.Read(ref reads)).IsEqualTo(1);
            await Assert.That(workerStopped.Task.IsCompleted).IsFalse();
        }
        finally
        {
            cleanup.Dispose();
            worker.Dispose();
            storage.BeforeBegin = null;
            await requester.CancelAsync();
        }

        await workerStopped.Task.WaitAsync(deadline.Token);
        // Storage tasks complete before their pool workers decrement the running gauge.
        while (engine.Io.Admitted != 0 || engine.Io.Running != 0)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        await Assert.That(Volatile.Read(ref flights)).IsEqualTo(0);
        await Assert.That(Volatile.Read(ref waiting)).IsEqualTo(0);
        await Assert.That(engine.Io.Admitted).IsEqualTo(0);
        await Assert.That(engine.Io.Running).IsEqualTo(0);
    }

    [Test]
    public async Task Expiring_a_subscription_retains_its_budget_until_the_shared_shape_worker_stops()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var storage = new UsageCommitHook();
        var cancelledRead = new TaskCompletionSource<(bool Cancelled, bool Completed)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            storage.Install(services);
            var registration = services.Single(service => service.ServiceType == typeof(IShapeService));
            services.Remove(registration);
            services.AddSingleton<ShapeService>();
            services.AddSingleton<IShapeService>(provider => new ObservedShapes(
                provider.GetRequiredService<ShapeService>(), cancelledRead));
        }, time: clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var database = await api.CreateManagedDatabaseAsync("db" + new Faker().Random.AlphaNumeric(8).ToLowerInvariant());
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var data = await api.CreateClientAsync(database.Organization, ApiKeyScopes.DocumentsWrite);
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        using var shapes = await api.CreateClientAsync(database.Organization, ApiKeyScopes.ShapesRead);
        // Authenticate once so the ordinary client's credential cache is part of the memory baseline.
        using (var warm = await shapes.GetAsync(ManagementApi.Uri($"{database.Data}/collections/items/shape?offset=-1"), deadline.Token))
        {
            await Assert.That(warm.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        }
        using var subscriber = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = ApiKeyScopes.ShapesRead;
            token.Expires = clock.GetUtcNow().AddMinutes(1).UtcDateTime;
        }));

        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long reserved = 0;
        long waiting = 0;
        long flights = 0;
        long reads = 0;
        var ordinaryWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name is
                "terrabase.memory.reserved" or "terrabase.shape.requests.waiting" or "terrabase.shape.flights.active" or "terrabase.shape.reads")
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case "terrabase.memory.reserved":
                    Interlocked.Exchange(ref reserved, value - api.Server.Services.GetRequiredService<UsageMeter>().ReservedMeasurementBytes);
                    break;
                case "terrabase.shape.requests.waiting":
                    var count = Interlocked.Add(ref waiting, value);
                    if (count == 1 && value == 1)
                    {
                        ordinaryWaiting.TrySetResult();
                    }
                    else if (count == 2 && value == 1)
                    {
                        // Measurement callbacks run inline after joining the flight, before WaitAsync.
                        // Expiring here makes cancellation cleanup run before ReadAsync returns its ValueTask.
                        clock.Advance(TimeSpan.FromMinutes(2));
                    }
                    break;
                case "terrabase.shape.flights.active":
                    Interlocked.Add(ref flights, value);
                    break;
                case "terrabase.shape.reads":
                    Interlocked.Add(ref reads, value);
                    break;
            }
        });
        listener.Start();
        api.Server.Services.GetRequiredService<UsageMeter>().ObserveMemory(listener.RecordObservableInstruments);
        var baseline = Volatile.Read(ref reserved);

        using var held = new UsageCommitHook.Gate(fail: false);
        var workerToken = CancellationToken.None;
        storage.BeforeBegin = (tenant, cancellationToken) =>
        {
            if (tenant.DatabaseId != database.Id)
            {
                return ValueTask.CompletedTask;
            }

            workerToken = cancellationToken;
            return held.WaitAsync(cancellationToken);
        };
        var ordinary = shapes.GetAsync(ManagementApi.Uri($"{database.Data}/collections/items/shape?offset=-1"), deadline.Token);
        try
        {
            await held.Entered.Task.WaitAsync(deadline.Token);
            await ordinaryWaiting.Task.WaitAsync(deadline.Token);
            var subscription = subscriber.GetAsync(ManagementApi.Uri(
                $"/v1/projects/{database.Project}/databases/{database.Name}/collections/items/subscribe"),
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var (cancelled, completed) = await cancelledRead.Task.WaitAsync(deadline.Token);
            await Assert.That(cancelled).IsTrue();
            await Assert.That(completed).IsFalse();
            api.Server.Services.GetRequiredService<UsageMeter>().ObserveMemory(listener.RecordObservableInstruments);
            await Assert.That(Volatile.Read(ref reserved) - baseline).IsGreaterThanOrEqualTo(32 * 1024 * 1024);
            await Assert.That(Volatile.Read(ref flights)).IsEqualTo(1);
            await Assert.That(Volatile.Read(ref waiting)).IsEqualTo(2);
            await Assert.That(Volatile.Read(ref reads)).IsEqualTo(1);
            await Assert.That(workerToken.IsCancellationRequested).IsFalse();
            await Assert.That(ordinary.IsCompleted).IsFalse();
            await Assert.That(subscription.IsCompleted).IsFalse();

            held.Dispose();
            using var shapeResponse = await ordinary.WaitAsync(deadline.Token);
            await Assert.That(shapeResponse.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
            using var subscriptionResponse = await subscription.WaitAsync(deadline.Token);
            await Assert.That(subscriptionResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await subscriptionResponse.Content.ReadAsStringAsync(deadline.Token)).IsEqualTo("");
            api.Server.Services.GetRequiredService<UsageMeter>().ObserveMemory(listener.RecordObservableInstruments);
            await Assert.That(Volatile.Read(ref reserved)).IsEqualTo(baseline);
            await Assert.That(Volatile.Read(ref flights)).IsEqualTo(0);
            await Assert.That(Volatile.Read(ref waiting)).IsEqualTo(0);
        }
        finally
        {
            held.Dispose();
            storage.BeforeBegin = null;
            await deadline.CancelAsync();
        }
    }

    [Test]
    public async Task Storage_saturation_before_start_returns_503_with_retry_after_and_retry_succeeds_after_release()
    {
        var storage = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "1";
        }, services: storage.Install);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var database = await api.CreateManagedDatabaseAsync("db" + new Faker().Random.AlphaNumeric(8).ToLowerInvariant());
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var data = await api.CreateClientAsync(database.Organization, ApiKeyScopes.DocumentsWrite);
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        var found = await api.Server.Services.GetRequiredService<ITenantDirectory>()
            .FindDatabaseAsync(database.Organization, database.Project, database.Name, deadline.Token);
        var context = found is TenantContext resolved ? resolved : throw new InvalidOperationException("the seeded database must resolve");
        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(context!, deadline.Token))!;
        using var subscriber = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = ApiKeyScopes.ShapesRead;
        }));
        using var beforeRead = new UsageCommitHook.Gate(fail: false);
        storage.BeforeBegin = (tenant, cancellationToken) => tenant.DatabaseId == database.Id
            ? beforeRead.WaitAsync(cancellationToken)
            : ValueTask.CompletedTask;
        var subscription = subscriber.GetAsync(ManagementApi.Uri(
            $"/v1/projects/{database.Project}/databases/{database.Name}/collections/items/subscribe"), deadline.Token);
        try
        {
            await beforeRead.Entered.Task.WaitAsync(deadline.Token);
            // Route and tenant lookup have finished; saturate the first real shape read before SSE headers.
            await using var held = await store.BeginTransactionAsync(write: false, deadline.Token);
            beforeRead.Dispose();
            using var refused = await subscription.WaitAsync(deadline.Token);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(refused.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            using var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(deadline.Token));
            await Assert.That(error.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("storage_busy");
        }
        finally
        {
            beforeRead.Dispose();
            storage.BeforeBegin = null;
        }

        using var admitted = await subscriber.GetAsync(ManagementApi.Uri(
            $"/v1/projects/{database.Project}/databases/{database.Name}/collections/items/subscribe"),
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(admitted.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");
    }

    [Test]
    public async Task Storage_saturation_after_start_emits_a_terminal_error_and_returns_resources()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["storage-threads"] = "1";
            settings["storage-queue"] = "1";
        }, time: clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var database = await api.CreateManagedDatabaseAsync("db" + new Faker().Random.AlphaNumeric(8).ToLowerInvariant());
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var data = await api.CreateClientAsync(database.Organization, ApiKeyScopes.DocumentsWrite);
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        var found = await api.Server.Services.GetRequiredService<ITenantDirectory>()
            .FindDatabaseAsync(database.Organization, database.Project, database.Name, deadline.Token);
        var context = found is TenantContext resolved ? resolved : throw new InvalidOperationException("the seeded database must resolve");
        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(context!, deadline.Token))!;

        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long reserved = 0;
        long flights = 0;
        long waiting = 0;
        var liveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name is
                "terrabase.memory.reserved" or "terrabase.shape.flights.active" or "terrabase.shape.requests.waiting" or "terrabase.shape.live.waits")
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case "terrabase.memory.reserved":
                    Interlocked.Exchange(ref reserved, value - api.Server.Services.GetRequiredService<UsageMeter>().ReservedMeasurementBytes);
                    break;
                case "terrabase.shape.flights.active":
                    Interlocked.Add(ref flights, value);
                    break;
                case "terrabase.shape.requests.waiting":
                    Interlocked.Add(ref waiting, value);
                    break;
                case "terrabase.shape.live.waits" when value == 1:
                    liveEntered.TrySetResult();
                    break;
            }
        });
        listener.Start();
        api.Server.Services.GetRequiredService<UsageMeter>().ObserveMemory(listener.RecordObservableInstruments);
        var baseline = Volatile.Read(ref reserved);
        using var client = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = ApiKeyScopes.ShapesRead;
            token.Expires = clock.GetUtcNow().AddMinutes(5).UtcDateTime;
        }));
        using var response = await client.GetAsync(ManagementApi.Uri(
            $"/v1/projects/{database.Project}/databases/{database.Name}/collections/items/subscribe"),
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var upToDate = false;
        while (!upToDate)
        {
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsEqualTo("event: shape");
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    using var page = JsonDocument.Parse(line[6..]);
                    upToDate = page.RootElement.GetProperty("up_to_date").GetBoolean();
                }
            }
        }

        await liveEntered.Task.WaitAsync(deadline.Token);
        api.Server.Services.GetRequiredService<UsageMeter>().ObserveMemory(listener.RecordObservableInstruments);
        await Assert.That(Volatile.Read(ref reserved) - baseline).IsGreaterThanOrEqualTo(32 * 1024 * 1024);

        // The subscription has sent its snapshot and released its storage transaction. Holding the sole
        // admission forces the real live-read fallback to fail after its 20-second deadline.
        await using (var held = await store.BeginTransactionAsync(write: false, deadline.Token))
        {
            clock.Advance(TimeSpan.FromSeconds(21));
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsEqualTo("event: error");
            var line = await reader.ReadLineAsync(deadline.Token);
            await Assert.That(line).StartsWith("data: ");
            using var error = JsonDocument.Parse(line![6..]);
            await Assert.That(error.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("storage_busy");
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsEqualTo("");
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsNull();
        }

        // Advancing the clock also triggers the usage flusher; await that catalog work before checking storage cleanup.
        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(deadline.Token);

        // A pool call completes its task before its worker decrements the running gauge.
        while (engine.Io.Running != 0)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        api.Server.Services.GetRequiredService<UsageMeter>().ObserveMemory(listener.RecordObservableInstruments);
        await Assert.That(Volatile.Read(ref reserved)).IsEqualTo(baseline);
        await Assert.That(Volatile.Read(ref flights)).IsEqualTo(0);
        await Assert.That(Volatile.Read(ref waiting)).IsEqualTo(0);
        await Assert.That(engine.Io.Admitted).IsEqualTo(0);
        await Assert.That(engine.Io.Running).IsEqualTo(0);
    }

    private sealed class CancellationObservedShapes(IShapeService inner, TaskCompletionSource<bool> cancelled) : IShapeService
    {
        public async ValueTask<ShapeReadResult> ReadAsync(ShapeRequest request, CancellationToken cancellationToken, bool drainOnCancellation = false)
        {
            try
            {
                return await inner.ReadAsync(request, cancellationToken, drainOnCancellation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult(drainOnCancellation);
                throw;
            }
        }
    }

    private sealed class ObservedShapes(IShapeService inner, TaskCompletionSource<(bool Cancelled, bool Completed)> entered) : IShapeService
    {
        public ValueTask<ShapeReadResult> ReadAsync(ShapeRequest request, CancellationToken cancellationToken, bool drainOnCancellation = false)
        {
            var read = inner.ReadAsync(request, cancellationToken, drainOnCancellation);
            if (drainOnCancellation)
            {
                entered.TrySetResult((cancellationToken.IsCancellationRequested, read.IsCompleted));
            }

            return read;
        }
    }
}
