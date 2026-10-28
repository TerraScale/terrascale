using ApiBench;
using TerraScale.DotNet.TerraBase;
using TUnit.Core;

namespace BenchClient.Tests;

public sealed class ParityCellTests
{
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    private static ParityCellPlan Plan(TimeSpan? readiness = null) =>
        new(new ParityWindowPlan(4, TimeSpan.Zero, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(10)), readiness ?? TimeSpan.FromSeconds(10), TimeSpan.Zero);

    private static async Task<ParityCellRow> RunAsync(string lane, FakeReads server, long expiresIn = 3600, TimeSpan? readiness = null)
    {
        var (tokens, auth, http) = FakeAuth.Create(expiresIn);
        using (http)
        {
            var target = new ParityTarget(lane, "gru", Project, "api-perf-x", FakeReads.Id, 256, FakeReads.Document);
            using var observed = new ParityLane(target, tokens, _ => server);
            return await ParityCell.RunAsync(observed, target, 1, Plan(readiness), auth, tokens, TimeProvider.System, default).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Test]
    [Arguments("raw-h1", "1.1", HttpVersionPolicy.RequestVersionExact)]
    [Arguments("raw-h2", "2.0", HttpVersionPolicy.RequestVersionExact)]
    [Arguments("sdk", "2.0", HttpVersionPolicy.RequestVersionOrLower)]
    public async Task EachLaneKeepsItsVersionPolicyAndAcceptsOnlyExactSeeds(string lane, string version, HttpVersionPolicy policy)
    {
        var server = new FakeReads("ok");
        var row = await RunAsync(lane, server);
        await Assert.That(row.Rejection).IsNull();
        await Assert.That(server.Policies.Keys.Single()).IsEqualTo((Version.Parse(version), policy));
        await Assert.That(row.Attempts!.Value.Attempts).IsEqualTo(row.Window!.Starts);
        await Assert.That(row.Attempts!.Value.H2).IsEqualTo(version == "2.0" ? row.Window.Starts : 0);
        await Assert.That(row.Readiness.Attempts).IsEqualTo(1);
        // A fake handler opens no socket, so the connect count is unknown rather than zero.
        await Assert.That(row.Connects).IsNull();
        await Assert.That(row.WireReplays).IsNull();
        await Assert.That(row.AuthExchanges).IsEqualTo(0);
    }

    [Test]
    [Arguments("raw-h1", "h1", null)]
    [Arguments("raw-h2", "h1", "protocol")]
    [Arguments("sdk", "h1", null)]
    [Arguments("raw-h1", "region", "region")]
    [Arguments("sdk", "region", "region")]
    [Arguments("raw-h2", "document", "document")]
    [Arguments("sdk", "document", "document")]
    [Arguments("raw-h2", "retry", "503")]
    [Arguments("sdk", "retry", "attempts")]
    [Arguments("raw-h1", "500", "500")]
    [Arguments("sdk", "500", "500")]
    [Arguments("raw-h2", "401", "401")]
    [Arguments("sdk", "401", "401")]
    public async Task EveryLaneRejectsRetryBodyStatusRegionAndProtocolFaults(string lane, string variant, string? expected)
    {
        var row = await RunAsync(lane, new FakeReads(variant));
        await Assert.That(row.Rejection).IsEqualTo(expected);
        // The SDK's H2-or-lower negotiation is reported as observed, never forced.
        if (lane == "sdk" && variant == "h1") await Assert.That(row.Attempts!.Value.H1).IsEqualTo(row.Window!.Starts);
        // The shipped SDK neither refreshes nor resends a 401.
        if (variant == "401") await Assert.That(row.Attempts!.Value.Attempts).IsLessThanOrEqualTo(row.Window!.Starts);
    }

    [Test]
    [Arguments("raw-h1")]
    [Arguments("sdk")]
    public async Task ReadinessIsBoundedAndKeptOutOfTheWindowSnapshot(string lane)
    {
        var row = await RunAsync(lane, new FakeReads("missing"));
        await Assert.That(row.Rejection).IsNull();
        await Assert.That(row.Readiness.Attempts).IsEqualTo(3);
        await Assert.That(row.Attempts!.Value.Attempts).IsEqualTo(row.Window!.Starts);

        row = await RunAsync(lane, new FakeReads("absent"), readiness: TimeSpan.FromMilliseconds(50));
        await Assert.That(row.Rejection).IsEqualTo("readiness");
        await Assert.That(row.Readiness.Last).IsEqualTo("404");
        await Assert.That(row.Window).IsNull();
        await Assert.That(row.Attempts).IsNull();
    }

    [Test]
    [Arguments("raw-h2")]
    [Arguments("sdk")]
    public async Task TokenRefreshInsideACellRejectsItAndAuthStaysOffTheDataLane(string lane)
    {
        // A 60 s token is due for refresh at once, so every read exchanges again through the provider.
        var row = await RunAsync(lane, new FakeReads("ok"), expiresIn: 60);
        await Assert.That(row.Rejection).IsEqualTo("auth");
        await Assert.That(row.AuthExchanges).IsGreaterThan(0);
        await Assert.That(row.AuthPinned).IsEqualTo(0);
        await Assert.That(row.Attempts!.Value.WrongTarget).IsEqualTo(0);
        await Assert.That(row.Readiness.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task PinnedAuthRequestRejectsTheCell()
    {
        var (tokens, auth, http) = FakeAuth.Create(3600);
        using (http)
        {
            http.DefaultRequestHeaders.Add(TerraBaseRegion.PinHeader, "gru");
            var target = new ParityTarget("raw-h1", "gru", Project, "api-perf-x", FakeReads.Id, 256, FakeReads.Document);
            using var observed = new ParityLane(target, tokens, _ => new FakeReads("ok"));
            var row = await ParityCell.RunAsync(observed, target, 1, Plan(), auth, tokens, TimeProvider.System, default);
            await Assert.That(row.Rejection).IsEqualTo("auth_pin");
            await Assert.That(row.AuthPinned).IsEqualTo(1);
        }
    }
}
