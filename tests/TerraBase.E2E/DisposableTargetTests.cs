using System.Net;
using System.Text.Json;

namespace TerraBase.E2E;

/// <summary>
/// Negative controls of a machine-pinned run's target (<see cref="DisposableTarget"/>) and identity check
/// (<see cref="Cluster.CheckIdentitiesAsync"/>): a missing, production, preview or mismatched configuration is refused
/// before any flyctl call or request; an app outside its org or network, or a machine set that differs, is refused before
/// the first write or fault; a machine that does not answer as itself stops the run; and a pinned run refuses scenarios
/// still listed as expected failures.
/// </summary>
public sealed class DisposableTargetTests
{
    private static readonly string[] Regions = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    // Each case changes one variable of a valid disposable-app configuration (Good); null unsets it.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments(DisposableTarget.AppVariable, null, "has no default")]
    [Arguments(DisposableTarget.AppVariable, "", "has no default")]
    [Arguments(DisposableTarget.AppVariable, "ts-terrabase", "production or preview")]
    [Arguments(DisposableTarget.AppVariable, "TS-Terrabase", "production or preview")]
    [Arguments(DisposableTarget.AppVariable, "ts-auth", "production or preview")]
    [Arguments(DisposableTarget.AppVariable, "ts-dashboard", "production or preview")]
    [Arguments(DisposableTarget.AppVariable, "ts-pr-151", "production or preview")]
    [Arguments(Cluster.FlyUrlVariable, null, "the disposable app's own host")]
    [Arguments(Cluster.FlyUrlVariable, "https://ts-terrabase.fly.dev/", "the disposable app's own host")]
    [Arguments(Cluster.FlyUrlVariable, "https://ts-auth.fly.dev/", "the disposable app's own host")]
    [Arguments(Cluster.FlyUrlVariable, "https://api.terrascale.tech/", "the disposable app's own host")]
    [Arguments(Cluster.FlyUrlVariable, "https://gru.api.terrascale.tech/", "the disposable app's own host")]
    [Arguments(Cluster.FlyUrlVariable, "https://auth.terrascale.tech/", "the disposable app's own host")]
    [Arguments(Cluster.FlyUrlVariable, "http://tsb-e2e.fly.dev/", "the disposable app's own host")]
    [Arguments(Cluster.FlyUrlVariable, "https://tsb-e2e.fly.dev/v1", "the disposable app's own host")]
    [Arguments(DisposableTarget.OrgVariable, null, "has no default")]
    [Arguments(DisposableTarget.OrgVariable, "terrascale-previews-515", "the PR previews' org")]
    [Arguments(DisposableTarget.OrgVariable, "terrascale", "option A needs TERRABASE_E2E_FLY_NETWORK")]
    [Arguments(DisposableTarget.MachinesVariable, null, "has no default")]
    [Arguments(DisposableTarget.MachinesVariable, "gru=m-gru,sjc=m-sjc", "names no machine for fra")]
    [Arguments(DisposableTarget.MachinesVariable, "gru=m-gru,gru=m-2,sjc=m-sjc,fra=m-fra", "each region once")]
    [Arguments(DisposableTarget.MachinesVariable, "gru=m-gru,sjc,fra=m-fra", "each region once")]
    [Arguments(DisposableTarget.MachinesVariable, "gru=,sjc=m-sjc,fra=m-fra", "each region once")]
    public async Task A_missing_production_preview_or_mismatched_target_is_refused_before_any_flyctl_call(string name, string? value, string reason)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(DisposableTarget.Resolve(With(name, value), Regions)));
        await Assert.That(error!.Message).Contains(reason);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_disposable_app_on_its_own_host_with_every_machine_named_is_accepted()
    {
        var target = DisposableTarget.Resolve(With(DisposableTarget.AppVariable, "tsb-e2e"), Regions);
        await Assert.That(target.App).IsEqualTo("tsb-e2e");
        await Assert.That(target.Network).IsEqualTo("");
        var approved = DisposableTarget.Resolve(name => name switch
        {
            DisposableTarget.AppVariable => "ts-terrabase-e2e",
            Cluster.FlyUrlVariable => "https://ts-terrabase-e2e.fly.dev/",
            DisposableTarget.OrgVariable => "terrascale-previews-515",
            _ => Good(name),
        }, Regions);
        // Field names verified read-only against flyctl apps list --org terrascale-previews-515 --json; sanitized output recorded in the PR reply.
        using var liveShape = JsonDocument.Parse("""[{"Name":"ts-terrabase-e2e","Organization":{"Slug":"terrascale-previews-515"},"Network":""}]""");
        DisposableTarget.CheckApp(approved, liveShape.RootElement);
        await Assert.That(target.Machines[Cluster.Fra]).IsEqualTo("m-fra");
        var optionA = DisposableTarget.Resolve(name => name switch
        {
            DisposableTarget.OrgVariable => "terrascale",
            DisposableTarget.NetworkVariable => "tsb-e2e-net",
            _ => Good(name),
        }, Regions);
        await Assert.That(optionA.Network).IsEqualTo("tsb-e2e-net");
    }

    // flyctl apps list --json, read-only: the app must be in the expected org on the expected network (empty = Fly's default).
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments("tsb-e2e", "tsb-e2e-org", "", null)]
    [Arguments("tsb-e2e", "terrascale", "", "org terrascale")]
    [Arguments("tsb-e2e", "tsb-e2e-org", "tsb-e2e-net", "network \"tsb-e2e-net\"")]
    [Arguments("other", "tsb-e2e-org", "", "no single app tsb-e2e")]
    public async Task The_app_must_be_in_its_org_on_its_network(string app, string org, string network, string? refusal)
    {
        using var apps = JsonDocument.Parse($$"""[{"Name":"{{app}}","Organization":{"Slug":"{{org}}"},"Network":"{{network}}"},{"Name":"ts-terrabase","Organization":{"Slug":"terrascale"},"Network":""}]""");
        var target = DisposableTarget.Resolve(Good, Regions);
        if (refusal is null)
        {
            DisposableTarget.CheckApp(target, apps.RootElement);
            return;
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => DisposableTarget.CheckApp(target, apps.RootElement)));
        await Assert.That(error!.Message).Contains(refusal);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task Option_a_needs_the_app_on_its_own_network_not_production_s()
    {
        var target = DisposableTarget.Resolve(name => name switch
        {
            DisposableTarget.OrgVariable => "terrascale",
            DisposableTarget.NetworkVariable => "tsb-e2e-net",
            _ => Good(name),
        }, Regions);
        using var onDefault = JsonDocument.Parse("""[{"Name":"tsb-e2e","Organization":{"Slug":"terrascale"},"Network":""}]""");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => DisposableTarget.CheckApp(target, onDefault.RootElement)));
        await Assert.That(error!.Message).Contains("on network \"\"");
        using var onOwn = JsonDocument.Parse("""[{"Name":"tsb-e2e","Organization":{"Slug":"terrascale"},"Network":"tsb-e2e-net"}]""");
        DisposableTarget.CheckApp(target, onOwn.RootElement);
    }

    // flyctl machine list --json, read-only: each region holds exactly its named machine.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments("""[{"id":"m-gru","region":"gru"},{"id":"m-sjc","region":"sjc"},{"id":"m-fra","region":"fra"}]""", null)]
    [Arguments("""[{"id":"m-gru","region":"gru"},{"id":"m-sjc","region":"sjc"}]""", "exactly machine m-fra in fra")]
    [Arguments("""[{"id":"m-gru","region":"gru"},{"id":"m-sjc","region":"sjc"},{"id":"m-x","region":"fra"}]""", "exactly machine m-fra in fra")]
    [Arguments("""[{"id":"m-gru","region":"gru"},{"id":"m-sjc","region":"sjc"},{"id":"m-fra","region":"fra"},{"id":"m-2","region":"fra"}]""", "exactly machine m-fra in fra")]
    [Arguments("""[{"id":"m-gru","region":"sjc"},{"id":"m-sjc","region":"gru"},{"id":"m-fra","region":"fra"}]""", "exactly machine m-gru in gru")]
    public async Task Each_region_must_hold_exactly_its_named_machine(string listed, string? refusal)
    {
        using var machines = JsonDocument.Parse(listed);
        var target = DisposableTarget.Resolve(Good, Regions);
        if (refusal is null)
        {
            DisposableTarget.CheckMachines(target, machines.RootElement);
            return;
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => DisposableTarget.CheckMachines(target, machines.RootElement)));
        await Assert.That(error!.Message).Contains(refusal);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_pinned_run_refuses_a_scenario_still_listed_as_an_expected_failure()
    {
        await Assert.That(Gaps.PinnedRefusal("E11")).Contains("its Gaps row must be removed first");
        await Assert.That(Gaps.PinnedRefusal("not-a-gap")).IsNull();
    }

    // /healthz of gru's pinned machine: the run starts (and ends) only when it answers as m-gru in gru running the revision.
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments(HttpStatusCode.OK, """{"region":"gru","machine":"m-gru","revision":"rev1"}""", null)]
    [Arguments(HttpStatusCode.OK, """{"region":"sjc","machine":"m-gru","revision":"rev1"}""", "region sjc")]
    [Arguments(HttpStatusCode.OK, """{"region":"gru","machine":"m-other","revision":"rev1"}""", "machine m-other")]
    [Arguments(HttpStatusCode.OK, """{"region":"gru","machine":"m-gru","revision":"rev2"}""", "revision rev2")]
    [Arguments(HttpStatusCode.OK, """{"region":"gru","machine":"m-gru","revision":null}""", "revision (none)")]
    [Arguments(HttpStatusCode.OK, """{"status":"ok","version":"0.1.0","region":"gru"}""", "machine (none)")]
    [Arguments(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"unavailable"}}""", "503")]
    public async Task Each_machine_must_answer_as_itself_running_the_revision_under_test(HttpStatusCode status, string body, string? refusal)
    {
        using var client = new HttpClient(new Stand(_ => new HttpResponseMessage(status) { Content = new StringContent(body) })) { BaseAddress = new Uri("http://fly.test") };
        var clients = new Dictionary<string, HttpClient>(StringComparer.Ordinal) { [Cluster.Gru] = client };
        var machines = new Dictionary<string, string>(StringComparer.Ordinal) { [Cluster.Gru] = "m-gru" };
        if (refusal is null)
        {
            await Cluster.CheckIdentitiesAsync(clients, machines, "rev1");
            return;
        }

        var error = await Assert.ThrowsAsync<Exception>(() => Cluster.CheckIdentitiesAsync(clients, machines, "rev1"));
        await Assert.That(error!.Message).Contains(refusal);
    }

    // A valid disposable-app configuration outside production's org.
    private static string? Good(string name) => name switch
    {
        DisposableTarget.AppVariable => "tsb-e2e",
        Cluster.FlyUrlVariable => "https://tsb-e2e.fly.dev/",
        DisposableTarget.OrgVariable => "tsb-e2e-org",
        DisposableTarget.NetworkVariable => null,
        DisposableTarget.MachinesVariable => "gru=m-gru,sjc=m-sjc,fra=m-fra",
        _ => throw new InvalidOperationException($"the target read {name}"),
    };

    private static Func<string, string?> With(string changed, string? value) => name => name == changed ? value : Good(name);

    private sealed class Stand(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
