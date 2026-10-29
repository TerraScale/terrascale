using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
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

    private static async Task<JsonElement> ReadAsync(TerraBaseTestServer server, CancellationToken cancellationToken)
    {
        using var response = await server.Client.GetAsync(Pressure, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    private static Ulid Incarnation(JsonElement snapshot) => Ulid.Parse(snapshot.GetProperty("process_id").GetString()!);

    private static double[] MetricValues(string metrics, string name, string? label = null) =>
        [.. metrics.Split('\n').Where(line =>
            (line.StartsWith(name + " ", StringComparison.Ordinal) || line.StartsWith(name + "{", StringComparison.Ordinal))
            && (label is null || line.Contains(label, StringComparison.Ordinal)))
            .Select(line => double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture))];

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
