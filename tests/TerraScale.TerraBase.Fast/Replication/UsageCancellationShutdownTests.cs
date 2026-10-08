using System.Net;
using Bogus;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class UsageCancellationShutdownTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Follower_shutdown_cancels_in_flight_observations_without_faulting_background_cleanup(bool queryShapes)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var peers = new PeerTls();
        var hold = new UsageAcknowledgementHold(token);
        await using var primary = await TerraBaseTestServer.StartAsync(peers.Apply, services =>
        {
            services.AddSingleton(hold);
            services.AddGrpc(options => options.Interceptors.Add<UsageAcknowledgementHold>());
        });
        var fake = new Faker();
        var tenant = await primary.RegisterAsync(new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby,
            EntityIds.New().ToString(), EntityIds.New().ToString()));
        var time = new UsageClock();
        var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = primary.PeerAddress!.ToString();
        }, services => services.AddSingleton<TimeProvider>(time));
        try
        {
            using var healthy = await follower.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
            await Assert.That(healthy.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var usage = follower.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single();
            await time.TimerCreated.Task.WaitAsync(token);
            if (queryShapes)
            {
                var shape = QueryShape.Normalize(new DocumentQuery("items", null, [], null, 1, 1024))!;
                follower.Services.GetRequiredService<IQueryShapeTracker>().Record(tenant, "items", shape, TimeSpan.Zero, 1);
            }
            else
            {
                follower.Services.GetRequiredService<IUsageMeter>().RecordRead(tenant);
            }
            time.Advance(TimeSpan.FromSeconds(10));

            // This is the real background batch, received and committed over the primary's mTLS listener.
            var batch = await hold.Entered.Task.WaitAsync(token);
            if (queryShapes)
            {
                await Assert.That(((QueryShapesBatch)batch).Sequence).IsEqualTo(1UL);
                await Assert.That(((QueryShapesBatch)batch).Envelope.Length).IsGreaterThan(0);
            }
            else
            {
                await Assert.That(((UsageBatch)batch).Counts.Count).IsEqualTo(1);
                await Assert.That(((UsageBatch)batch).Counts[0].DatabaseId).IsEqualTo(tenant.DatabaseId);
                await Assert.That(((UsageBatch)batch).Counts[0].Reads).IsEqualTo(1UL);
            }
            var execution = usage.ExecuteTask!;
            await Assert.That(execution.IsCompleted).IsFalse();
            follower.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await hold.Cancelled.Task.WaitAsync(token);
            try
            {
                await execution.WaitAsync(token);
            }
            catch (OperationCanceledException) when (execution.IsCanceled && !token.IsCancellationRequested)
            {
                // Caller cancellation is a normal background exit; RpcException would fault this task and cleanup.
            }

            await Assert.That(execution.IsCanceled || execution.IsCompletedSuccessfully).IsTrue();
        }
        finally
        {
            await follower.DisposeAsync().AsTask().WaitAsync(token);
        }

        await Assert.That(Directory.Exists(follower.DataDirectory)).IsFalse();
    }

    private sealed class UsageClock() : FakeTimeProvider(DateTimeOffset.UtcNow)
    {
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            // Other background services also use 10-second timers; wait for this service before advancing once.
            if (Environment.StackTrace.Contains(nameof(UsageFlushService), StringComparison.Ordinal))
            {
                TimerCreated.TrySetResult();
            }

            return timer;
        }
    }

    private sealed class UsageAcknowledgementHold(CancellationToken testCancellation) : Interceptor
    {
        private int _calls;

        public TaskCompletionSource<object> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            var response = await continuation(request, context);
            if (request is UsageBatch or QueryShapesBatch && Interlocked.Increment(ref _calls) == 1)
            {
                // Hold only the first acknowledgement. Shutdown retries the same batch through the real receiver.
                using var cancelled = context.CancellationToken.Register(() => Cancelled.TrySetResult());
                Entered.TrySetResult(request);
                await Cancelled.Task.WaitAsync(testCancellation);
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            return response;
        }
    }
}
