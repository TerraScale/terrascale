// <copyright file="DirectTbepTests.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

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
        await using var driver = TbepChannel.Connect(socket);
        try
        {
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

    /// <summary>The direct mode answers the session diagnostics honestly: the registry is the host's, the engine counters are real.</summary>
    [Test]
    public async Task DirectModeSessionsReportTheEngineCountersHonestly()
    {
        var (process, socket) = await EngineHarness.SpawnAsync();
        await using var driver = TbepChannel.Connect(socket);
        try
        {
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
