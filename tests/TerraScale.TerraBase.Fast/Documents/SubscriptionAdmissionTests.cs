using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class SubscriptionAdmissionTests
{
    [Test]
    public async Task The_129th_connection_is_refused_and_disconnecting_all_128_returns_admission_and_memory()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var writes = new BlockedWrites(128);
        // This is a logical reservation budget, not an allocation: each real snapshot contains one small document.
        await using var api = await ManagementApi.StartAsync(
            settings => settings["memory-budget"] = "17179869184", services: writes.Install, time: clock);
        using var deadline = Deadline();
        using var resources = new SubscriptionResources(api);
        var (database, text) = await SeedAsync(api, deadline.Token);
        await resources.WaitForCleanupAsync(null, deadline.Token);
        var baseline = resources.Reserved;
        using var client = Subscriber(api, database, clock);
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        // Open the bounded set together instead of spending the deadline on 128 serial round trips.
        var opening = Enumerable.Range(0, 128)
            .Select(_ => SubscribeAsync(client, database, block: true, deadline.Token)).ToArray();
        try
        {
            var connections = await Task.WhenAll(opening);
            foreach (var response in connections)
            {
                await AssertStreamAsync(response);
                await Assert.That(response.Version).IsEqualTo(HttpVersion.Version20);
            }

            await writes.AllEntered.Task.WaitAsync(deadline.Token);
            await Assert.That(resources.Active).IsEqualTo(128);
            await Assert.That(resources.Reserved - baseline).IsGreaterThanOrEqualTo(128L * 32 * 1024 * 1024);
            var held = resources.Reserved;
            using (var refused = await SubscribeAsync(client, database, block: false, deadline.Token))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
                using var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(deadline.Token));
                await Assert.That(error.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("subscription_busy");
            }

            await Assert.That(resources.Active).IsEqualTo(128);
            await Assert.That(resources.Reserved).IsEqualTo(held);
            foreach (var response in connections)
            {
                response.Dispose();
            }

            await writes.AllReturned.Task.WaitAsync(deadline.Token);
            await resources.WaitForCleanupAsync(baseline, deadline.Token);
            await Assert.That(writes.Cancelled).IsEqualTo(128);
            await resources.AssertReleasedAsync(baseline);

            using (var admitted = await SubscribeAsync(client, database, block: false, deadline.Token))
            {
                await AssertSnapshotAsync(admitted, text, deadline.Token);
                await Assert.That(resources.Active).IsEqualTo(1);
            }

            await resources.WaitForCleanupAsync(baseline, deadline.Token);
            await resources.AssertReleasedAsync(baseline);
        }
        finally
        {
            foreach (var task in opening.Where(task => task.IsCompletedSuccessfully))
            {
                (await task).Dispose();
            }
        }
    }

    [Test]
    public async Task A_write_blocked_past_ten_seconds_aborts_and_returns_its_page_and_connection_admission()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var writes = new BlockedWrites(1);
        await using var api = await ManagementApi.StartAsync(services: writes.Install, time: clock);
        using var deadline = Deadline();
        using var resources = new SubscriptionResources(api);
        var (database, text) = await SeedAsync(api, deadline.Token);
        await resources.WaitForCleanupAsync(null, deadline.Token);
        var baseline = resources.Reserved;
        using var client = Subscriber(api, database, clock);
        using var blocked = await SubscribeAsync(client, database, block: true, deadline.Token);
        await AssertStreamAsync(blocked);
        await writes.AllEntered.Task.WaitAsync(deadline.Token);
        using var reader = new StreamReader(await blocked.Content.ReadAsStreamAsync(deadline.Token));
        await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsEqualTo("event: shape");
        await Assert.That(await reader.ReadLineAsync(deadline.Token)).StartsWith("id: ");
        var body = reader.ReadLineAsync(deadline.Token).AsTask();
        await Assert.That(body.IsCompleted).IsFalse();
        await Assert.That(resources.Active).IsEqualTo(1);
        await Assert.That(resources.Reserved - baseline).IsGreaterThanOrEqualTo(32 * 1024 * 1024);
        var held = resources.Reserved;

        clock.Advance(TimeSpan.FromSeconds(9));
        await Assert.That(writes.Cancelled).IsEqualTo(0);
        await Assert.That(body.IsCompleted).IsFalse();
        await Assert.That(resources.Active).IsEqualTo(1);
        await Assert.That(resources.Reserved).IsEqualTo(held);

        clock.Advance(TimeSpan.FromSeconds(2));
        // The partial SSE frame must end with a transport error, not a successful EOF or token expiration.
        await Assert.That(async () => await body).Throws<IOException>();
        await writes.AllReturned.Task.WaitAsync(deadline.Token);
        // Advancing fake time also starts the usage flusher; include its real catalog work in cleanup.
        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(deadline.Token);
        await resources.WaitForCleanupAsync(baseline, deadline.Token);
        await Assert.That(writes.Cancelled).IsEqualTo(1);
        await resources.AssertReleasedAsync(baseline);

        using (var admitted = await SubscribeAsync(client, database, block: false, deadline.Token))
        {
            await AssertSnapshotAsync(admitted, text, deadline.Token);
            await Assert.That(resources.Active).IsEqualTo(1);
        }

        await resources.WaitForCleanupAsync(baseline, deadline.Token);
        await resources.AssertReleasedAsync(baseline);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Expiration_or_application_stopping_during_a_partial_event_aborts_and_returns_admission(bool stopApplication)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var writes = new BlockedWrites(1);
        var shutdown = new HeldShutdown();
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            writes.Install(services);
            if (stopApplication)
            {
                // Only the finally release may let Kestrel teardown reach the blocked response.
                services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(90));
                services.AddHostedService(_ => shutdown);
            }
        }, time: clock);
        try
        {
            using var deadline = Deadline();
            using var resources = new SubscriptionResources(api);
            var (database, text) = await SeedAsync(api, deadline.Token);
            // The issuer and authentication use real UTC; setup must not age the short signed token.
            clock.SetUtcNow(DateTimeOffset.UtcNow);
            await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(deadline.Token);
            await resources.WaitForCleanupAsync(null, deadline.Token);
            var baseline = resources.Reserved;
            using var client = Subscriber(api, database, clock, TimeSpan.FromSeconds(5));
            using var blocked = await SubscribeAsync(client, database, block: true, deadline.Token);
            await AssertStreamAsync(blocked);
            await writes.AllEntered.Task.WaitAsync(deadline.Token);
            using var reader = new StreamReader(await blocked.Content.ReadAsStreamAsync(deadline.Token), leaveOpen: true);
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).IsEqualTo("event: shape");
            await Assert.That(await reader.ReadLineAsync(deadline.Token)).StartsWith("id: ");
            var body = reader.ReadLineAsync(deadline.Token).AsTask();
            await Assert.That(body.IsCompleted).IsFalse();
            await Assert.That(resources.Active).IsEqualTo(1);
            await Assert.That(resources.Reserved - baseline).IsGreaterThanOrEqualTo(32 * 1024 * 1024);
            await Assert.That(writes.Cancelled).IsEqualTo(0);

            if (stopApplication)
            {
                api.Server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                await shutdown.Stopping.Task.WaitAsync(deadline.Token);
            }
            else
            {
                // Expire the signed token before the independent ten-second send timeout.
                clock.Advance(TimeSpan.FromSeconds(6));
            }

            await Assert.That(async () => await body).Throws<IOException>();
            await writes.AllReturned.Task.WaitAsync(deadline.Token);
            await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(deadline.Token);
            await resources.WaitForCleanupAsync(baseline, deadline.Token);
            await Assert.That(writes.Cancelled).IsEqualTo(1);
            await resources.AssertReleasedAsync(baseline);

            if (!stopApplication)
            {
                using var fresh = Subscriber(api, database, clock);
                using (var admitted = await SubscribeAsync(fresh, database, block: false, deadline.Token))
                {
                    await AssertSnapshotAsync(admitted, text, deadline.Token);
                    await Assert.That(resources.Active).IsEqualTo(1);
                }

                await resources.WaitForCleanupAsync(baseline, deadline.Token);
                await resources.AssertReleasedAsync(baseline);
            }
        }
        finally
        {
            shutdown.Release.TrySetResult();
        }
    }

    private static CancellationTokenSource Deadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        return deadline;
    }

    private static async Task<(ManagedDatabaseRef Database, string Text)> SeedAsync(ManagementApi api, CancellationToken cancellationToken)
    {
        var faker = new Faker();
        var database = await api.CreateManagedDatabaseAsync("db" + faker.Random.AlphaNumeric(8).ToLowerInvariant());
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var data = await api.CreateClientAsync(database.Organization, ApiKeyScopes.DocumentsWrite, ApiKeyScopes.ShapesRead);
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        var text = faker.Lorem.Sentence();
        using var put = await data.PutAsJsonAsync(ManagementApi.Uri(
            $"{database.Data}/collections/items/documents/{faker.Random.Guid():D}"), new { text }, cancellationToken);
        put.EnsureSuccessStatusCode();
        // Warm the API-key credential cache before measuring the subscription's memory reservations.
        using var warm = await data.GetAsync(ManagementApi.Uri($"{database.Data}/collections/items/shape?offset=-1"), cancellationToken);
        await Assert.That(warm.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(cancellationToken);
        return (database, text);
    }

    private static HttpClient Subscriber(ManagementApi api, ManagedDatabaseRef database, FakeTimeProvider clock, TimeSpan? tokenLifetime = null)
    {
        var handler = api.Tls.CreateHandler();
        // Do not drain blocked bodies when disposing HTTP/1.1 responses; HTTP/2 disposal resets the stream.
        handler.MaxResponseDrainSize = 0;
        // A single HTTP/2 connection cannot hold all 128 subscriptions under Kestrel's default stream limit.
        handler.EnableMultipleHttp2Connections = true;
        var client = new HttpClient(handler) { BaseAddress = api.Server.ClientAddress, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new("Bearer", api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = ApiKeyScopes.ShapesRead;
            token.Expires = clock.GetUtcNow().Add(tokenLifetime ?? TimeSpan.FromMinutes(5)).UtcDateTime;
        }));
        return client;
    }

    private static async Task<HttpResponseMessage> SubscribeAsync(
        HttpClient client, ManagedDatabaseRef database, bool block, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri(
            $"/projects/{database.Project}/databases/{database.Name}/collections/items/subscribe"))
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy,
        };
        if (block)
        {
            request.Headers.Add(BlockedWrites.Header, "true");
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task AssertStreamAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");
    }

    private static async Task AssertSnapshotAsync(HttpResponseMessage response, string text, CancellationToken cancellationToken)
    {
        await AssertStreamAsync(response);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken), leaveOpen: true);
        await Assert.That(await reader.ReadLineAsync(cancellationToken)).IsEqualTo("event: shape");
        await Assert.That(await reader.ReadLineAsync(cancellationToken)).StartsWith("id: ");
        var line = await reader.ReadLineAsync(cancellationToken);
        await Assert.That(line).StartsWith("data: ");
        using var page = JsonDocument.Parse(line![6..]);
        var operation = page.RootElement.GetProperty("changes").EnumerateArray()
            .SelectMany(change => change.GetProperty("ops").EnumerateArray()).Single();
        await Assert.That(operation.GetProperty("op").GetString()).IsEqualTo("upsert");
        await Assert.That(operation.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
        await Assert.That(await reader.ReadLineAsync(cancellationToken)).IsEqualTo("");
    }

    // Registered last so application stopping cancels the subscription while the real listeners remain alive.
    private sealed class HeldShutdown : IHostedService
    {
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Stopping.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class SubscriptionResources : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly SqliteStorageEngine _engine;
        private readonly UsageMeter _usage;
        private long _reserved;
        private long _active;
        private long _flights;
        private long _waiting;

        public SubscriptionResources(ManagementApi api)
        {
            _engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
            _usage = api.Server.Services.GetRequiredService<UsageMeter>();
            var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
            _listener.InstrumentPublished = (instrument, observer) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name is
                    "terrabase.memory.reserved" or "terrabase.subscriptions.active" or "terrabase.shape.flights.active" or "terrabase.shape.requests.waiting")
                {
                    observer.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                switch (instrument.Name)
                {
                    case "terrabase.memory.reserved":
                        Interlocked.Exchange(ref _reserved, value - api.Server.Services.GetRequiredService<UsageMeter>().ReservedMeasurementBytes);
                        break;
                    case "terrabase.subscriptions.active":
                        Interlocked.Add(ref _active, value);
                        break;
                    case "terrabase.shape.flights.active":
                        Interlocked.Add(ref _flights, value);
                        break;
                    case "terrabase.shape.requests.waiting":
                        Interlocked.Add(ref _waiting, value);
                        break;
                }
            });
            _listener.Start();
        }

        public long Reserved
        {
            get
            {
                _usage.ObserveMemory(_listener.RecordObservableInstruments);
                return Volatile.Read(ref _reserved);
            }
        }

        public long Active => Volatile.Read(ref _active);

        public async Task WaitForCleanupAsync(long? baseline, CancellationToken cancellationToken)
        {
            while (Active != 0 || Volatile.Read(ref _flights) != 0 || Volatile.Read(ref _waiting) != 0
                || _engine.Io.Admitted != 0 || _engine.Io.Running != 0 || (baseline is { } bytes && Reserved != bytes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        public async Task AssertReleasedAsync(long baseline)
        {
            await Assert.That(Reserved).IsEqualTo(baseline);
            await Assert.That(Active).IsEqualTo(0);
            await Assert.That(Volatile.Read(ref _flights)).IsEqualTo(0);
            await Assert.That(Volatile.Read(ref _waiting)).IsEqualTo(0);
            await Assert.That(_engine.Io.Admitted).IsEqualTo(0);
            await Assert.That(_engine.Io.Running).IsEqualTo(0);
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class BlockedWrites(int expected)
    {
        public const string Header = "X-Test-Block-Subscription-Write";
        private readonly int _expected = expected;
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        private int _returned;
        private int _cancelled;

        public TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Cancelled => Volatile.Read(ref _cancelled);

        private void RecordCancellation() => Interlocked.Increment(ref _cancelled);

        public void Install(IServiceCollection services)
        {
            var registration = services.Single(service => service.ServiceType == typeof(IShapeSubscriptionService));
            services.Remove(registration);
            services.AddSingleton<ShapeSubscriptionService>();
            services.AddSingleton<IShapeSubscriptionService>(provider => new ObservedSubscription(
                provider.GetRequiredService<ShapeSubscriptionService>(), this));
            // Advancing the fake clock past a flush tick would run usage flushes and storage samples on the same SQLite I/O
            // pool these tests assert is released; usage is not under test here.
            services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(UsageFlushService)));
        }

        private sealed class ObservedSubscription(IShapeSubscriptionService inner, BlockedWrites writes) : IShapeSubscriptionService
        {
            public async ValueTask<IResult> StreamAsync(
                HttpContext context, ShapeRequest request, DateTimeOffset expiresAt, CancellationToken cancellationToken)
            {
                if (!context.Request.Headers.ContainsKey(Header))
                {
                    return await inner.StreamAsync(context, request, expiresAt, cancellationToken);
                }

                var original = context.Response.Body;
                context.Response.Body = new BlockedStream(original, writes);
                try
                {
                    return await inner.StreamAsync(context, request, expiresAt, cancellationToken);
                }
                finally
                {
                    context.Response.Body = original;
                    if (Interlocked.Increment(ref writes._returned) == writes._expected)
                    {
                        writes.AllReturned.TrySetResult();
                    }
                }
            }
        }

        private sealed class BlockedStream(Stream inner, BlockedWrites writes) : Stream
        {
            private int _writes;

            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (++_writes == 2)
                {
                    if (Interlocked.Increment(ref writes._entered) == writes._expected)
                    {
                        writes.AllEntered.TrySetResult();
                    }

                    try
                    {
                        await writes._blocked.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        writes.RecordCancellation();
                        throw;
                    }
                }

                await inner.WriteAsync(buffer, cancellationToken);
                if (_writes == 1)
                {
                    // Deliver real SSE headers and the event prefix before blocking the actual page write.
                    await inner.FlushAsync(cancellationToken);
                }
            }
        }
    }
}
