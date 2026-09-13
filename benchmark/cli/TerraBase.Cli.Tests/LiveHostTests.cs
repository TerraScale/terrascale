// <copyright file="LiveHostTests.cs" company="TerraScale">
// TerraBase (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

using System.Text.Json;

/// <summary>
/// The live API host fixture rows of the AC: the CLI drives the real
/// ASP.NET Core host over its typed HTTP surface, with the real Rust
/// engine process the supervisor spawns and watches over UDS (the
/// SE-08 harness pattern — the same served surface the production
/// entry point runs). The status, sessions, audit, and metrics rows
/// serve real engine answers; the document and query rows complete
/// their typed round trips and surface the engine's truthful typed
/// refusal until the engine-side host wiring lands (the named
/// integrator action of the SE-14 record, exactly as the SE-08 family
/// pinned it).
/// </summary>
public sealed class LiveHostTests
{
    private static IReadOnlyDictionary<string, string?> Environment(string url) =>
        new Dictionary<string, string?> { ["TERRABASE_HOST_URL"] = url };

    private static async Task<int> RunAsync(
        HostFixture host,
        string[] args,
        StringWriter stdout,
        StringWriter stderr,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var environment = new Dictionary<string, string?> { ["TERRABASE_HOST_URL"] = host.Url };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                environment[key] = value;
            }
        }

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await Cli.RunAsync(args, stdout, stderr, bound.Token, environment);
    }

    private static (JsonElement Result, string Text) Machine(StringWriter stdout)
    {
        var text = stdout.ToString();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
        {
            throw new InvalidOperationException($"expected exactly one machine line, saw {lines.Length}: {text}");
        }

        return (JsonDocument.Parse(lines[0]).RootElement.Clone(), text);
    }

    /// <summary>status answers the engine and topology summary in the exact replication vocabulary.</summary>
    [Test]
    public async Task StatusAnswersTheEngineAndTopologySummary()
    {
        await using var host = await HostFixture.StartAsync();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await RunAsync(host, ["status"], stdout, stderr);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stderr.ToString()).IsEmpty();
        var human = stdout.ToString();
        await Assert.That(human).Contains("Engine");
        await Assert.That(human).Contains("Serving");
        await Assert.That(human).Contains("TopologyView");
        await Assert.That(human).Contains("LocalUds");
        // Truthful, not invented: the engine publishes no TopologyView at
        // this binding, so no Region, Member, or Failure Domain is claimed.
        await Assert.That(human).Contains("not reported");

        var machineOut = new StringWriter();
        var machineErr = new StringWriter();
        await RunAsync(host, ["--json", "status"], machineOut, machineErr);
        var (root, text) = Machine(machineOut);
        await Assert.That(root.GetProperty("result").GetProperty("health").GetProperty("engineState").GetString()).IsEqualTo("Serving");
        await Assert.That(root.GetProperty("result").GetProperty("health").GetProperty("status").GetString()).IsEqualTo("serving");
        await Assert.That(root.GetProperty("result").GetProperty("topology").GetProperty("binding").GetString()).IsEqualTo("LocalUds");
        await Assert.That(root.GetProperty("result").GetProperty("topology").GetProperty("engineTopologyAvailable").GetBoolean()).IsFalse();
        await Assert.That(root.GetProperty("result").GetProperty("admin").GetProperty("registryVersion").GetString()).IsNotNull();

        // No credentials exist on this local surface and none are echoed:
        // the envelope names no header, no principal, no tenant.
        await Assert.That(text).DoesNotContain("Authorization");
        await Assert.That(text).DoesNotContain("Bearer");
        await Assert.That(text).DoesNotContain("X-TB-");
    }

    /// <summary>The document commands complete their typed round trips; the engine answers its truthful typed refusal.</summary>
    [Test]
    public async Task DocumentCommandsCompleteTheTypedRoundTrip()
    {
        await using var host = await HostFixture.StartAsync();
        var caller = host.As("backend-operator", "tenant-a");

        var read = new StringWriter();
        var readErr = new StringWriter();
        var readExit = await RunAsync(host, ["--json", "document", "read", "orders", "d-01"], read, readErr);
        await Assert.That(readExit).IsEqualTo(2);
        var (readError, _) = Machine(readErr);
        await Assert.That(readError.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("operation_refused");

        var write = new StringWriter();
        var writeErr = new StringWriter();
        var writeExit = await RunAsync(host, ["--json", "document", "write", "orders", "d-01", """{"qty": 3}""", "--idempotency-key", "op-01"], write, writeErr);
        await Assert.That(writeExit).IsEqualTo(2);
        var (writeError, _) = Machine(writeErr);
        await Assert.That(writeError.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("operation_refused");

        var delete = new StringWriter();
        var deleteErr = new StringWriter();
        var deleteExit = await RunAsync(host, ["--json", "document", "delete", "orders", "d-01", "--idempotency-key", "op-02"], delete, deleteErr);
        await Assert.That(deleteExit).IsEqualTo(2);

        var browse = new StringWriter();
        var browseErr = new StringWriter();
        var browseExit = await RunAsync(host, ["--json", "document", "browse", "orders"], browse, browseErr);
        await Assert.That(browseExit).IsEqualTo(2);
        var (browseError, _) = Machine(browseErr);
        await Assert.That(browseError.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("operation_refused");
        await Assert.That(caller).IsNotNull();
    }

    /// <summary>The query commands complete their typed round trips through TQL text.</summary>
    [Test]
    public async Task QueryCommandsCompleteTheTypedRoundTrip()
    {
        await using var host = await HostFixture.StartAsync();

        var query = new StringWriter();
        var queryErr = new StringWriter();
        var queryExit = await RunAsync(host, ["--json", "query", "from orders"], query, queryErr);
        await Assert.That(queryExit).IsEqualTo(2);
        var (queryError, _) = Machine(queryErr);
        await Assert.That(queryError.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("operation_refused");

        var cancel = new StringWriter();
        var cancelErr = new StringWriter();
        var cancelExit = await RunAsync(host, ["--json", "query", "cancel", "1"], cancel, cancelErr);
        await Assert.That(cancelExit).IsEqualTo(2);
    }

    /// <summary>The session and admin diagnostics report the live registry, audit, and metrics.</summary>
    [Test]
    public async Task SessionAndAdminDiagnosticsReport()
    {
        await using var host = await HostFixture.StartAsync();
        var caller = host.As("backend-operator", "tenant-a");
        var sessionId = await HostFixture.OpenSessionAsync(caller, "orders");

        var sessions = new StringWriter();
        var sessionsErr = new StringWriter();
        var sessionsExit = await RunAsync(
            host,
            ["--json", "sessions"],
            sessions,
            sessionsErr,
            new Dictionary<string, string?> { ["TERRABASE_TENANT"] = "tenant-a" });
        await Assert.That(sessionsExit).IsEqualTo(0);
        var (sessionsResult, _) = Machine(sessions);
        var listed = sessionsResult.GetProperty("result").GetProperty("sessions").EnumerateArray().Single();
        await Assert.That(listed.GetProperty("sessionId").GetUInt64()).IsEqualTo(sessionId);
        await Assert.That(listed.GetProperty("collection").GetString()).IsEqualTo("orders");

        var audit = new StringWriter();
        var auditErr = new StringWriter();
        var auditExit = await RunAsync(host, ["--json", "audit"], audit, auditErr);
        await Assert.That(auditExit).IsEqualTo(0);
        var (auditResult, auditText) = Machine(audit);
        await Assert.That(auditResult.GetProperty("result").GetProperty("records").GetArrayLength()).IsGreaterThan(0);
        // The audit discipline rides through: no bodies, no credentials.
        await Assert.That(auditText).DoesNotContain("body-of-");
        await Assert.That(auditText).DoesNotContain("Authorization");

        var metrics = new StringWriter();
        var metricsErr = new StringWriter();
        var metricsExit = await RunAsync(host, ["--json", "metrics"], metrics, metricsErr);
        await Assert.That(metricsExit).IsEqualTo(0);
        var (metricsResult, _) = Machine(metrics);
        await Assert.That(metricsResult.GetProperty("result").GetProperty("engineGeneration").GetInt32()).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>The engine subcommands serve the typed reads.</summary>
    [Test]
    public async Task EngineSubcommandsServeTheTypedReads()
    {
        await using var host = await HostFixture.StartAsync();
        foreach (var subcommand in new[] { "health", "status", "topology" })
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = await RunAsync(host, ["engine", subcommand], stdout, stderr);
            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(stdout.ToString()).IsNotEmpty();
        }
    }

    /// <summary>The engine shutdown answers the draining state and ends the generation.</summary>
    [Test]
    public async Task EngineShutdownAnswersTheDrainingState()
    {
        await using var host = await HostFixture.StartAsync();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await RunAsync(host, ["--json", "engine", "shutdown", "--reason", "operator"], stdout, stderr);
        await Assert.That(exit).IsEqualTo(0);
        var (result, _) = Machine(stdout);
        await Assert.That(result.GetProperty("result").GetProperty("state").GetString()).IsEqualTo("Draining");
    }

    /// <summary>A host that cannot be reached answers the typed unavailable exit, never a hang.</summary>
    [Test]
    public async Task AnUnreachableHostAnswersTheTypedUnavailableExit()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exit = await Cli.RunAsync(
            ["--json", "--url", "http://127.0.0.1:9", "status"],
            stdout,
            stderr,
            bound.Token,
            new Dictionary<string, string?>());
        await Assert.That(exit).IsEqualTo(3);
        var line = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Single();
        var envelope = JsonDocument.Parse(line).RootElement;
        await Assert.That(envelope.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
    }
}
