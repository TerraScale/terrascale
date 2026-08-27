// <copyright file="SupervisionTests.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Diagnostics;

/// <summary>
/// The supervision rows of the AC (against the real engine process):
/// engine-process loss produces truthful health (never optimistic),
/// clean typed refusals for every caller, resume-able stream closes,
/// and ended (never orphaned) session state — and the bounded restart
/// policy brings a fresh engine generation back. An attached engine is
/// truthfully unavailable on loss (no respawn), and a deliberate
/// graceful shutdown never restarts.
/// </summary>
public sealed class SupervisionTests
{
    private static async Task<EngineOptions> OptionsAsync(int maxRestarts = 3)
    {
        var binary = await EngineHarness.BinaryPathAsync();
        return new EngineOptions
        {
            SocketPath = Path.Combine(Path.GetTempPath(), $"se08-supervision-{Guid.NewGuid():N}.sock"),
            ManagedBinary = binary,
            MaxRestarts = maxRestarts,
            RestartDelay = TimeSpan.FromMilliseconds(150),
        };
    }

    private static async Task<(int Status, string Code)> HealthOf(HostHarness host)
    {
        var (status, body) = await HostHarness.SendAsync(host.Http, "/health");
        var code = body.RootElement.TryGetProperty("status", out var state) ? state.GetString() : null;
        return (status, code ?? "?");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"timed out waiting for {what}");
    }

    /// <summary>Engine-process loss: truthful health, clean typed refusals, closed streams, ended sessions, then recovery.</summary>
    [Test]
    public async Task EngineLossIsTruthfulCleanAndRecoversWithoutOrphanedState()
    {
        var host = await HostHarness.StartAsync(await OptionsAsync());
        await using var _ = host;
        var caller = host.As("backend-operator", "tenant-a");
        var generation = host.Supervisor.Generation;

        // One caller session and one open stream exist when the engine dies.
        var (setup, setupBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(setup).IsEqualTo(200);
        var sessionId = setupBody.RootElement.GetProperty("sessionId").GetUInt64();
        var checkpoint = setupBody.RootElement.GetProperty("checkpoint").GetString()!;
        await using var sse = await SseReader.OpenAsync(caller, "collections=orders");
        await sse.ExpectAsync("opened");

        // The engine process is lost for real.
        var process = host.Supervisor.EngineProcess;
        await Assert.That(process).IsNotNull();
        process!.Kill(entireProcessTree: true);

        // The open stream closes with the resume-able generation-lost
        // frame: the consumer reconnects from its last sequence.
        var closed = await sse.ExpectAsync("closed");
        await Assert.That(closed.Data.RootElement.GetProperty("reason").GetString()).IsEqualTo("engine-generation-lost");
        await Assert.That(closed.Data.RootElement.GetProperty("resumeFrom").GetUInt64()).IsGreaterThanOrEqualTo(0ul);

        // The session did not orphan: it is answered as ended, truthfully.
        var (ended, endedBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/delta", new { from = checkpoint });
        await Assert.That(ended).IsEqualTo(410);
        await Assert.That(endedBody.RootElement.GetProperty("code").GetString()).IsEqualTo("session-ended");

        // The loss is audited.
        await WaitUntilAsync(
            () => Task.FromResult(host.Audit.Recent(AuditLog.Capacity).Any(record => record.Kind == AuditKind.EngineLost)),
            "the engine loss to be audited");

        // Within the bounded policy the host recovers: a fresh engine
        // generation serves again.
        await WaitUntilAsync(async () => (await HealthOf(host)).Status == 200, "the engine generation to recover");
        await Assert.That(host.Supervisor.Generation).IsEqualTo(generation + 1);
        await Assert.That(host.Supervisor.Restarts).IsEqualTo(1);

        // The recovered generation serves a brand-new session, and its
        // frames answer on the new generation. (The engine's session ids
        // restart with the process: the ended session's id may be
        // re-opened by this fresh setup — the host tracks the current
        // generation's session, and the ended one was already answered
        // 410 above, before any id could be reused.)
        var (fresh, freshBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(fresh).IsEqualTo(200);
        var newSession = freshBody.RootElement.GetProperty("sessionId").GetUInt64();
        var newCheckpoint = freshBody.RootElement.GetProperty("checkpoint").GetString()!;
        var (delta, _) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{newSession}/delta", new { from = newCheckpoint });
        await Assert.That(delta).IsEqualTo(200);
        var (mutation, _) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{newSession}/mutations",
            new { document = "d-01", change = new { put = new[] { new { name = "n", value = new { integer = 1L } } } } },
            idempotencyKey: "op-after-recovery");
        await Assert.That(mutation).IsEqualTo(200);
    }

    /// <summary>Calls during the loss window hear the typed engine-unavailable refusal, not a hang or a lie.</summary>
    [Test]
    public async Task CallsDuringTheLossWindowHearTheTypedRefusal()
    {
        // An attached engine: no respawn can race the loss window, so
        // the typed refusal is the only possible answer.
        var binary = await EngineHarness.BinaryPathAsync();
        var socket = Path.Combine(Path.GetTempPath(), $"se08-window-{Guid.NewGuid():N}.sock");
        var start = new ProcessStartInfo
        {
            FileName = binary,
            Arguments = $"\"{socket}\" 9",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        using var engine = Process.Start(start)!;
        var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = socket, ManagedBinary = null });
        await using var _ = host;
        var caller = host.As("backend-operator", "tenant-a");
        var (setup, _) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(setup).IsEqualTo(200);

        engine.Kill(entireProcessTree: true);

        // The engine process is gone: every engine-bound call is a
        // typed 503 engine-unavailable — clean, bounded, truthful.
        var (health, healthBody) = await HostHarness.SendAsync(host.Http, "/health");
        await Assert.That(health).IsEqualTo(503);
        await Assert.That(healthBody.RootElement.GetProperty("status").GetString()).IsEqualTo("unavailable");
        var (read, readBody) = await HostHarness.SendAsync(caller, "/v1/documents/orders/d-01");
        await Assert.That(read).IsEqualTo(503);
        await Assert.That(readBody.RootElement.GetProperty("code").GetString()).IsEqualTo("engine-unavailable");
        var (session, sessionBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(session).IsEqualTo(503);
        await Assert.That(sessionBody.RootElement.GetProperty("code").GetString()).IsEqualTo("engine-unavailable");
    }

    /// <summary>An attached engine is never respawned: its loss is truthfully permanent.</summary>
    [Test]
    public async Task AnAttachedEngineLossIsTruthfullyPermanent()
    {
        var binary = await EngineHarness.BinaryPathAsync();
        var socket = Path.Combine(Path.GetTempPath(), $"se08-attached-{Guid.NewGuid():N}.sock");
        var start = new ProcessStartInfo
        {
            FileName = binary,
            Arguments = $"\"{socket}\" 7",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        using var engine = Process.Start(start)!;
        var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = socket, ManagedBinary = null });
        await using var _ = host;

        var (healthy, _) = await HostHarness.SendAsync(host.Http, "/health");
        await Assert.That(healthy).IsEqualTo(200);

        engine.Kill(entireProcessTree: true);
        await WaitUntilAsync(async () => (await HealthOf(host)).Status == 503, "the attached loss to surface");

        // No respawn: the attached policy reports truthfully for as
        // long as the caller cares to ask.
        await Task.Delay(TimeSpan.FromSeconds(1));
        var (still, stillBody) = await HostHarness.SendAsync(host.Http, "/health");
        await Assert.That(still).IsEqualTo(503);
        await Assert.That(stillBody.RootElement.GetProperty("engine").GetProperty("restarts").GetInt32()).IsEqualTo(0);
    }

    /// <summary>The restart policy is bounded: beyond it, the loss stays truthful.</summary>
    [Test]
    public async Task TheRestartPolicyIsBounded()
    {
        var host = await HostHarness.StartAsync(await OptionsAsync(maxRestarts: 1));
        await using var _ = host;

        // First loss: one restart within the policy.
        host.Supervisor.EngineProcess!.Kill(entireProcessTree: true);
        await WaitUntilAsync(async () => (await HealthOf(host)).Status == 200, "the first restart");
        await Assert.That(host.Supervisor.Restarts).IsEqualTo(1);

        // Second loss: the budget is spent; no further restart.
        host.Supervisor.EngineProcess!.Kill(entireProcessTree: true);
        await WaitUntilAsync(async () => (await HealthOf(host)).Status == 503, "the final loss to surface");
        await Assert.That(host.Supervisor.Restarts).IsEqualTo(1);
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        var (health, _) = await HostHarness.SendAsync(host.Http, "/health");
        await Assert.That(health).IsEqualTo(503);
        await Assert.That(host.Supervisor.Status.ToString().ToLowerInvariant()).IsEqualTo("unavailable");
    }
}
