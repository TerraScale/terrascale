// <copyright file="DirectTbepTests.cs" company="TerraScale">
// TerraBase (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

using System.Diagnostics;
using System.Text.Json;
using TerraBase.Protocol;

/// <summary>
/// The direct-TBEP mode rows of the AC: the CLI exercises the same
/// typed operation set against a local engine process, speaking
/// through the shared SE-14 <c>TerraBase.Protocol</c> client over the
/// required UDS binding (never a raw socket, never a C ABI). The
/// engine is the repository's real engine server, spawned by the
/// harness; the test commits through its own bound channel so the
/// changes tail follows real journal delivery.
/// </summary>
public sealed class DirectTbepTests
{
    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string socket, string[] args)
    {
        var environment = new Dictionary<string, string?> { ["TERRABASE_TBEP_SOCKET"] = socket };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exit = await Cli.RunAsync(["--mode", "tbep", "--json", .. args], stdout, stderr, bound.Token, environment);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static JsonElement Machine(string text) =>
        JsonDocument.Parse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Single()).RootElement.Clone();

    /// <summary>The direct mode reads the local engine's status and topology.</summary>
    [Test]
    public async Task DirectModeReadsTheLocalEngineStatus()
    {
        var (process, socket) = await EngineHarness.SpawnAsync();
        try
        {
            var (exit, stdout, stderr) = await RunAsync(socket, ["status"]);
            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(stderr).IsEmpty();
            var result = Machine(stdout).GetProperty("result");
            await Assert.That(result.GetProperty("health").GetProperty("engineState").GetString()).IsEqualTo("Serving");
            await Assert.That(result.GetProperty("topology").GetProperty("binding").GetString()).IsEqualTo("LocalUds");
            await Assert.That(result.GetProperty("admin").GetProperty("registryVersion").GetString()).IsNotNull();
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }

    /// <summary>The direct mode's document and query commands complete the typed round trips.</summary>
    [Test]
    public async Task DirectModeDocumentsAndQueryCompleteTheTypedRoundTrip()
    {
        var (process, socket) = await EngineHarness.SpawnAsync();
        try
        {
            var (readExit, _, readErr) = await RunAsync(socket, ["document", "read", "orders", "d-01"]);
            await Assert.That(readExit).IsEqualTo(2);
            await Assert.That(Machine(readErr).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("operation_refused");

            var (writeExit, _, writeErr) = await RunAsync(socket, ["document", "write", "orders", "d-01", """{"qty": 3}""", "--idempotency-key", "op-01"]);
            await Assert.That(writeExit).IsEqualTo(2);
            await Assert.That(Machine(writeErr).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("operation_refused");

            var (queryExit, _, queryErr) = await RunAsync(socket, ["query", "from orders"]);
            await Assert.That(queryExit).IsEqualTo(2);
            await Assert.That(Machine(queryErr).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("operation_refused");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }

    /// <summary>The direct mode's changes tail follows the local engine's Change Event Backbone.</summary>
    [Test]
    public async Task DirectModeChangesTailTheLocalEngine()
    {
        var (process, socket) = await EngineHarness.SpawnAsync();
        try
        {
            // The engine's kill starts before the driver connects: a
            // connect or bind failure must not leak the engine process.
            await using var driver = TbepChannel.Connect(socket);
            await driver.BindChannelAsync("backend-operator", "tenant-a").ConfigureAwait(false);
            var setup = await driver.SyncSessionSetupAsync("orders", null).ConfigureAwait(false);
            foreach (var operation in new[] { "op-01", "op-02" })
            {
                var answer = await driver.SyncMutationSubmitAsync(
                    setup.SessionId,
                    "d-01",
                    SyncChange.Put([new SyncField { Name = "note", Value = TbepValue.Of($"body-of-{operation}") }]),
                    operation).ConfigureAwait(false);
                await Assert.That(answer.Outcome.Variant).IsEqualTo("Applied");
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var environment = new Dictionary<string, string?> { ["TERRABASE_TBEP_SOCKET"] = socket };
            var exit = await Cli.RunAsync(["--mode", "tbep", "--json", "--tenant", "tenant-a", "--principal", "backend-operator", "changes", "--from", "1", "--idle", "400"], stdout, stderr, bound.Token, environment);
            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(stderr.ToString()).IsEmpty();
            var events = stdout.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Where(e => e.TryGetProperty("event", out _))
                .Select(e => e.GetProperty("event"))
                .ToList();
            var operationIds = events
                .Select(e => e.TryGetProperty("operationId", out var id) ? id.GetString() : null)
                .Where(id => id is not null)
                .ToList();
            await Assert.That(operationIds).Contains("op-01");
            await Assert.That(operationIds).Contains("op-02");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }

    /// <summary>
    /// The direct mode's follow tail detects an engine loss under the
    /// tail, notices the reconnect with its checkpoint, and resumes on a
    /// fresh engine process that takes the same socket — the direct-mode
    /// counterpart of the host family's dropped-connection row. The
    /// fixture owns both engine processes, so the kill is deterministic.
    /// </summary>
    [Test]
    public async Task DirectModeFollowResumesAfterTheEngineDies()
    {
        var (process, socket) = await EngineHarness.SpawnAsync();
        Process? replacement = null;
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var environment = new Dictionary<string, string?> { ["TERRABASE_TBEP_SOCKET"] = socket };
            var run = Cli.RunAsync(
                [
                    "--mode", "tbep", "--json", "--principal", "backend-operator", "--tenant", "tenant-a",
                    "changes", "--follow", "--from", "1",
                ],
                stdout,
                stderr,
                bound.Token,
                environment);

            var opened = false;
            for (var attempt = 0; attempt < 400 && !opened; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                opened = stdout.ToString().Contains("\"kind\":\"opened\"", StringComparison.Ordinal);
            }

            await Assert.That(opened).IsTrue();

            // The engine dies under the following tail.
            process.Kill(entireProcessTree: true);

            var reconnect = false;
            for (var attempt = 0; attempt < 400 && !reconnect; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                reconnect = stdout.ToString().Contains("\"kind\":\"reconnect\"", StringComparison.Ordinal);
            }

            await Assert.That(reconnect).IsTrue();

            // The engine stays away for a while: the tail keeps retrying
            // its re-subscription without crashing on the dead connection.
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Assert.That(run.IsCompleted).IsFalse();
            await Assert.That(run.IsFaulted).IsFalse();

            // A fresh engine takes the same socket: the tail must
            // re-subscribe from its checkpoint and deliver the new commit.
            var (fresh, _) = await EngineHarness.SpawnAsync(socket);
            replacement = fresh;
            await using var driver = TbepChannel.Connect(socket);
            await driver.BindChannelAsync("backend-operator", "tenant-a").ConfigureAwait(false);
            var setup = await driver.SyncSessionSetupAsync("orders", null).ConfigureAwait(false);
            var answer = await driver.SyncMutationSubmitAsync(
                setup.SessionId,
                "d-01",
                SyncChange.Put([new SyncField { Name = "note", Value = TbepValue.Of("body-of-op-restart") }]),
                "op-restart").ConfigureAwait(false);
            await Assert.That(answer.Outcome.Variant).IsEqualTo("Applied");

            var resumed = false;
            for (var attempt = 0; attempt < 400 && !resumed; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                resumed = stdout.ToString().Contains("op-restart", StringComparison.Ordinal);
            }

            await Assert.That(resumed).IsTrue();
            bound.Cancel();
            var exit = await run.ConfigureAwait(false);
            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(stderr.ToString()).IsEmpty();
            await MachineEnvelope.AssertStreamWellFormedAsync(stdout.ToString(), "changes", "tbep");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
            if (replacement is { } engine)
            {
                if (!engine.HasExited)
                {
                    engine.Kill(entireProcessTree: true);
                }

                engine.Dispose();
            }
        }
    }

    /// <summary>The direct mode answers the session diagnostics honestly: the registry is the host's, the engine counters are real.</summary>
    [Test]
    public async Task DirectModeSessionsReportTheEngineCountersHonestly()
    {
        var (process, socket) = await EngineHarness.SpawnAsync();
        try
        {
            // The engine's kill starts before the driver connects: a
            // connect or bind failure must not leak the engine process.
            await using var driver = TbepChannel.Connect(socket);
            await driver.BindChannelAsync("backend-operator", "tenant-a").ConfigureAwait(false);
            var setup = await driver.SyncSessionSetupAsync("orders", null).ConfigureAwait(false);
            await Assert.That(setup.SessionId).IsGreaterThan(0ul);

            var (exit, stdout, stderr) = await RunAsync(socket, ["sessions"]);
            await Assert.That(exit).IsEqualTo(0);
            var result = Machine(stdout).GetProperty("result");
            await Assert.That(result.GetProperty("available").GetBoolean()).IsFalse();
            await Assert.That(result.GetProperty("engineSessions").GetInt32()).IsGreaterThanOrEqualTo(1);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }

    /// <summary>The host-surface diagnostics fail closed in direct mode with the seam named.</summary>
    [Test]
    public async Task HostSurfaceDiagnosticsFailClosedInDirectMode()
    {
        var (process, socket) = await EngineHarness.SpawnAsync();
        try
        {
            var (exit, _, stderr) = await RunAsync(socket, ["audit"]);
            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(stderr).Contains("host");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }
}
