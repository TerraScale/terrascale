using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class ShapeMemoryIsolationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Shared_shape_memory_survives_the_first_response_or_cancellation_until_the_second_response(bool cancelFirst)
    {
        const long Budget = 8 * 1024 * 1024;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        ResponseBarrier? barrier = null;
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = Budget.ToString(CultureInfo.InvariantCulture), services =>
        {
            services.AddHttpContextAccessor();
            services.AddSingleton<IShapeService>(provider => barrier = new ResponseBarrier(
                ActivatorUtilities.CreateInstance<ShapeService>(provider), provider.GetRequiredService<IHttpContextAccessor>()));
        }, deadline.Token);
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        using var start = await shapes.GetAsync(new Uri($"{api.Shape}?offset=-1", UriKind.Relative), deadline.Token);
        using var snapshot = await shapes.GetAsync(start.Headers.Location, deadline.Token);
        snapshot.EnsureSuccessStatusCode();
        var handle = snapshot.Headers.GetValues("TB-Shape-Handle").Single();
        var head = snapshot.Headers.GetValues("TB-Shape-Next-Offset").Single();
        var before = await ReservedAsync(api.Server, deadline.Token);

        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long waiting = 0;
        var scope = api.Server.Services.GetRequiredService<IMeterFactory>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Scope == scope && instrument.Name is "terrabase.shape.live.waits" or "terrabase.shape.requests.waiting")
                {
                    observer.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "terrabase.shape.live.waits" && value == 1)
            {
                live.TrySetResult();
            }
            else if (instrument.Name == "terrabase.shape.requests.waiting" && Interlocked.Add(ref waiting, value) == 2)
            {
                joined.TrySetResult();
            }
        });
        listener.Start();

        using var firstCancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var uri = new Uri($"{api.Shape}?handle={handle}&offset={head}&live=true", UriKind.Relative);
        var first = shapes.GetAsync(uri, firstCancel.Token);
        Task<HttpResponseMessage>? second = null;
        try
        {
            // Establish the first flight before joining, rather than relying on request arrival order.
            await live.Task.WaitAsync(deadline.Token);
            second = shapes.GetAsync(uri, deadline.Token);
            await joined.Task.WaitAsync(deadline.Token);
            if (cancelFirst)
            {
                await firstCancel.CancelAsync();
                await Assert.ThrowsAsync<TaskCanceledException>(() => first);
                await barrier!.FirstCompleted.Task.WaitAsync(deadline.Token);
            }

            var padding = new Bogus.Faker().Random.String2(32 * 1024, "abcdef");
            var id = DocumentApi.NewId();
            var written = await api.PutAsync(id, new { padding }, cancellationToken: deadline.Token);
            await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
            await barrier!.SecondReady.Task.WaitAsync(deadline.Token);
            if (!cancelFirst)
            {
                using var finished = await first.WaitAsync(deadline.Token);
                finished.EnsureSuccessStatusCode();
                await barrier.FirstCompleted.Task.WaitAsync(deadline.Token);
            }

            // The second endpoint has its shared body but has not serialized it yet. Its root request must
            // retain the flight's materialization leases after the first request's root owner is gone.
            var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
            using (var refused = budget.TryReserve(MemoryLane.Foreground, Budget - before - 16 * 1024 - 32 * 1024, null))
            {
                await Assert.That(refused).IsNull();
            }

            barrier.ReleaseSecond.TrySetResult();
            using var response = await second.WaitAsync(deadline.Token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            await Assert.That(body.GetProperty("changes")[0].GetProperty("ops")[0].GetProperty("document").GetProperty("padding").GetString())
                .IsEqualTo(padding);
            await barrier.SecondCompleted.Task.WaitAsync(deadline.Token);
            api.Server.Services.GetRequiredService<DocumentCache>().Dispose();
            using var all = budget.TryReserve(MemoryLane.Foreground, Budget, null);
            await Assert.That(all).IsNotNull();
        }
        finally
        {
            barrier!.ReleaseSecond.TrySetResult();
            await firstCancel.CancelAsync();
            await deadline.CancelAsync();
            await FinishAsync(first);
            if (second is not null)
            {
                await FinishAsync(second);
            }
        }
    }

    private static async Task<long> ReservedAsync(TerraBaseTestServer server, CancellationToken cancellationToken)
    {
        var metrics = await server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), cancellationToken);
        var line = metrics.Split('\n').Single(value => value.StartsWith("terrabase_memory_reserved", StringComparison.Ordinal));
        return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
    }

    private static async Task FinishAsync(Task<HttpResponseMessage> request)
    {
        try
        {
            (await request).Dispose();
        }
        catch (OperationCanceledException)
        {
            // Every pending listener request is joined before the test's server is disposed.
        }
    }

    private sealed class ResponseBarrier(IShapeService inner, IHttpContextAccessor contexts) : IShapeService
    {
        private int _requests;
        public TaskCompletionSource FirstCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ShapeReadResult> ReadAsync(ShapeRequest request, CancellationToken cancellationToken, bool drainOnCancellation = false)
        {
            if (!request.Live)
            {
                return await inner.ReadAsync(request, cancellationToken, drainOnCancellation);
            }

            var ordinal = Interlocked.Increment(ref _requests);
            contexts.HttpContext!.Response.OnCompleted(() =>
            {
                (ordinal == 1 ? FirstCompleted : SecondCompleted).TrySetResult();
                return Task.CompletedTask;
            });
            var result = await inner.ReadAsync(request, cancellationToken, drainOnCancellation);
            if (ordinal == 2)
            {
                SecondReady.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
