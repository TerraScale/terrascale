using System.Runtime.Versioning;
using ApiBench;
using TUnit.Core;
using static BenchClient.Tests.ParityRunTests;

namespace BenchClient.Tests;

[SupportedOSPlatform("linux")]
public sealed class ParityReviewTests
{
    [Test]
    [Arguments(null, "operation")]
    [Arguments("document", "document")]
    public async Task SuccessfulHeadersCannotOverrideAnOperationFailure(string? failure, string expected)
    {
        var window = await ParityWindow.RunAsync(_ => Task.FromException(new InvalidOperationException("synthetic worker failure")),
            new ParityWindowPlan(1, TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), TimeProvider.System, default);
        var successfulHeaders = new ParityAttemptSnapshot(window.Starts, window.Starts, window.Starts, 0, 0, 0, 0, 0);
        await Assert.That(window.Rejection).IsEqualTo("operation");
        await Assert.That(ParityCell.Rejection(window, failure, default, successfulHeaders)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationAfterTheLastCellStillRejectsTheOutcomeAndKeepsItsCause(bool runLimit)
    {
        using var run = new Run();
        using var interrupt = new CancellationTokenSource();
        var clock = new ExpiringClock();
        var server = new FakeTerraBase { OnDelete = runLimit ? clock.Expire : interrupt.Cancel };
        var rig = server.Rig() with { Clock = clock, RunLimit = ExpiringClock.Limit };
        await Assert.That(await run.StartAsync(server, rig, interrupt: interrupt.Token)).IsEqualTo(1);
        var rows = run.Rows();
        var cells = rows.Where(row => Text(row, "type") == "cell").ToArray();
        await Assert.That(cells.Length).IsEqualTo(108);
        await Assert.That(cells.All(cell => cell.GetProperty("rejection").ValueKind == System.Text.Json.JsonValueKind.Null)).IsTrue();
        await Assert.That(Text(rows[^2], "deletion")).IsEqualTo("logical_accepted");
        await Assert.That(rows[^1].GetProperty("accepted").GetBoolean()).IsFalse();
        await Assert.That(Text(rows[^1], "reason")).IsEqualTo(runLimit ? "cells:run_limit" : "cells:interrupted");
    }

    // Trigger only the run-limit timer after the final cell; other timers retain their normal behavior.
    private sealed class ExpiringClock : RequestClock
    {
        internal static readonly TimeSpan Limit = TimeSpan.FromMinutes(30);
        private Action? expire;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Limit) return base.CreateTimer(callback, state, dueTime, period);
            expire = () => callback(state);
            return base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
        }

        internal void Expire() => expire!();
    }
}
