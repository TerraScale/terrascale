// <copyright file="ChangesFollowTests.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

using System.Text.Json;

/// <summary>
/// The Change Event Backbone rows of the AC (against the live host and
/// the real engine): the <c>changes</c> tail replays the retained
/// window and ends at its idle bound, the human lines use the glossary
/// vocabulary exactly (change event, Change Notification, content-free
/// triggers), and <c>--follow</c> reconnects with checkpoint resume
/// through a dropped connection — the missed range is re-delivered
/// exactly once, with no duplicate.
/// </summary>
[NotInParallel]
public sealed class ChangesFollowTests
{
    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        HostFixture host,
        string[] args,
        CancellationToken cancellation = default)
    {
        // The same backend-supplied tenant the commit driver uses: the
        // engine scopes every journal and registry by tenant, so the
        // tail must present the same context the commits landed under.
        var environment = new Dictionary<string, string?>
        {
            ["TERRABASE_HOST_URL"] = host.Url,
            ["TERRABASE_PRINCIPAL"] = "backend-operator",
            ["TERRABASE_TENANT"] = "tenant-a",
        };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellation, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        var exit = await Cli.RunAsync(args, stdout, stderr, bound.Token, environment);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static List<JsonElement> Envelopes(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone())];

    /// <summary>The tail replays the retained window and ends at its idle bound.</summary>
    [Test]
    public async Task TheTailReplaysTheRetainedWindowAndEnds()
    {
        await using var host = await HostFixture.StartAsync();
        var caller = host.As("backend-operator", "tenant-a");
        var sessionId = await HostFixture.OpenSessionAsync(caller, "orders");
        await HostFixture.CommitAsync(caller, sessionId, "d-01", "op-01");
        await HostFixture.CommitAsync(caller, sessionId, "d-02", "op-02");

        var (exit, stdout, stderr) = await RunAsync(host, ["--json", "changes", "--from", "1", "--idle", "300"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        // Every machine line of the tail — the events and the lifecycle
        // notices — carries the stable header and exactly one payload.
        var envelopes = await MachineEnvelope.AssertStreamWellFormedAsync(stdout, "changes", "host");
        await Assert.That(envelopes.Any(e => e.TryGetProperty("event", out _))).IsTrue();
        await Assert.That(envelopes.Any(e => e.TryGetProperty("notice", out _))).IsTrue();
        var events = envelopes.Where(e => e.TryGetProperty("event", out _)).ToList();
        var operationIds = events
            .Select(e => e.GetProperty("event").TryGetProperty("operationId", out var id) ? id.GetString() : null)
            .Where(id => id is not null)
            .ToList();
        await Assert.That(operationIds).Contains("op-01");
        await Assert.That(operationIds).Contains("op-02");
        var sequences = events
            .Select(e => e.GetProperty("event").TryGetProperty("sequence", out var s) ? s.GetInt64() : -1)
            .Where(s => s > 0)
            .ToList();
        await Assert.That(sequences.SequenceEqual(sequences.Distinct())).IsTrue();
        await Assert.That(sequences).IsInOrder();
    }

    /// <summary>The human lines use the glossary vocabulary exactly; triggers are content-free.</summary>
    [Test]
    public async Task TheHumanLinesUseTheGlossaryVocabularyExactly()
    {
        await using var host = await HostFixture.StartAsync();
        var caller = host.As("backend-operator", "tenant-a");
        var sessionId = await HostFixture.OpenSessionAsync(caller, "orders");
        await HostFixture.CommitAsync(caller, sessionId, "d-01", "op-01");

        var (exit, stdout, _) = await RunAsync(host, ["changes", "--from", "1", "--idle", "400"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout).Contains("change event");
        await Assert.That(stdout).Contains("collection=orders");
        await Assert.That(stdout).Contains("operationId=op-01");

        // The live commit's trigger is delivered as the content-free
        // Change Notification and names no document data; the operator
        // stops the tail with the usual interrupt.
        var followOut = new StringWriter();
        var followErr = new StringWriter();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var environment = new Dictionary<string, string?>
        {
            ["TERRABASE_HOST_URL"] = host.Url,
            ["TERRABASE_PRINCIPAL"] = "backend-operator",
            ["TERRABASE_TENANT"] = "tenant-a",
        };
        var follow = Cli.RunAsync(["changes", "--follow", "--from", "1"], followOut, followErr, bound.Token, environment);
        var notified = false;
        for (var attempt = 0; attempt < 400 && !notified; attempt++)
        {
            await Task.Delay(50).ConfigureAwait(false);
            var text = followOut.ToString();
            if (text.Contains("op-01", StringComparison.Ordinal) && text.Contains("Change Notification", StringComparison.Ordinal))
            {
                notified = true;
                bound.Cancel();
            }
            else if (attempt == 100)
            {
                await HostFixture.CommitAsync(caller, sessionId, "d-02", "op-live").ConfigureAwait(false);
            }
        }

        var followExit = await follow.ConfigureAwait(false);
        await Assert.That(notified).IsTrue();
        await Assert.That(followExit).IsEqualTo(0);
        await Assert.That(followOut.ToString()).DoesNotContain("body-of-");
    }

    /// <summary>The follow tail reconnects with checkpoint resume through a dropped connection.</summary>
    [Test]
    public async Task TheFollowTailReconnectsThroughADroppedConnection()
    {
        await using var host = await HostFixture.StartAsync();
        var caller = host.As("backend-operator", "tenant-a");
        var sessionId = await HostFixture.OpenSessionAsync(caller, "orders");
        await HostFixture.CommitAsync(caller, sessionId, "d-01", "op-01");
        await HostFixture.CommitAsync(caller, sessionId, "d-02", "op-02");

        var handler = new DroppingHandler();
        CliTransport.HandlerFactory = () => new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var environment = new Dictionary<string, string?>
            {
                ["TERRABASE_HOST_URL"] = host.Url,
                ["TERRABASE_PRINCIPAL"] = "backend-operator",
                ["TERRABASE_TENANT"] = "tenant-a",
            };
            var run = Cli.RunAsync(["--json", "changes", "--follow", "--from", "1", "--max-events", "3"], stdout, stderr, bound.Token, environment);

            // Wait until the tail consumed op-02, then drop the connection
            // under it, and commit op-03 while it is away.
            var seen = false;
            for (var attempt = 0; attempt < 400 && !seen; attempt++)
            {
                await Task.Delay(50).ConfigureAwait(false);
                seen = stdout.ToString().Contains("op-02", StringComparison.Ordinal);
            }

            await Assert.That(seen).IsTrue();
            handler.Drop();
            await handler.Dropped.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await HostFixture.CommitAsync(caller, sessionId, "d-03", "op-03");

            var exit = await run.ConfigureAwait(false);
            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(stderr.ToString()).IsEmpty();
            var envelopes = Envelopes(stdout.ToString());
            var events = envelopes.Where(e => e.TryGetProperty("event", out _))
                .Select(e => e.GetProperty("event"))
                .Where(e => e.GetProperty("kind").GetString() == "change-event")
                .ToList();
            await Assert.That(events.Count).IsEqualTo(3);
            var operationIds = events.Select(e => e.GetProperty("operationId").GetString()).ToList();
            await Assert.That(operationIds).Contains("op-03");
            // Exactly one delivery of each change event — the checkpoint
            // resume re-delivers the missed range, never a duplicate.
            await Assert.That(operationIds.Count(id => id == "op-01")).IsEqualTo(1);
            await Assert.That(operationIds.Count(id => id == "op-02")).IsEqualTo(1);

            // The reconnect surfaced as a notice carrying the checkpoint
            // (the last seen change-event sequence the resume follows).
            var reconnect = envelopes.Where(e => e.TryGetProperty("notice", out _))
                .Select(e => e.GetProperty("notice"))
                .Single(n => n.GetProperty("kind").GetString() == "reconnect");
            await Assert.That(reconnect.GetProperty("checkpoint").GetInt64()).IsGreaterThan(0);
        }
        finally
        {
            CliTransport.HandlerFactory = null;
            handler.Dispose();
        }
    }
}
