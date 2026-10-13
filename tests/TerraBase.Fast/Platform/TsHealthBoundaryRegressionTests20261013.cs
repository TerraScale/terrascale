using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

// TS Health maps to the existing operational probes; there is no separate ts-health project.
public sealed class TsHealthBoundaryRegressionTests20261013
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Catalog_readiness_recovers_and_degrades_without_changing_liveness(bool clientListener)
    {
        var catalog = new ToggledCatalog();
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services =>
            services.AddSingleton<ICatalogReadiness>(catalog));
        using var client = clientListener ? tls.CreateClient(server.ClientAddress!) : PeerTls.CreateClient(server.BaseAddress);
        client.Timeout = TimeSpan.FromSeconds(30);

        await AssertProbeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable", server.DataDirectory);
        await AssertProbeAsync(client, "/healthz", HttpStatusCode.OK, "ok", server.DataDirectory);
        catalog.Ready = true;
        await AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready", server.DataDirectory);
        catalog.Ready = false;
        await AssertProbeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable", server.DataDirectory);
        await AssertProbeAsync(client, "/healthz", HttpStatusCode.OK, "ok", server.DataDirectory);
        catalog.Ready = true;
        await AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready", server.DataDirectory);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Filesystem_probe_failures_are_sanitized_and_recover_after_cache_expiry(bool denied)
    {
        var clock = new FakeTimeProvider();
        var marker = new Faker().Random.AlphaNumeric(32);
        var fail = true;
        await using var server = await TerraBaseTestServer.StartAsync(services: services =>
            ConfigureDiskProbe(services, clock, () => Volatile.Read(ref fail)
                ? Task.FromException(denied ? new UnauthorizedAccessException(marker) : new IOException(marker))
                : Task.CompletedTask));
        server.Client.Timeout = TimeSpan.FromSeconds(30);

        await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable", marker);
        await AssertProbeAsync(server.Client, "/healthz", HttpStatusCode.OK, "ok", marker);
        Volatile.Write(ref fail, false);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.OK, "ready", marker);
        await Assert.That(Directory.EnumerateFiles(server.DataDirectory, "*.probe")).IsEmpty();
    }

    [Test]
    public async Task Catalog_degradation_during_an_inflight_disk_probe_must_not_report_ready()
    {
        var catalog = new ToggledCatalog { Ready = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TerraBaseTestServer.StartAsync(services: services =>
        {
            services.AddSingleton<ICatalogReadiness>(catalog);
            ConfigureDiskProbe(services, new FakeTimeProvider(), () =>
            {
                entered.TrySetResult();
                return release.Task;
            });
        });
        server.Client.Timeout = TimeSpan.FromSeconds(30);
        var pending = server.Client.GetAsync(new Uri("/readyz", UriKind.Relative));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertProbeAsync(server.Client, "/healthz", HttpStatusCode.OK, "ok", server.DataDirectory);
            // The catalog stops being ready after NodeReadiness enters the storage boundary.
            catalog.Ready = false;
            release.TrySetResult();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(30));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(body.RootElement.GetProperty("status").GetString()).IsEqualTo("unavailable");
        }
        finally
        {
            release.TrySetResult();
            using var completed = await pending.WaitAsync(TimeSpan.FromSeconds(30));
            await server.Services.GetRequiredService<SqliteStorageEngine>().ReadinessProbeTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static void ConfigureDiskProbe(IServiceCollection services, FakeTimeProvider clock, Func<Task> probe)
    {
        // Freeze background pressure sampling separately from the filesystem probe's test clock.
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        var registration = services.Single(service => service.ServiceType == typeof(SqliteStorageEngine));
        services.Remove(registration);
        services.AddSingleton(provider =>
        {
            var engine = (SqliteStorageEngine)registration.ImplementationFactory!(provider);
            engine.ReadinessClock = clock;
            engine.BeforeReadinessProbe = probe;
            return engine;
        });
    }

    private static async Task AssertProbeAsync(HttpClient client, string path, HttpStatusCode expected, string status, string privateValue)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);

        await Assert.That(response.StatusCode).IsEqualTo(expected);
        await Assert.That(body.RootElement.GetProperty("status").GetString()).IsEqualTo(status);
        await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name).ToList())
            .IsEquivalentTo(["status", "version", "region", "storageFormat", "revision", "machine"]);
        await Assert.That(text.Contains(privateValue, StringComparison.Ordinal)).IsFalse();
    }

    private sealed class ToggledCatalog : ICatalogReadiness
    {
        private bool _ready;

        public bool Ready
        {
            get => Volatile.Read(ref _ready);
            set => Volatile.Write(ref _ready, value);
        }

        public bool IsReady => Ready;
    }
}
