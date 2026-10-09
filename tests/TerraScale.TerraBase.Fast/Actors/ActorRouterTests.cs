using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Engine.Actors;
using TerraBase.Fast.Platform;
using TerraBase.Tenancy;
using TerraBase.Testing;
using static TerraBase.Fast.Actors.ActorRequests;

namespace TerraBase.Fast.Actors;

public sealed class ActorRouterTests
{
    [Test]
    [Arguments(0, 1, 1, "actorCount")]
    [Arguments(257, 1, 1, "actorCount")]
    [Arguments(1, 0, 1, "maxItemsPerActor")]
    [Arguments(1, -1, 1, "maxItemsPerActor")]
    [Arguments(1, 1, 0, "maxBytesPerActor")]
    [Arguments(1, 1, -1, "maxBytesPerActor")]
    public async Task Actor_registration_rejects_invalid_limits_during_real_host_startup(int actors, int items, int bytes, string parameter)
    {
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await using var unexpected = await TerraBaseTestServer.StartAsync(services: services =>
                services.AddTerraBaseActors(actors, items, bytes));
        });
        await Assert.That(error!.GetType()).IsEqualTo(typeof(ArgumentOutOfRangeException));
        await Assert.That(error.ParamName).IsEqualTo(parameter);
    }

    [Test]
    [Arguments(-1, 1, false, "actor", nameof(ArgumentOutOfRangeException))]
    [Arguments(1, 1, false, "actor", nameof(ArgumentOutOfRangeException))]
    [Arguments(0, -1, false, "retainedBytes", nameof(ArgumentOutOfRangeException))]
    [Arguments(0, 1, true, "work", nameof(ArgumentNullException))]
    public async Task Invalid_invocation_arguments_reject_over_http_without_consuming_mailbox_capacity(int actor, int bytes, bool nullCallback, string parameter, string exception)
    {
        await using var requests = new ActorRequests(actorCount: 1, maxItems: 1, maxBytes: 1);
        await requests.StartAsync();
        using var invalid = await requests.SendAsync(new Work(actor: actor, retainedBytes: bytes, nullCallback: nullCallback));
        await AssertRejectedAsync(invalid, HttpStatusCode.BadRequest, "argument");
        await Assert.That(Header(invalid, "X-Test-Exception")).IsEqualTo(exception);
        await Assert.That(Header(invalid, "X-Test-Parameter")).IsEqualTo(parameter);

        var held = new Work(held: true);
        var running = requests.SendAsync(held);
        await SignalAsync(held.Entered.Task);
        using var full = await requests.SendAsync(new Work());
        await AssertRejectedAsync(full, HttpStatusCode.ServiceUnavailable, "busy");
        held.Release.TrySetResult();
        using var completed = await running;
        await AssertHealthyAsync(completed);
        using var recovered = await requests.SendAsync(new Work());
        await AssertHealthyAsync(recovered);
    }

    [Test]
    public async Task Document_identity_routes_deterministically_across_requests_plans_and_router_instances()
    {
        await using var first = new ActorRequests(actorCount: 4);
        await using var second = new ActorRequests(actorCount: 4);
        await first.StartAsync();
        await second.StartAsync();
        var actors = new HashSet<int>();
        var fake = new Faker { Random = new Randomizer(648) };
        for (var index = 0; index < 16; index++)
        {
            var key = NewKey(fake);
            using var original = await first.SendAsync(new Work(key, actor: null));
            using var repeated = await first.SendAsync(new Work(key with { Database = key.Database with { Plan = PlanTier.Pro } }, actor: null));
            using var independent = await second.SendAsync(new Work(key, actor: null));
            await AssertHealthyAsync(original);
            await AssertHealthyAsync(repeated);
            await AssertHealthyAsync(independent);
            var actor = int.Parse(Header(original, "X-Test-Actor"), CultureInfo.InvariantCulture);
            await Assert.That(actor is >= 0 and < 4).IsTrue();
            await Assert.That(Header(original, "X-Test-Actor-Count")).IsEqualTo("4");
            await Assert.That(Header(repeated, "X-Test-Actor")).IsEqualTo(Header(original, "X-Test-Actor"));
            await Assert.That(Header(independent, "X-Test-Actor")).IsEqualTo(Header(original, "X-Test-Actor"));
            actors.Add(actor);
        }

        await Assert.That(actors.Count > 1).IsTrue();

        // Independent SHA-256 vectors pin encoding and field boundaries, rather than a process-local hash seed.
        var database = new TenantContext("org", PlanTier.Hobby, "project", "database");
        foreach (var (collection, id, expected) in new[] { ("a", "bc", "2"), ("ab", "c", "1"), ("é", "鍵", "1") })
        {
            using var response = await first.SendAsync(new Work(new ActorKey(database, collection, id), actor: null));
            await AssertHealthyAsync(response);
            await Assert.That(Header(response, "X-Test-Actor")).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task An_awaiting_actor_serializes_its_mailbox_while_another_actor_serves_health()
    {
        await using var requests = new ActorRequests();
        await requests.StartAsync();
        var held = new Work(held: true);
        var first = requests.SendAsync(held);
        await SignalAsync(held.Entered.Task);
        var queued = new Work();
        var second = requests.SendAsync(queued);
        await SignalAsync(queued.Submitted.Task);
        using var other = await requests.SendAsync(new Work(actor: 1));
        await AssertHealthyAsync(other);
        await Assert.That(queued.Entered.Task.IsCompleted).IsFalse();
        held.Release.TrySetResult();
        using var firstResponse = await first;
        using var secondResponse = await second;
        await AssertHealthyAsync(firstResponse);
        await AssertHealthyAsync(secondResponse);
        await Assert.That(Header(firstResponse, "X-Test-Active")).IsEqualTo("1");
        await Assert.That(Header(secondResponse, "X-Test-Active")).IsEqualTo("1");
        await Assert.That(Header(firstResponse, "X-Test-Sequence")).IsEqualTo("1");
        await Assert.That(Header(secondResponse, "X-Test-Sequence")).IsEqualTo("2");
        await Assert.That(Header(other, "X-Test-Actor")).IsEqualTo("1");
    }

    [Test]
    [Arguments(4, 128)]
    [Arguments(16, 16)]
    public async Task Concurrent_admission_fits_item_and_byte_budgets_and_recovers_after_drain(int items, int bytes)
    {
        // Each case admits four 4-byte inputs: one reaches only its item bound, the other only its byte bound.
        await using var requests = new ActorRequests(maxItems: items, maxBytes: bytes);
        await requests.StartAsync();
        for (var round = 0; round < 2; round++)
        {
            var held = new Work(retainedBytes: 4, held: true);
            var running = requests.SendAsync(held);
            await SignalAsync(held.Entered.Task);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = Enumerable.Range(0, 12).Select(_ => new Work(retainedBytes: 4, admissionGate: gate)).ToArray();
            var pending = work.Select(requests.SendAsync).ToArray();
            await SignalAsync(Task.WhenAll(work.Select(input => input.Ready.Task)));
            gate.SetResult();
            var admitted = await SignalAsync(Task.WhenAll(work.Select(input => input.Submitted.Task)));
            await Assert.That(admitted.Count(accepted => accepted)).IsEqualTo(3);
            await Assert.That(work.All(input => !input.Entered.Task.IsCompleted)).IsTrue();
            for (var index = 0; index < pending.Length; index++)
            {
                if (!admitted[index])
                {
                    using var rejected = await pending[index];
                    await AssertRejectedAsync(rejected, HttpStatusCode.ServiceUnavailable, "busy");
                }
            }

            using var stillFull = await requests.SendAsync(new Work());
            await AssertRejectedAsync(stillFull, HttpStatusCode.ServiceUnavailable, "busy");
            using var other = await requests.SendAsync(new Work(actor: 1, retainedBytes: 4));
            await AssertHealthyAsync(other);
            held.Release.TrySetResult();
            using var runningResponse = await running;
            await AssertHealthyAsync(runningResponse);
            for (var index = 0; index < pending.Length; index++)
            {
                if (admitted[index])
                {
                    using var completed = await pending[index];
                    await AssertHealthyAsync(completed);
                    await Assert.That(Header(completed, "X-Test-Active")).IsEqualTo("1");
                }
            }
        }

        using var oversized = await requests.SendAsync(new Work(retainedBytes: bytes + 1));
        await AssertRejectedAsync(oversized, HttpStatusCode.ServiceUnavailable, "busy");
        using var recovered = await requests.SendAsync(new Work(retainedBytes: bytes));
        await AssertHealthyAsync(recovered);
    }

    [Test]
    public async Task Queued_and_executing_cancellation_recovers_capacity_and_callback_failure_is_isolated()
    {
        await using var requests = new ActorRequests(maxItems: 2, maxBytes: 8);
        await requests.StartAsync();
        var held = new Work(retainedBytes: 4, held: true);
        var running = requests.SendAsync(held);
        await SignalAsync(held.Entered.Task);
        using var cancellation = new CancellationTokenSource();
        var cancelled = new Work(retainedBytes: 4, cancellationToken: cancellation.Token);
        var waiting = requests.SendAsync(cancelled);
        await SignalAsync(cancelled.Submitted.Task);
        await cancellation.CancelAsync();
        using var stillFull = await requests.SendAsync(new Work());
        await AssertRejectedAsync(stillFull, HttpStatusCode.ServiceUnavailable, "busy");
        held.Release.TrySetResult();
        using var runningResponse = await running;
        using var cancelledResponse = await waiting;
        await AssertHealthyAsync(runningResponse);
        await AssertRejectedAsync(cancelledResponse, HttpStatusCode.RequestTimeout, "cancelled");
        using var alreadyCancelled = await requests.SendAsync(new Work(cancellationToken: cancellation.Token));
        await AssertRejectedAsync(alreadyCancelled, HttpStatusCode.RequestTimeout, "cancelled");
        using var executingCancellation = new CancellationTokenSource();
        var executing = new Work(retainedBytes: 8, held: true, cancellationToken: executingCancellation.Token);
        var executingRequest = requests.SendAsync(executing);
        await SignalAsync(executing.Entered.Task);
        await executingCancellation.CancelAsync();
        using var executingResponse = await executingRequest;
        await Assert.That(executingResponse.StatusCode).IsEqualTo(HttpStatusCode.RequestTimeout);
        await Assert.That(Header(executingResponse, "X-Test-Failure")).IsEqualTo("cancelled");
        await Assert.That(Header(executingResponse, "X-Test-Executed")).IsEqualTo("true");
        await Assert.That(executing.Release.Task.IsCompleted).IsFalse();
        using var failed = await requests.SendAsync(new Work(fail: true, retainedBytes: 8));
        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(Header(failed, "X-Test-Failure")).IsEqualTo("callback");
        await Assert.That(Header(failed, "X-Test-Executed")).IsEqualTo("true");
        using var recovered = await requests.SendAsync(new Work(retainedBytes: 8));
        await AssertHealthyAsync(recovered);
    }

    [Test]
    public async Task Router_shutdown_races_concurrent_submission_and_drains_every_admitted_health_request()
    {
        await using var requests = new ActorRequests(maxItems: 4, maxBytes: 16);
        await requests.StartAsync();
        var held = new Work(retainedBytes: 4, held: true);
        var running = requests.SendAsync(held);
        await SignalAsync(held.Entered.Task);
        var queued = new Work(retainedBytes: 4);
        var waiting = requests.SendAsync(queued);
        await Assert.That(await SignalAsync(queued.Submitted.Task)).IsTrue();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Enumerable.Range(0, 12).Select(_ => new Work(retainedBytes: 4, admissionGate: gate)).ToArray();
        var pending = work.Select(requests.SendAsync).ToArray();
        var draining = DrainAsync();
        await SignalAsync(Task.WhenAll(work.Select(input => input.Ready.Task)));
        gate.SetResult();
        var admitted = await SignalAsync(Task.WhenAll(work.Select(input => input.Submitted.Task)));
        await SignalAsync(shutdownStarted.Task);
        await Assert.That(admitted.Count(accepted => accepted) <= 2).IsTrue();
        await Assert.That(work.All(input => !input.Entered.Task.IsCompleted)).IsTrue();
        for (var index = 0; index < pending.Length; index++)
        {
            if (!admitted[index])
            {
                using var rejection = await pending[index];
                var failure = Header(rejection, "X-Test-Failure");
                await Assert.That(failure is "busy" or "disposed").IsTrue();
                await AssertRejectedAsync(rejection, HttpStatusCode.ServiceUnavailable, failure);
            }
        }

        using var rejected = await requests.SendAsync(new Work(actor: 1));
        await AssertRejectedAsync(rejected, HttpStatusCode.ServiceUnavailable, "disposed");
        await Assert.That(draining.IsCompleted).IsFalse();
        held.Release.TrySetResult();
        using var runningResponse = await running;
        using var queuedResponse = await waiting;
        await AssertHealthyAsync(runningResponse);
        await AssertHealthyAsync(queuedResponse);
        for (var index = 0; index < pending.Length; index++)
        {
            if (admitted[index])
            {
                using var completed = await pending[index];
                await AssertHealthyAsync(completed);
                await Assert.That(Header(completed, "X-Test-Active")).IsEqualTo("1");
            }
        }

        await SignalAsync(draining);
        using var after = await requests.SendAsync(new Work());
        await AssertRejectedAsync(after, HttpStatusCode.ServiceUnavailable, "disposed");

        async Task DrainAsync()
        {
            await SignalAsync(gate.Task);
            var completion = requests.Router.DisposeAsync();
            shutdownStarted.SetResult();
            await completion;
        }
    }

    [Test]
    public async Task Throwing_metric_listeners_preserve_queued_callers_busy_rejection_and_shutdown()
    {
        // A private Meter keeps this throwing callback off every other parallel fixture.
        using var meter = new Meter($"TerraBase.Fast.Actors.{Guid.NewGuid():N}");
        using var listener = new MeterListener();
        var completed = 0;
        var rejected = 0;
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
        {
            if (instrument.Name == "terrabase.actors.completed")
            {
                Interlocked.Increment(ref completed);
            }
            else if (instrument.Name == "terrabase.actors.rejected")
            {
                Interlocked.Increment(ref rejected);
            }

            throw new InvalidOperationException("test metric listener failure");
        });
        listener.Start();
        await using var requests = new ActorRequests(maxItems: 2, maxBytes: 8, meter: meter);
        await requests.StartAsync();
        var held = new Work(retainedBytes: 4, held: true);
        var running = requests.SendAsync(held);
        await SignalAsync(held.Entered.Task);
        var queued = new Work(retainedBytes: 4);
        var waiting = requests.SendAsync(queued);
        await Assert.That(await SignalAsync(queued.Submitted.Task)).IsTrue();
        using var busy = await requests.SendAsync(new Work());
        await AssertRejectedAsync(busy, HttpStatusCode.ServiceUnavailable, "busy");
        held.Release.TrySetResult();
        using var runningResponse = await running;
        using var queuedResponse = await waiting;
        await AssertHealthyAsync(runningResponse);
        await AssertHealthyAsync(queuedResponse);
        var later = new Work(retainedBytes: 4, held: true);
        var laterRequest = requests.SendAsync(later);
        await SignalAsync(later.Entered.Task);
        var final = new Work(retainedBytes: 4);
        var finalRequest = requests.SendAsync(final);
        await Assert.That(await SignalAsync(final.Submitted.Task)).IsTrue();
        var disposal = requests.Router.DisposeAsync().AsTask();
        await Assert.That(disposal.IsCompleted).IsFalse();
        later.Release.TrySetResult();
        using var laterResponse = await laterRequest;
        using var finalResponse = await finalRequest;
        await AssertHealthyAsync(laterResponse);
        await AssertHealthyAsync(finalResponse);
        await SignalAsync(disposal);
        await Assert.That(Volatile.Read(ref completed)).IsEqualTo(4);
        await Assert.That(Volatile.Read(ref rejected)).IsEqualTo(1);
        using var disposed = await requests.SendAsync(new Work());
        await AssertRejectedAsync(disposed, HttpStatusCode.ServiceUnavailable, "disposed");
    }

    [Test]
    public async Task An_idle_actor_releases_its_last_callback_payload_and_result_before_next_admission()
    {
        const string ChildFlag = "TERRABASE_TEST_ACTOR_RETENTION_CHILD";
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
        {
            // Full collections must not pause unrelated fixtures in the parallel suite.
            await IsolatedTestChild.RunAsync(ChildFlag, "TerraBase.Fast.Actors", nameof(ActorRouterTests),
                nameof(An_idle_actor_releases_its_last_callback_payload_and_result_before_next_admission));
            return;
        }

        using var meter = new Meter($"TerraBase.Fast.Actors.{Guid.NewGuid():N}");
        using var listener = new MeterListener();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "terrabase.actors.completed")
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => completed.TrySetResult());
        listener.Start();
        await using var requests = new ActorRequests(actorCount: 1, meter: meter);
        await requests.StartAsync();
        var references = await CompletePayloadRequestAsync(requests);
        await SignalAsync(completed.Task);

        // No new callback or router disposal may advance the mailbox and hide last-item retention.
        await TerraBaseTestServer.EventuallyAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            return Task.FromResult(!references.Work.IsAlive && !references.Payload.IsAlive);
        }, timeout: TimeSpan.FromSeconds(5));

        using var later = await requests.SendAsync(new Work());
        await AssertHealthyAsync(later);
    }

    private static async Task<(WeakReference Work, WeakReference Payload)> CompletePayloadRequestAsync(ActorRequests requests)
    {
        // The payload is captured by the callback AND returned as its result. Neither may remain rooted.
        var work = new Work(retainedBytes: 4096, payload: new byte[4096]);
        var references = (new WeakReference(work), new WeakReference(work.Payload!));
        using var response = await requests.SendAsync(work);
        await AssertHealthyAsync(response);
        return references;
    }

    [Test]
    public async Task Routing_rejects_missing_scope_invalid_unicode_and_oversized_encoded_keys_over_http()
    {
        await using var requests = new ActorRequests();
        await requests.StartAsync();
        var database = new TenantContext("o", PlanTier.Hobby, "p", "d");
        var key = new ActorKey(database, "c", "id");
        foreach (var invalid in new[]
        {
            key with { Database = database with { ProjectId = null } },
            key with { Database = database with { DatabaseId = null } },
            key with { Id = "\uD800" },
            key with { Collection = "\uDC00" },
            key with { Id = new string('é', 2036) + "a" },
        })
        {
            using var rejected = await requests.SendAsync(new Work(invalid, actor: null));
            await AssertRejectedAsync(rejected, HttpStatusCode.BadRequest, "key");
        }

        // Five 4-byte prefixes and four ASCII fields leave 4072 bytes for the id.
        foreach (var id in new[] { new string('é', 2035) + "a", new string('é', 2036) })
        {
            using var accepted = await requests.SendAsync(new Work(key with { Id = id }, actor: null));
            await AssertHealthyAsync(accepted);
        }
    }

    private static Task SignalAsync(Task signal) => signal.WaitAsync(TestContext.Current!.Execution.CancellationToken);

    private static Task<T> SignalAsync<T>(Task<T> signal) => signal.WaitAsync(TestContext.Current!.Execution.CancellationToken);

    private static string Header(HttpResponseMessage response, string name) => response.Headers.GetValues(name).Single();

    private static async Task AssertHealthyAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Header(response, "X-Test-Executed")).IsEqualTo("true");
        await Assert.That(Header(response, "X-Test-Ambient")).IsEqualTo("none");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(body.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response, HttpStatusCode status, string failure)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(Header(response, "X-Test-Failure")).IsEqualTo(failure);
        await Assert.That(Header(response, "X-Test-Executed")).IsEqualTo("false");
    }
}
