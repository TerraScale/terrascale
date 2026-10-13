using System.Diagnostics;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// Region offline and rejoin on Fly: stops the sjc machine, writes through gru, starts sjc and waits for it to catch
/// up. It stops a machine, so it runs alone (<c>make terrabase-e2e-fly-outage</c>), never in the deploy run, and only
/// on a pinned disposable app (<c>TERRABASE_E2E_FLY_REVISION</c> and <c>TERRABASE_E2E_FLY_APP</c>, never production; see
/// docs/terrabase/e2e-disposable-app.md) with <c>TERRABASE_E2E_FLY_OUTAGE=1</c>.
/// </summary>
[Category("fly-outage")]
public sealed class FlyOutageE2ETests
{
    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task Sjc_machine_stopped_then_started_catches_up_on_writes_made_during_the_outage()
    {
        Skip.Unless(Cluster.FlyRequested && Cluster.FlyMachineFaults,
            "Fly outage test: a pinned disposable app (TERRABASE_E2E_FLY_URL, TERRABASE_E2E_FLY_APP, TERRABASE_E2E_FLY_REVISION) and TERRABASE_E2E_FLY_OUTAGE=1");
        await using var cluster = await Cluster.StartAsync();
        var stopped = false;
        try
        {
            await cluster.StopAsync(Cluster.Sjc);
            stopped = true;
            var uri = cluster.Document(EntityIds.New().ToString());
            var body = new JsonObject { ["value"] = $"written-during-outage-{EntityIds.New()}" };
            await Docs.PutAsync(cluster[Cluster.Gru], uri, body);
            await Assert.That(Docs.Canonical(await Docs.ReadAsync(cluster[Cluster.Gru], uri))).IsEqualTo(Docs.Canonical(body));

            await cluster.StartAgainAsync(Cluster.Sjc);
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
                await cluster.StartAgainAsync(Cluster.Sjc);
            }
        }
    }
}
