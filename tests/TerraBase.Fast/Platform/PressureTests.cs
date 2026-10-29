using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class PressureTests
{
    private static readonly Uri Pressure = new("/v1/admin/node/pressure", UriKind.Relative);
    private static readonly Uri Metrics = new("/metrics", UriKind.Relative);

    [Test]
    public async Task Machine_pressure_reports_local_readiness()
    {
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["node-name"] = "d894655b3ed118");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        JsonElement snapshot = default;
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            snapshot = await ReadAsync(server, token);
            return snapshot.GetProperty("local_ready").ValueKind == JsonValueKind.True;
        }, TimeSpan.FromSeconds(25));
        using var readiness = await server.Client.GetAsync(new Uri("/readyz", UriKind.Relative), deadline.Token);
        await Assert.That(readiness.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(snapshot.GetProperty("schema").GetInt32()).IsEqualTo(1);
        await Assert.That(snapshot.GetProperty("node").GetString()).IsEqualTo("d894655b3ed118");
        await Assert.That(snapshot.TryGetProperty("instance_id", out _)).IsFalse();
        await Assert.That(snapshot.GetProperty("state").GetString()).IsEqualTo("unknown");
        await Assert.That(snapshot.GetProperty("load").GetProperty("storage_bytes").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(snapshot.GetProperty("disk_iops").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(snapshot.GetProperty("io_queue_depth").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(snapshot.TryGetProperty("top_tenants", out _)).IsFalse();
        await Assert.That(snapshot.GetProperty("top_organizations").GetArrayLength()).IsEqualTo(0);
        using var evidence = await server.Client.GetAsync(Pressure, deadline.Token);
        await Assert.That(evidence.Headers.CacheControl!.NoStore).IsTrue();
        using var oldRoute = await server.Client.GetAsync(new Uri("/admin/node/pressure", UriKind.Relative), deadline.Token);
        await Assert.That(oldRoute.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Closed_storage_is_not_locally_ready()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var original = await ReadAsync(server, deadline.Token);
        await server.Services.GetRequiredService<IStorageEngine>().DisposeAsync();
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            var snapshot = await ReadAsync(server, token);
            return snapshot.GetProperty("sampled_at").GetDateTimeOffset() > original.GetProperty("sampled_at").GetDateTimeOffset()
                && snapshot.GetProperty("local_ready").ValueKind == JsonValueKind.False
                && snapshot.GetProperty("io_admitted_count").ValueKind == JsonValueKind.Null;
        }, TimeSpan.FromSeconds(25));
        using var readiness = await server.Client.GetAsync(new Uri("/readyz", UriKind.Relative), deadline.Token);
        await Assert.That(readiness.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task Replacing_the_data_directory_with_a_link_reports_unknown_storage()
    {
        var clock = new SamplingClock();
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        while ((await ReadAsync(server, deadline.Token)).GetProperty("storage_sampled_at").ValueKind == JsonValueKind.Null)
        {
            await Task.Yield();
        }

        var external = Directory.CreateTempSubdirectory("terrabase-pressure-link-").FullName;
        var original = server.DataDirectory + ".original";
        try
        {
            var faker = new Faker();
            var project = Directory.CreateDirectory(Path.Combine(external, EntityIds.New().ToString(), EntityIds.New().ToString()));
            await File.WriteAllBytesAsync(Path.Combine(project.FullName, EntityIds.New() + ".db"), faker.Random.Bytes(4096), deadline.Token);
            Directory.Move(server.DataDirectory, original);
            Directory.CreateSymbolicLink(server.DataDirectory, external);
            clock.SampleStorage();

            JsonElement snapshot;
            do
            {
                snapshot = await ReadAsync(server, deadline.Token);
                await Task.Yield();
            }
            while (snapshot.GetProperty("data_bytes").ValueKind != JsonValueKind.Null
                && snapshot.GetProperty("data_bytes").GetInt64() == 0);

            await Assert.That(snapshot.GetProperty("data_bytes").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(snapshot.GetProperty("top_organizations_by_storage").GetArrayLength()).IsEqualTo(0);
        }
        finally
        {
            if (new DirectoryInfo(server.DataDirectory).LinkTarget == external)
            {
                Directory.Delete(server.DataDirectory);
            }

            if (Directory.Exists(original))
            {
                Directory.Move(original, server.DataDirectory);
            }

            Directory.Delete(external, recursive: true);
        }
    }

    [Test]
    public async Task Engine_closure_reports_unknown_storage_without_stopping_admin_telemetry()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        while ((await ReadAsync(server, deadline.Token)).GetProperty("storage_sampled_at").ValueKind == JsonValueKind.Null)
        {
            await Task.Yield();
        }

        await server.Services.GetRequiredService<IStorageEngine>().DisposeAsync();
        JsonElement snapshot;
        do
        {
            snapshot = await ReadAsync(server, deadline.Token);
            await Task.Yield();
        }
        while (snapshot.GetProperty("storage_sampled_at").ValueKind != JsonValueKind.Null);

        await Assert.That(snapshot.GetProperty("data_bytes").ValueKind).IsEqualTo(JsonValueKind.Null);
        using var readiness = await server.Client.GetAsync(new Uri("/readyz", UriKind.Relative), deadline.Token);
        await Assert.That(readiness.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task Pending_tls_connections_are_counted_and_removed_after_disconnect()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, server.ClientAddress!.Port, deadline.Token);
        while ((await ReadAsync(server, deadline.Token)).GetProperty("load").GetProperty("connections").GetInt64() == 0)
        {
            await Task.Yield();
        }

        connection.Dispose();
        while ((await ReadAsync(server, deadline.Token)).GetProperty("load").GetProperty("connections").GetInt64() != 0)
        {
            await Task.Yield();
        }
    }

    [Test]
    [Arguments("memory-budget", "0")]
    [Arguments("memory-budget", "1048575")]
    [Arguments("memory-budget", "9223372036854775808")]
    public async Task Invalid_pressure_settings_refuse_real_host_startup(string setting, string value)
    {
        await Assert.That(async () =>
        {
            await using var server = await TerraBaseTestServer.StartAsync(settings => settings[setting] = value);
        }).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task The_metrics_listener_serves_metrics_and_nothing_else()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var scraper = new HttpClient { BaseAddress = server.MetricsAddress };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        using var pressure = await scraper.GetAsync(Pressure, deadline.Token);
        using var metrics = await scraper.GetAsync(Metrics, deadline.Token);

        await Assert.That(pressure.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(metrics.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await metrics.Content.ReadAsStringAsync(deadline.Token)).Contains("terrabase_pressure_available");
    }

    [Test]
    public async Task Admin_pressure_reports_the_schema_and_explicit_unknowns_and_is_hidden_from_authenticated_clients()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "1048576");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var snapshot = await ReadAsync(api.Server, deadline.Token);
        await AssertSnapshotAsync(snapshot);
        await Assert.That(snapshot.GetProperty("memory_budget_bytes").GetInt64()).IsEqualTo(1048576);
        await Assert.That(snapshot.GetProperty("mem_util").GetDouble()).IsEqualTo(1);

        foreach (var client in new[] { api.Client, api.Management.Client })
        {
            foreach (var path in new[] { Pressure, Metrics })
            {
                using var response = await client.GetAsync(path, deadline.Token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
        }

        var metrics = await api.Server.Client.GetStringAsync(Metrics, deadline.Token);
        foreach (var signal in new[] { "disk_iops", "io_queue_depth", "storage_bytes" })
        {
            await Assert.That(MetricValues(metrics, "terrabase_pressure_" + signal)).IsEmpty();
            await Assert.That(MetricValues(metrics, "terrabase_pressure_available", $"signal=\"{signal}\""))
                .IsEquivalentTo([0d]);
        }
    }

    [Test]
    public async Task Region_and_process_incarnation_are_stable_within_a_host_and_distinct_between_hosts()
    {
        await using var first = await TerraBaseTestServer.StartAsync(settings => settings["region"] = "gru-1");
        await using var second = await TerraBaseTestServer.StartAsync(settings =>
        {
            settings["region"] = "iad-1";
            settings["node-name"] = "pressure-test-node";
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var original = await ReadAsync(first, deadline.Token);
        var other = await ReadAsync(second, deadline.Token);
        var repeated = await ReadAsync(first, deadline.Token);

        await Assert.That(original.GetProperty("region").GetString()).IsEqualTo("gru-1");
        await Assert.That(other.GetProperty("region").GetString()).IsEqualTo("iad-1");
        await Assert.That(repeated.GetProperty("region").GetString()).IsEqualTo("gru-1");
        await Assert.That(original.GetProperty("node").GetString()).IsEqualTo("local");
        await Assert.That(other.GetProperty("node").GetString()).IsEqualTo("pressure-test-node");
        var incarnation = Incarnation(original);
        await Assert.That(incarnation).IsNotEqualTo(Ulid.Empty);
        await Assert.That(Incarnation(other)).IsNotEqualTo(Ulid.Empty);
        await Assert.That(Incarnation(other)).IsNotEqualTo(incarnation);
        await Assert.That(Incarnation(repeated)).IsEqualTo(incarnation);
    }

    [Test]
    public async Task Sampling_leaves_the_clock_callback_and_coalesces_the_ticks_that_fire_meanwhile()
    {
        using var time = new TickClock();
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<TimeProvider>(time));
        var start = time.GetUtcNow();
        await time.ProcessTimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(25));
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(time.StorageReads > 0), TimeSpan.FromSeconds(25));

        for (var second = 0; second < 20; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var now = time.GetUtcNow();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(time.LastProcessRead >= now), TimeSpan.FromSeconds(25));
        }

        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(time.LastStorageRead >= start.AddSeconds(10)), TimeSpan.FromSeconds(25));
        await Assert.That(time.ReadsInCallbacks).IsEqualTo(0);

        int process;
        int storage;
        DateTimeOffset end;
        try
        {
            time.Hold();
            time.Advance(TimeSpan.FromSeconds(10));
            await time.ProcessHeld.Task.WaitAsync(TimeSpan.FromSeconds(25));
            await time.StorageHeld.Task.WaitAsync(TimeSpan.FromSeconds(25));
            (process, storage) = (time.ProcessReads, time.StorageReads);
            time.Advance(TimeSpan.FromDays(2));
        }
        finally
        {
            time.Release();
        }

        time.Advance(TimeSpan.FromSeconds(10));
        end = time.GetUtcNow();
        await TerraBaseTestServer.EventuallyAsync(
            _ => Task.FromResult(time.LastProcessRead >= end && time.LastStorageRead >= end), TimeSpan.FromSeconds(25));

        await Assert.That(time.ProcessReads - process).IsLessThanOrEqualTo(11);
        await Assert.That(time.StorageReads - storage).IsLessThanOrEqualTo(2);
        await Assert.That(time.ReadsInCallbacks).IsEqualTo(0);
    }

    [Test]
    public async Task Concurrent_fetches_read_complete_cached_snapshots_without_rewriting_the_sampling_time()
    {
        var clock = new SamplingClock(pauseProcess: true);
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await clock.ProcessReady.Task.WaitAsync(deadline.Token);
        var original = await ReadAsync(server, deadline.Token);
        var beforeRefresh = DateTimeOffset.UtcNow;
        clock.SampleProcess();
        JsonElement refreshed;
        do
        {
            refreshed = await ReadAsync(server, deadline.Token);
            await Task.Yield();
        }
        while (refreshed.GetProperty("sampled_at").GetDateTimeOffset() <= original.GetProperty("sampled_at").GetDateTimeOffset());

        await Assert.That(refreshed.GetProperty("sampled_at").GetDateTimeOffset()).IsGreaterThanOrEqualTo(beforeRefresh);
        await Assert.That(refreshed.GetProperty("sampled_at").GetDateTimeOffset()).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => ReadAsync(server, deadline.Token)));
        foreach (var snapshot in snapshots)
        {
            await Assert.That(Incarnation(snapshot)).IsEqualTo(Incarnation(original));
        }

        var samples = snapshots.GroupBy(snapshot => snapshot.GetProperty("sampled_at").GetDateTimeOffset()).ToArray();
        await Assert.That(samples.Length).IsEqualTo(1);
        foreach (var sample in samples)
        {
            await AssertSnapshotAsync(sample.First());
            await Assert.That(sample.Select(snapshot => snapshot.GetRawText()).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Real_https_writes_and_reads_update_rates_listener_totals_storage_and_pressure_metrics()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "hobby");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var before = await ReadAsync(api.Server, deadline.Token);
        var clientBefore = Listener(before, "client").GetProperty("requests_total").GetInt64();
        var storageBefore = before.GetProperty("storage_sampled_at");
        var body = new Faker().Random.AlphaNumeric(1024);
        var document = new Uri(api.Document(DocumentApi.NewId()), UriKind.Relative);
        var writes = 0;
        JsonElement snapshot;
        string metrics;
        while (true)
        {
            using var write = await api.Client.PutAsJsonAsync(document, new { body, n = ++writes }, deadline.Token);
            write.EnsureSuccessStatusCode();
            using var read = await api.Client.GetAsync(document, deadline.Token);
            read.EnsureSuccessStatusCode();
            var stored = await read.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            await Assert.That(stored.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(writes);
            clock.Advance(TimeSpan.FromSeconds(1));
            snapshot = await ReadAsync(api.Server, deadline.Token);
            var top = snapshot.GetProperty("top_organizations").EnumerateArray().Any(tenant =>
                tenant.GetProperty("org").GetString() == api.Tenant.OrganizationId && tenant.GetProperty("request_rate").GetDouble() > 0);
            var storage = snapshot.GetProperty("top_organizations_by_storage").EnumerateArray().Any(tenant =>
                tenant.GetProperty("org").GetString() == api.Tenant.OrganizationId && tenant.GetProperty("bytes").GetInt64() > 0);
            var data = snapshot.GetProperty("data_bytes");
            var storageTime = snapshot.GetProperty("storage_sampled_at");
            if (top && storage && data.ValueKind == JsonValueKind.Number && data.GetInt64() > 0
                && storageTime.ValueKind == JsonValueKind.String
                && (storageBefore.ValueKind == JsonValueKind.Null || storageTime.GetDateTimeOffset() > storageBefore.GetDateTimeOffset())
                && snapshot.GetProperty("request_rate").GetDouble() > 0
                && snapshot.GetProperty("p95_latency_ms").GetDouble() > 0
                && snapshot.GetProperty("load").GetProperty("write_qps").GetDouble() > 0
                && snapshot.GetProperty("cpu_util").ValueKind == JsonValueKind.Number
                && snapshot.GetProperty("cpu_util").GetDouble() > 0
                && Listener(snapshot, "client").GetProperty("request_rate").GetDouble() > 0)
            {
                metrics = await api.Server.Client.GetStringAsync(Metrics, deadline.Token);
                if (MetricValues(metrics, "terrabase_pressure_request_rate").Any(value => value > 0)
                    && MetricValues(metrics, "terrabase_pressure_write_qps").Any(value => value > 0)
                    && MetricValues(metrics, "terrabase_pressure_listener_request_rate", "listener=\"client\"").Any(value => value > 0))
                {
                    break;
                }
            }

            await Task.Yield();
        }

        await AssertSnapshotAsync(snapshot, clock.GetUtcNow());
        await Assert.That(snapshot.GetProperty("cpu_util").GetDouble()).IsGreaterThan(0);
        await Assert.That(snapshot.GetProperty("memory_used_bytes").GetInt64()).IsGreaterThan(0);
        await Assert.That(Listener(snapshot, "client").GetProperty("requests_total").GetInt64()).IsGreaterThan(clientBefore);
        await Assert.That(snapshot.GetProperty("load").GetProperty("connections").GetInt64()).IsGreaterThan(0);
        await Assert.That(MetricValues(metrics, "terrabase_pressure_connections").Any(value => value > 0)).IsTrue();
        await Assert.That(snapshot.GetProperty("top_organizations").EnumerateArray()
            .Single(tenant => tenant.GetProperty("org").GetString() == api.Tenant.OrganizationId).GetProperty("plan").GetString()).IsEqualTo("hobby");
        await Assert.That(Listener(snapshot, "internal").GetProperty("requests_total").GetInt64())
            .IsGreaterThan(Listener(before, "internal").GetProperty("requests_total").GetInt64());
        await Assert.That(snapshot.GetProperty("data_bytes").GetInt64()).IsGreaterThanOrEqualTo(
            snapshot.GetProperty("top_organizations_by_storage").EnumerateArray()
                .Single(tenant => tenant.GetProperty("org").GetString() == api.Tenant.OrganizationId).GetProperty("bytes").GetInt64());

        foreach (var name in new[] { "memory_used_bytes", "memory_budget_bytes", "request_rate", "p95_latency_ms", "data_bytes", "write_qps" })
        {
            await Assert.That(MetricValues(metrics, "terrabase_pressure_" + name)).IsNotEmpty();
            await Assert.That(MetricValues(metrics, "terrabase_pressure_" + name)).All().Satisfy(value => value.IsGreaterThan(0));
        }

        foreach (var name in new[] { "cpu_util", "disk_used_bytes", "disk_free_bytes", "disk_total_bytes" })
        {
            if (snapshot.GetProperty(name).ValueKind == JsonValueKind.Number)
            {
                await Assert.That(MetricValues(metrics, "terrabase_pressure_" + name)).IsNotEmpty();
                await Assert.That(MetricValues(metrics, "terrabase_pressure_" + name)).All().Satisfy(value => value.IsGreaterThanOrEqualTo(0));
            }
        }

        foreach (var listener in new[] { "metrics", "client", "internal" })
        {
            foreach (var name in new[] { "request_rate", "p95_latency_ms", "requests_total" })
            {
                var values = MetricValues(metrics, "terrabase_pressure_listener_" + name, $"listener=\"{listener}\"");
                await Assert.That(values.Length).IsEqualTo(1);
                await Assert.That(values).All().Satisfy(value => value.IsGreaterThanOrEqualTo(0));
            }
        }

        foreach (var name in new[] { "top_tenant_request_rate", "top_tenant_storage_bytes" })
        {
            var values = MetricValues(metrics, "terrabase_pressure_" + name, "rank=\"");
            await Assert.That(values.Length).IsEqualTo(10);
            await Assert.That(values.Any(value => value > 0)).IsTrue();
            for (var rank = 1; rank <= 10; rank++)
            {
                await Assert.That(MetricValues(metrics, "terrabase_pressure_" + name, $"rank=\"{rank}\"").Length).IsEqualTo(1);
            }
        }

        await Assert.That(MetricValues(metrics, "terrabase_pressure_info")).IsEquivalentTo([1d]);
        await Assert.That(MetricValues(metrics, "terrabase_pressure_available").Length).IsEqualTo(11);
        foreach (var signal in new[] { "cpu_util", "mem_util", "disk", "data_bytes", "disk_iops", "io_queue_depth",
            "io_admitted_count", "connections", "storage_bytes", "tenant_cpu_share", "local_ready" })
        {
            await Assert.That(MetricValues(metrics, "terrabase_pressure_available", $"signal=\"{signal}\"").Length).IsEqualTo(1);
        }

        await Assert.That(metrics.Split('\n').Where(line => line.StartsWith("terrabase_pressure_", StringComparison.Ordinal))
            .Any(line => line.Contains("otel_metric_overflow", StringComparison.Ordinal))).IsFalse();
        await Assert.That(metrics.Split('\n').Where(line => line.StartsWith("terrabase_pressure_", StringComparison.Ordinal))
            .Any(line => line.Contains(api.Tenant.OrganizationId, StringComparison.Ordinal))).IsFalse();
    }

    private static async Task<JsonElement> ReadAsync(TerraBaseTestServer server, CancellationToken cancellationToken)
    {
        using var response = await server.Client.GetAsync(Pressure, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    private static JsonElement Listener(JsonElement snapshot, string listener) =>
        snapshot.GetProperty("listeners").EnumerateArray().Single(item => item.GetProperty("listener").GetString() == listener);

    private static Ulid Incarnation(JsonElement snapshot) => Ulid.Parse(snapshot.GetProperty("process_id").GetString()!);

    private static double[] MetricValues(string metrics, string name, string? label = null) =>
        [.. metrics.Split('\n').Where(line =>
            (line.StartsWith(name + " ", StringComparison.Ordinal) || line.StartsWith(name + "{", StringComparison.Ordinal))
            && (label is null || line.Contains(label, StringComparison.Ordinal)))
            .Select(line => double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture))];

    private sealed class TickClock() : FakeTimeProvider(DateTimeOffset.UtcNow), IDisposable
    {
        [ThreadStatic]
        private static bool t_inCallback;

        private readonly ManualResetEventSlim _released = new(initialState: true);
        private int _readsInCallbacks;
        private int _processReads;
        private int _storageReads;
        private long _lastProcessRead;
        private long _lastStorageRead;

        public TaskCompletionSource ProcessTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ProcessHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StorageHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadsInCallbacks => Volatile.Read(ref _readsInCallbacks);

        public int ProcessReads => Volatile.Read(ref _processReads);

        public int StorageReads => Volatile.Read(ref _storageReads);

        public DateTimeOffset LastProcessRead => new(Volatile.Read(ref _lastProcessRead), TimeSpan.Zero);

        public DateTimeOffset LastStorageRead => new(Volatile.Read(ref _lastStorageRead), TimeSpan.Zero);

        public void Hold() => _released.Reset();

        public void Release() => _released.Set();

        public void Dispose() => _released.Dispose();

        public override DateTimeOffset GetUtcNow()
        {
            if (t_inCallback)
            {
                Interlocked.Increment(ref _readsInCallbacks);
            }

            var stack = Environment.StackTrace;
            var process = stack.Contains("PressureSignals.Sample", StringComparison.Ordinal);
            if (!process && !stack.Contains("PressureSignals", StringComparison.Ordinal))
            {
                return base.GetUtcNow();
            }

            Interlocked.Increment(ref process ? ref _processReads : ref _storageReads);
            if (!t_inCallback && !_released.IsSet)
            {
                _ = ThreadPool.QueueUserWorkItem(static held => held.TrySetResult(), process ? ProcessHeld : StorageHeld, preferLocal: false);
                _released.Wait();
            }

            var now = base.GetUtcNow();
            Volatile.Write(ref process ? ref _lastProcessRead : ref _lastStorageRead, now.UtcTicks);
            return now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != TimeSpan.FromSeconds(1) && period != TimeSpan.FromSeconds(10))
            {
                return base.CreateTimer(callback, state, dueTime, period);
            }

            var timer = base.CreateTimer(
                value =>
                {
                    t_inCallback = true;
                    try
                    {
                        callback(value);
                    }
                    finally
                    {
                        t_inCallback = false;
                    }
                }, state, dueTime, period);
            if (period == TimeSpan.FromSeconds(1))
            {
                ProcessTimerCreated.TrySetResult();
            }

            return timer;
        }
    }

    private sealed class SamplingClock(bool pauseProcess = false) : TimeProvider
    {
        private readonly ConcurrentQueue<ITimer> _storageTimers = new();
        private ITimer? _processTimer;

        public TaskCompletionSource ProcessReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != TimeSpan.FromSeconds(10) && (!pauseProcess || period != TimeSpan.FromSeconds(1)))
            {
                return base.CreateTimer(callback, state, dueTime, period);
            }

            var timer = base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (period == TimeSpan.FromSeconds(10))
            {
                _storageTimers.Enqueue(timer);
            }
            else
            {
                Volatile.Write(ref _processTimer, timer);
                ProcessReady.TrySetResult();
            }

            return timer;
        }

        public void SampleStorage()
        {
            foreach (var timer in _storageTimers)
            {
                _ = timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        public void SampleProcess() => _ = Volatile.Read(ref _processTimer)!.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private static async Task AssertSnapshotAsync(JsonElement snapshot, DateTimeOffset? now = null)
    {
        await Assert.That(snapshot.GetProperty("schema").GetInt32()).IsEqualTo(1);
        await Assert.That(snapshot.GetProperty("state").GetString()).IsEqualTo("unknown");
        await Assert.That(snapshot.GetProperty("node").GetString()).IsNotEmpty();
        await Assert.That(Incarnation(snapshot)).IsNotEqualTo(Ulid.Empty);
        await Assert.That(snapshot.GetProperty("release").GetString()).IsEqualTo(
            typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        await Assert.That(snapshot.GetProperty("region").GetString()).IsNotEmpty();
        var sampledAt = snapshot.GetProperty("sampled_at").GetDateTimeOffset();
        await Assert.That(sampledAt.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(sampledAt).IsLessThanOrEqualTo(now ?? DateTimeOffset.UtcNow);
        await Assert.That(snapshot.GetProperty("sample_window_seconds").GetDouble()).IsGreaterThan(0);
        var used = snapshot.GetProperty("memory_used_bytes").GetInt64();
        var budget = snapshot.GetProperty("memory_budget_bytes").GetInt64();
        await Assert.That(used).IsGreaterThan(0);
        await Assert.That(budget).IsGreaterThan(0);
        foreach (var name in new[] { "cpu_util", "mem_util" })
        {
            var value = snapshot.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Null)
            {
                await Assert.That(double.IsFinite(value.GetDouble()) && value.GetDouble() >= 0 && value.GetDouble() <= 1).IsTrue();
                if (name == "mem_util")
                {
                    await Assert.That(value.GetDouble()).IsEqualTo(Math.Clamp((double)used / budget, 0, 1));
                }
            }
        }

        foreach (var name in new[] { "disk_used_bytes", "disk_free_bytes", "disk_total_bytes", "data_bytes" })
        {
            var value = snapshot.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Null)
            {
                await Assert.That(value.GetInt64()).IsGreaterThanOrEqualTo(0);
            }
        }

        foreach (var name in new[] { "disk_iops", "io_queue_depth" })
        {
            await Assert.That(snapshot.GetProperty(name).ValueKind).IsEqualTo(JsonValueKind.Null);
        }

        foreach (var name in new[] { "storage_bytes" })
        {
            await Assert.That(snapshot.GetProperty("load").GetProperty(name).ValueKind).IsEqualTo(JsonValueKind.Null);
        }

        await Assert.That(snapshot.GetProperty("io_admitted_count").GetInt64()).IsGreaterThanOrEqualTo(0);
        await Assert.That(snapshot.GetProperty("load").GetProperty("write_qps").GetDouble()).IsGreaterThanOrEqualTo(0);
        await Assert.That(snapshot.GetProperty("load").GetProperty("connections").GetInt64()).IsGreaterThanOrEqualTo(0);
        await Assert.That(snapshot.GetProperty("request_rate").GetDouble()).IsGreaterThanOrEqualTo(0);
        await Assert.That(snapshot.GetProperty("p95_latency_ms").GetDouble()).IsGreaterThanOrEqualTo(0);
        var listeners = snapshot.GetProperty("listeners");
        await Assert.That(listeners.GetArrayLength()).IsEqualTo(3);
        await Assert.That(listeners.EnumerateArray().Select(listener => listener.GetProperty("listener").GetString()!))
            .IsEquivalentTo(["metrics", "client", "internal"]);
        foreach (var listener in listeners.EnumerateArray())
        {
            await Assert.That(listener.GetProperty("requests_total").GetInt64()).IsGreaterThanOrEqualTo(0);
            await Assert.That(listener.GetProperty("request_rate").GetDouble()).IsGreaterThanOrEqualTo(0);
            await Assert.That(listener.GetProperty("p95_latency_ms").GetDouble()).IsGreaterThanOrEqualTo(0);
        }

        foreach (var name in new[] { "top_organizations", "top_organizations_by_storage" })
        {
            var tenants = snapshot.GetProperty(name);
            await Assert.That(tenants.GetArrayLength()).IsLessThanOrEqualTo(10);
            foreach (var tenant in tenants.EnumerateArray())
            {
                await Assert.That(tenant.GetProperty("org").GetString()).IsNotEmpty();
                if (name == "top_organizations")
                {
                    await Assert.That(tenant.GetProperty("cpu_share").ValueKind).IsEqualTo(JsonValueKind.Null);
                    await Assert.That(tenant.GetProperty("request_rate").GetDouble()).IsGreaterThanOrEqualTo(0);
                    var plan = tenant.GetProperty("plan");
                    await Assert.That(plan.ValueKind == JsonValueKind.Null || plan.GetString() is "hobby" or "starter" or "pro" or "team" or "enterprise").IsTrue();
                }
                else
                {
                    await Assert.That(tenant.GetProperty("bytes").GetInt64()).IsGreaterThanOrEqualTo(0);
                }
            }
        }

        var storageTime = snapshot.GetProperty("storage_sampled_at");
        if (storageTime.ValueKind != JsonValueKind.Null)
        {
            await Assert.That(storageTime.GetDateTimeOffset().Offset).IsEqualTo(TimeSpan.Zero);
            await Assert.That(storageTime.GetDateTimeOffset()).IsLessThanOrEqualTo(now ?? DateTimeOffset.UtcNow);
        }
    }
}
