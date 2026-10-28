using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ApiBench;
using TUnit.Core;
using static BenchClient.Tests.ParityRunTests;

namespace BenchClient.Tests;

[SupportedOSPlatform("linux")]
public sealed class ParityLifecycleTests
{
    [Test]
    [Arguments("create-transport", "delete-404", "uncertain", "absent", true, "setup:transport")]
    [Arguments("create-refused", "delete-404", "refused", "absent", false, "setup:403")]
    [Arguments("ok", "delete-500", "created", "failed", true, null)]
    public async Task CleanupReportsDeletionAndReconciliationForEveryCreateOutcome(string create, string delete, string state, string deletion, bool reconcile, string? reason)
    {
        using var run = new Run();
        await Assert.That(await run.StartAsync(new FakeTerraBase(create, delete))).IsEqualTo(1);
        var rows = run.Rows();
        var cleanup = rows[^2];
        await Assert.That(Text(cleanup, "create")).IsEqualTo(state);
        await Assert.That(Text(cleanup, "deletion")).IsEqualTo(deletion);
        await Assert.That(cleanup.GetProperty("reconcile_required").GetBoolean()).IsEqualTo(reconcile);
        await Assert.That(Text(cleanup, "remote_erasure")).IsEqualTo("unverified");
        await Assert.That(rows[^1].GetProperty("accepted").GetBoolean()).IsFalse();
        await Assert.That(rows[^1].GetProperty("reason").GetString()).IsEqualTo(reason);
        // Only the owned synthetic name, the deliberate public recovery identity, may appear in diagnostics.
        await Assert.That(run.Error.ToString().Contains(Text(rows[1], "database"), StringComparison.Ordinal)).IsEqualTo(reconcile);
        await Assert.That(run.Error.ToString().Contains(FakeAuth.Key, StringComparison.Ordinal)).IsFalse();
        await Assert.That(run.Error.ToString().Contains(Project, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task SetupDeadlineCoversEveryStepAndCleanupKeepsItsOwnDeadline()
    {
        using var run = new Run();
        var server = new FakeTerraBase("collection-hang");
        await Assert.That(await run.StartAsync(server, server.Rig(setup: TimeSpan.FromMilliseconds(100)))).IsEqualTo(1);
        var rows = run.Rows();
        await Assert.That(rows[^1].GetProperty("reason").GetString()).IsEqualTo("setup:timeout");
        await Assert.That(Text(rows[^2], "create")).IsEqualTo("created");
        await Assert.That(Text(rows[^2], "deletion")).IsEqualTo("logical_accepted");
        await Assert.That(server.DeleteCanceled).IsFalse();
    }

    [Test]
    public async Task RepeatedSignalsStayCaughtAndCleanupOfAnUncertainCreateCompletes()
    {
        using var run = new Run();
        var server = new FakeTerraBase("create-block", "delete-block");
        using var work = new CancellationTokenSource();
        var running = run.StartAsync(server, interrupt: work.Token);
        await server.CreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var signals = new[] { new PosixSignalContext(PosixSignal.SIGINT), new PosixSignalContext(PosixSignal.SIGTERM), new PosixSignalContext(PosixSignal.SIGINT) };
        ApiBench.Program.Catch(signals[0], work);
        await server.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Further signals arrive while the owned database is being deleted.
        ApiBench.Program.Catch(signals[1], work);
        ApiBench.Program.Catch(signals[2], work);
        server.DeleteRelease.SetResult();
        await Assert.That(await running).IsEqualTo(1);
        await Assert.That(signals.All(signal => signal.Cancel)).IsTrue();
        await Assert.That(server.DeleteCanceled).IsFalse();
        var rows = run.Rows();
        await Assert.That(Text(rows[^2], "create")).IsEqualTo("uncertain");
        await Assert.That(Text(rows[^2], "deletion")).IsEqualTo("logical_accepted");
        await Assert.That(rows[^2].GetProperty("reconcile_required").GetBoolean()).IsFalse();
        await Assert.That(rows[^1].GetProperty("reason").GetString()).IsEqualTo("setup:interrupted");
    }

    [Test]
    public async Task InterruptInsideACellEmitsItsEvidenceBeforeCleanup()
    {
        using var run = new Run();
        var server = new FakeTerraBase();
        using var work = new CancellationTokenSource();
        var rig = server.Rig() with
        {
            Plan = concurrency => new ParityCellPlan(new ParityWindowPlan(concurrency, TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(10), TimeSpan.Zero),
        };
        var running = run.StartAsync(server, rig, interrupt: work.Token);
        // Setup takes four requests and readiness one, so the eighth is inside the first cell's window.
        await Task.Run(async () => { while (server.Requests < 8) await Task.Yield(); }).WaitAsync(TimeSpan.FromSeconds(10));
        ApiBench.Program.Catch(new PosixSignalContext(PosixSignal.SIGTERM), work);
        await Assert.That(await running).IsEqualTo(1);
        var rows = run.Rows();
        await Assert.That(Text(rows[^3], "type")).IsEqualTo("cell");
        await Assert.That(Text(rows[^3], "rejection")).IsEqualTo("interrupted");
        await Assert.That(Text(rows[^2], "deletion")).IsEqualTo("logical_accepted");
        await Assert.That(rows[^1].GetProperty("reason").GetString()).IsEqualTo("cells:interrupted");
    }
}
