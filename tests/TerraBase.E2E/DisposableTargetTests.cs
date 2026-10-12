using System.Net;

namespace TerraBase.E2E;

/// <summary>
/// Negative controls of a machine-pinned run's target (<see cref="DisposableTarget"/>) and identity check
/// (<see cref="Cluster.CheckIdentitiesAsync"/>): a missing, production, preview or mismatched configuration is refused
/// before any flyctl call or request, and a machine that does not answer as itself stops the run.
/// </summary>
public sealed class DisposableTargetTests
{
    [Test]
    [Scenario(Nodes.Harness)]
    [Arguments(null, "https://ts-terrabase.fly.dev/", "has no default")]
    [Arguments("", "https://ts-terrabase.fly.dev/", "has no default")]
    [Arguments("ts-terrabase", "https://ts-terrabase.fly.dev/", "production or preview")]
    [Arguments("TS-Terrabase", "https://ts-terrabase.fly.dev/", "production or preview")]
    [Arguments("ts-auth", "https://ts-auth.fly.dev/", "production or preview")]
    [Arguments("ts-dashboard", "https://ts-dashboard.fly.dev/", "production or preview")]
    [Arguments("ts-pr-151", "https://ts-pr-151.fly.dev/", "production or preview")]
    [Arguments("tsb-e2e", "https://ts-terrabase.fly.dev/", "the disposable app's own host")]
    [Arguments("tsb-e2e", "https://api.terrascale.tech/", "the disposable app's own host")]
    [Arguments("tsb-e2e", "https://gru.api.terrascale.tech/", "the disposable app's own host")]
    [Arguments("tsb-e2e", "http://tsb-e2e.fly.dev/", "the disposable app's own host")]
    [Arguments("tsb-e2e", "https://tsb-e2e.fly.dev/v1", "the disposable app's own host")]
    [Arguments("tsb-e2e", null, "the disposable app's own host")]
    public async Task A_missing_production_preview_or_mismatched_target_is_refused(string? app, string? url, string reason)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(DisposableTarget.Resolve(Variables(app, url))));
        await Assert.That(error!.Message).Contains(reason);
    }

    [Test]
    [Scenario(Nodes.Harness)]
    public async Task The_disposable_app_on_its_own_host_is_accepted()
    {
        await Assert.That(DisposableTarget.Resolve(Variables("tsb-e2e", "https://tsb-e2e.fly.dev/"))).IsEqualTo("tsb-e2e");
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

    private static Func<string, string?> Variables(string? app, string? url) => name => name switch
    {
        DisposableTarget.AppVariable => app,
        Cluster.FlyUrlVariable => url,
        _ => throw new InvalidOperationException($"the target read {name}"),
    };

    private sealed class Stand(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
