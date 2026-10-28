using System.Text.Json.Serialization;

namespace ApiBench;

internal sealed record ParityCellPlan(ParityWindowPlan Window, TimeSpan ReadinessLimit, TimeSpan ReadinessPause)
{
    internal static ParityCellPlan Live(int concurrency) => new(ParityWindowPlan.Live(concurrency), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100));

    // Readiness, warmup, window and drain must all fit before a cached token is due to refresh.
    internal TimeSpan TokenHorizon => ReadinessLimit + Window.Warmup + Window.Measure + Window.Drain;
}

internal sealed record ParityReadiness(long Attempts, long? Connects, double Ms, string? Last);

/// <summary>
/// One emitted cell. Attempts, connects and auth cover warmup, window and drain; readiness is separate. Percentiles are
/// approximate (16,384-sample reservoir). Socket-level replays inside SocketsHttpHandler are invisible, so they are null.
/// </summary>
internal sealed record ParityCellRow(string Type, string Region, string Lane, int Repeat, int PayloadBytes, int Concurrency,
    ParityReadiness Readiness, ParityWindowResult? Window, ParityAttemptSnapshot? Attempts, long? Connects,
    long AuthExchanges, long AuthPinned, long? WireReplays, string Percentiles, string? Rejection);

internal static class ParityCell
{
    internal static async Task<ParityCellRow> RunAsync(ParityLane lane, ParityTarget target, int repeat, ParityCellPlan plan,
        ParityAuth auth, Tokens tokens, TimeProvider clock, CancellationToken interrupt)
    {
        await tokens.PrewarmAsync(plan.TokenHorizon, interrupt);
        var authBefore = auth.Snapshot.Attempts;
        long? Connects(long since) => lane.Connections.Counted ? lane.Connections.Count - since : null;
        ParityCellRow Row(ParityReadiness readiness, ParityWindowResult? window, ParityAttemptSnapshot? attempts, long? connects, string? rejection)
        {
            // Any exchange after the prewarm is inside the cell; a pinned auth request ever sent rejects every later cell.
            var (exchanges, pinned) = auth.Snapshot;
            exchanges -= authBefore;
            rejection ??= exchanges != 0 ? "auth" : pinned != 0 ? "auth_pin" : null;
            return new("cell", target.Region, target.Lane, repeat, target.PayloadBytes, plan.Window.Concurrency, readiness, window,
                attempts, connects, exchanges, pinned, null, "approximate_reservoir_16384", rejection);
        }

        // Untimed and bounded: the seed must be readable in the pinned region before warmup starts.
        var started = clock.GetTimestamp();
        var readyFrom = lane.Attempts.Snapshot;
        var readyConnects = lane.Connections.Count;
        using var limit = new CancellationTokenSource(plan.ReadinessLimit, clock);
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(interrupt, limit.Token);
        string? last = null, stopped = null;
        while (true)
        {
            try
            {
                last = await lane.ReadAsync(ready.Token);
                if (last is null) break;
                await Task.Delay(plan.ReadinessPause, clock, ready.Token);
            }
            catch (OperationCanceledException) when (ready.IsCancellationRequested)
            {
                stopped = interrupt.IsCancellationRequested ? "interrupted" : "readiness";
                break;
            }
        }
        var readiness = new ParityReadiness(lane.Attempts.Snapshot.Attempts - readyFrom.Attempts, Connects(readyConnects),
            clock.GetElapsedTime(started).TotalMilliseconds, last);
        if (stopped is not null) return Row(readiness, null, null, null, stopped);

        var before = lane.Attempts.Snapshot;
        var connects = lane.Connections.Count;
        string? failure = null;
        var window = await ParityWindow.RunAsync(async ct =>
        {
            if (await lane.ReadAsync(ct) is { } reason)
            {
                Interlocked.CompareExchange(ref failure, reason, null);
                throw new InvalidDataException("parity read rejected");
            }
        }, plan.Window, clock, interrupt);
        var after = lane.Attempts.Snapshot;
        var rejection = Rejection(window, failure, before, after);
        return Row(readiness, window, after.Since(before), Connects(connects), rejection);
    }

    internal static string? Rejection(ParityWindowResult window, string? failure, ParityAttemptSnapshot before, ParityAttemptSnapshot after) =>
        (window.Rejection == "operation" ? failure ?? "operation" : window.Rejection) ?? after.RejectionSince(before, window.Starts);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ParityCellRow))]
internal partial class ParityJson : JsonSerializerContext
{
}
