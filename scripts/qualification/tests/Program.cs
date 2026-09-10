using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

public sealed class JourneyRegressionTests
{
static async Task Check(bool condition) { await Assert.That(condition).IsTrue(); }
static async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"expected {typeof(T).Name}");
}
[Test]
public async Task QualificationRejectsFalseSuccessAndUnboundedWaits()
{
var first = new TerraScaleDocument("db", "collection", "id", default, null, "42");
await Check(JourneyChecks.IsStaleDenial(System.Net.HttpStatusCode.PreconditionFailed, "precondition_failed"));
await Check(!JourneyChecks.IsStaleDenial(System.Net.HttpStatusCode.Conflict, "precondition_failed"));
await Check(!JourneyChecks.IsStaleDenial(System.Net.HttpStatusCode.PreconditionFailed, "wrong_code"));
JourneyChecks.SameReceipt(first, first);
await Reject<InvalidOperationException>(() => { JourneyChecks.SameReceipt(first, first with { ChangeToken = "43" }); return Task.CompletedTask; });
await Reject<InvalidOperationException>(() => { JourneyChecks.SameReceipt(first with { ChangeToken = null }, first with { ChangeToken = null }); return Task.CompletedTask; });
var visits = new List<string>();
await Reject<AggregateException>(() => JourneyChecks.CleanupAsync(new[] { "one", "two", "three" }, id => { visits.Add(id); throw new Exception("secret must not survive aggregation"); }));
await Check(visits.SequenceEqual(new[] { "one", "two", "three" }));
var never = new TaskCompletionSource<object>();
await Reject<OperationCanceledException>(() => JourneyChecks.ObserveAsync(_ => never.Task, () => true, TimeSpan.FromMilliseconds(50)));
await Reject<OperationCanceledException>(() => JourneyChecks.ObserveAsync(_ => Task.FromResult<object?>(null), () => true, TimeSpan.FromMilliseconds(50)));
await Reject<TimeoutException>(() => JourneyChecks.CancellationAsync(_ => never.Task, TimeSpan.FromMilliseconds(50)));
await Reject<InvalidOperationException>(() => JourneyChecks.CancellationAsync(_ => Task.FromResult<object>(new()), TimeSpan.FromMilliseconds(50)));
await JourneyChecks.CancellationAsync(token => Task.FromCanceled<object>(token), TimeSpan.FromMilliseconds(50));
TerraScaleReactiveSnapshot Snapshot(ulong revision, int value, string id = "own") => new(1, 1, revision,
    new[] { new TerraScaleReactiveItem(id, new[] {
        new TerraScaleReactiveField("sdk", Encoding.UTF8.GetBytes("\"dotnet\"")),
        new TerraScaleReactiveField("runId", Encoding.UTF8.GetBytes("\"run\"")),
        new TerraScaleReactiveField("value", Encoding.UTF8.GetBytes(value.ToString())) }) }, null, new TerraScaleViewCheckpoint("checkpoint"));
await Check(!JourneyChecks.ReactiveValue(Snapshot(42, 5), 42, "own", "run"));
await Check(!JourneyChecks.ReactiveValue(Snapshot(43, 4), 42, "own", "run"));
await Check(!JourneyChecks.ReactiveValue(Snapshot(43, 5, "peer"), 42, "own", "run"));
await Check(!JourneyChecks.ReactiveValue(Snapshot(43, 5), 42, "own", "other-run"));
await Check(JourneyChecks.ReactiveValue(Snapshot(43, 5), 42, "own", "run"));
using var body = JsonDocument.Parse("{\"sdk\":\"dotnet\",\"runId\":\"run\",\"value\":3}");
JourneyChecks.Body(first with { Data = body.RootElement }, "run", 3);
await Reject<InvalidOperationException>(() => { JourneyChecks.Body(first with { Data = body.RootElement }, "run", 1); return Task.CompletedTask; });
Console.WriteLine("receipt mismatch, effects, cleanup attempt-all, no-event deadline, ignored cancellation, initial snapshot/wrong reactive value rejection: passed");
}
}
