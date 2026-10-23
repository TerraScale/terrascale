using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Turso;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

// TS Health maps to the existing operational probes; there is no separate ts-health project.
public sealed class TsHealthBoundaryRegressionTests20261013
{
    // Every machine writes its own catalog (decision 0009), so storage is readiness's remaining dependency.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Storage_readiness_recovers_and_degrades_without_changing_liveness(bool clientListener)
    {
        var clock = new FakeTimeProvider();
        var fail = true;
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services =>
            ConfigureDiskProbe(services, clock, () => Volatile.Read(ref fail) ? Task.FromException(new IOException()) : Task.CompletedTask));
        using var client = clientListener ? tls.CreateClient(server.ClientAddress!) : PeerTls.CreateClient(server.BaseAddress);
        client.Timeout = TimeSpan.FromSeconds(30);

        await AssertProbeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable", server.DataDirectory);
        await AssertProbeAsync(client, "/healthz", HttpStatusCode.OK, "ok", server.DataDirectory);
        Volatile.Write(ref fail, false);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready", server.DataDirectory);
        Volatile.Write(ref fail, true);
        clock.Advance(TimeSpan.FromSeconds(1));
        await AssertProbeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable", server.DataDirectory);
        await AssertProbeAsync(client, "/healthz", HttpStatusCode.OK, "ok", server.DataDirectory);
        Volatile.Write(ref fail, false);
        clock.Advance(TimeSpan.FromSeconds(1));
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
    public async Task Storage_failure_of_an_inflight_disk_probe_must_not_report_ready()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TerraBaseTestServer.StartAsync(services: services =>
            ConfigureDiskProbe(services, new FakeTimeProvider(), () =>
            {
                entered.TrySetResult();
                return release.Task;
            }));
        server.Client.Timeout = TimeSpan.FromSeconds(30);
        var pending = server.Client.GetAsync(new Uri("/readyz", UriKind.Relative));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertProbeAsync(server.Client, "/healthz", HttpStatusCode.OK, "ok", server.DataDirectory);
            // Storage fails after NodeReadiness entered the storage boundary.
            release.TrySetException(new IOException());
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(30));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(body.RootElement.GetProperty("status").GetString()).IsEqualTo("unavailable");
        }
        finally
        {
            release.TrySetResult();
            using var completed = await pending.WaitAsync(TimeSpan.FromSeconds(30));
            await server.Services.GetRequiredService<TursoStorageEngine>().ReadinessProbeTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static void ConfigureDiskProbe(IServiceCollection services, FakeTimeProvider clock, Func<Task> probe)
    {
        // Freeze background pressure sampling separately from the filesystem probe's test clock.
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        var registration = services.Single(service => service.ServiceType == typeof(TursoStorageEngine));
        services.Remove(registration);
        services.AddSingleton(provider =>
        {
            var engine = (TursoStorageEngine)registration.ImplementationFactory!(provider);
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
        // Build detail only on the internal listener; the public listener (which sets X-TerraBase-Region) answers status,
        // version and region.
        string[] names = response.Headers.Contains("X-TerraBase-Region")
            ? ["status", "version", "region"]
            : ["status", "version", "region", "storageFormat", "revision", "machine"];
        await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name).ToList()).IsEquivalentTo(names);
        await Assert.That(text.Contains(privateValue, StringComparison.Ordinal)).IsFalse();
    }
}
