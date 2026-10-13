using System.Diagnostics;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// Region offline and rejoin on Fly: stops the sjc machine, writes through gru, starts sjc and waits for it to catch
/// up. It stops a machine of the shared app, so it runs alone (<c>make terrabase-e2e-fly-outage</c>), never in the
/// deploy run. It runs only with <c>TERRABASE_E2E_FLY_URL</c> and <c>TERRABASE_E2E_FLY_OUTAGE=1</c>.
/// </summary>
[Category("fly-outage")]
public sealed class FlyOutageE2ETests
{
    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage()
    {
        Skip.Unless(Cluster.FlyRequested && Environment.GetEnvironmentVariable("TERRABASE_E2E_FLY_OUTAGE") == "1",
            "Fly outage test: set TERRABASE_E2E_FLY_URL and TERRABASE_E2E_FLY_OUTAGE=1, or run make terrabase-e2e-fly-outage");
        await using var cluster = await Cluster.StartAsync();
        var app = Environment.GetEnvironmentVariable("TERRABASE_E2E_FLY_APP") ?? "ts-terrabase";
        var machine = await Flyctl.MachineInRegionAsync(app, Cluster.Sjc);
        var stopped = false;
        try
        {
            await Flyctl.RunAsync("machine", "stop", machine, "--app", app);
            stopped = true;
            var uri = cluster.Document(EntityIds.New().ToString());
            var body = new JsonObject { ["value"] = $"written-during-outage-{EntityIds.New()}" };
            await Docs.PutAsync(cluster[Cluster.Gru], uri, body);
            await Assert.That(Docs.Canonical(await Docs.ReadAsync(cluster[Cluster.Gru], uri))).IsEqualTo(Docs.Canonical(body));

            await Flyctl.RunAsync("machine", "start", machine, "--app", app);
            stopped = false;
            var started = Stopwatch.GetTimestamp();
            // While sjc boots, Fly serves the pinned request from gru: the pin refuses that answer, so it reads as not yet there.
            var caught = await Converge.UntilAsync(async () =>
            {
                try
                {
                    return await Docs.ReadAsync(cluster[Cluster.Sjc], uri);
                }
                catch (RegionMismatchException)
                {
                    return null;
                }
            }, document => Docs.Canonical(document) == Docs.Canonical(body), "sjc catch-up after rejoin");
            Converge.Report("sjc catch-up after rejoin", started);
            await Assert.That(Docs.Canonical(caught)).IsEqualTo(Docs.Canonical(body));
        }
        finally
        {
            if (stopped)
            {
                await Flyctl.RunAsync("machine", "start", machine, "--app", app);
            }
        }
    }
}
