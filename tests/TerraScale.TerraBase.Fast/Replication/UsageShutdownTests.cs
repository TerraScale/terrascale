using System.Net;
using System.Text.Json;
using Bogus;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class UsageShutdownTests
{
    [Test]
    public async Task A_follower_stops_cleanly_when_shutdown_cancels_its_periodic_usage_rpc()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var peers = new PeerTls();
        var response = new HeldUsageResponse();
        await using var primary = await ManagementApi.StartAsync(peers.Apply, services =>
        {
            services.AddSingleton(response);
            services.AddGrpc(options => options.Interceptors.Add<HeldUsageResponse>());
        });
        var database = await primary.CreateManagedDatabaseAsync("orders");
        var key = await primary.Server.IssueAsync(database.Organization, ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite);
        var clock = new FlushClock();
        var catchUp = new CatalogCatchUp();
        TerraBaseTestServer? follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            primary.Tls.Apply(settings);
            settings["catalog-primary"] = primary.Server.PeerAddress!.ToString();
        }, services =>
        {
            services.AddSingleton<ICatalogApplyObserver>(catchUp);
            services.AddSingleton<CatalogPrimaryClient>();
            services.AddSingleton<ObservedUsageClient>();
            services.AddSingleton<ICatalogPrimaryClient>(provider => provider.GetRequiredService<ObservedUsageClient>());
            // Only the usage service gets this clock, so its timer has an unambiguous startup barrier.
            var registration = services.Single(service => service.ImplementationType == typeof(UsageFlushService));
            services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(
                provider => ActivatorUtilities.CreateInstance<UsageFlushService>(provider, clock));
        });
        try
        {
            await catchUp.Ready.Task.WaitAsync(token);
            await clock.Scheduled.Task.WaitAsync(token);
            var flush = follower.Services.GetServices<IHostedService>().OfType<UsageFlushService>().Single();
            var replied = follower.Services.GetRequiredService<ObservedUsageClient>().Replied.Task;
            using var data = primary.Tls.CreateClient(follower.ClientAddress!, key);
            await ManagementApi.CreateCollectionAsync(data, database, "items");
            using (var written = await data.PutAsJsonAsync(
                ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "first")), new { name = new Faker().Name.FullName() }, token))
            {
                await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            // The real primary adds the batch, but holds its response while the periodic flush is still sending.
            clock.Advance(TimeSpan.FromSeconds(10));
            var batch = await response.Pending.Task.WaitAsync(token);
            await Assert.That(batch.Counts.Sum(count => (long)count.Writes)).IsEqualTo(1L);
            var directory = follower.DataDirectory;
            await follower.DisposeAsync();
            follower = null;
            await response.Cancelled.Task.WaitAsync(token);
            await Assert.That(flush.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
            await Assert.That(Directory.Exists(directory)).IsFalse();

            // StopAsync swallows drain failures: require a real acknowledgement of the unchanged batch.
            await Assert.That(replied.IsCompletedSuccessfully).IsTrue();
            var (retry, accepted) = await replied.WaitAsync(token);
            await Assert.That(accepted).IsTrue();
            await Assert.That((retry.Epoch, retry.Sequence, retry.Counts))
                .IsEqualTo((batch.Epoch, batch.Sequence, batch.Counts));

            // With nothing pending in memory, the management API must read the saved total.
            var meter = primary.Server.Services.GetRequiredService<UsageMeter>();
            await meter.FlushAsync(token);
            await Assert.That(meter.PendingDatabases).IsEqualTo(0);
            var usage = await primary.Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(database.Path + "/usage"), token);
            await Assert.That(usage.GetProperty("writes").GetInt64()).IsEqualTo(1L);
        }
        finally
        {
            if (follower is not null)
            {
                await follower.DisposeAsync();
            }
        }
    }

    private sealed class ObservedUsageClient(CatalogPrimaryClient inner) : ICatalogPrimaryClient
    {
        public TaskCompletionSource<(UsageBatch Batch, bool Accepted)> Replied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> AddUsageAsync(UsageBatch batch, CancellationToken cancellationToken)
        {
            var request = batch.Clone();
            var accepted = await inner.AddUsageAsync(batch, cancellationToken);
            Replied.TrySetResult((request, accepted));
            return accepted;
        }

        public Task<bool> AddQueryShapesAsync(QueryShapesBatch batch, CancellationToken cancellationToken) =>
            inner.AddQueryShapesAsync(batch, cancellationToken);
    }

    private sealed class HeldUsageResponse : Interceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource<UsageBatch> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            var response = await continuation(request, context);
            if (request is UsageBatch batch && Interlocked.Increment(ref _calls) == 1)
            {
                Pending.TrySetResult(batch.Clone());
                // Shutdown cancels the actual HTTP/2 RPC. A retry during the final drain gets its real acknowledgement.
                try
                {
                    await _release.Task.WaitAsync(context.CancellationToken);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }

            return response;
        }
    }

    private sealed class FlushClock() : FakeTimeProvider(DateTimeOffset.UtcNow)
    {
        public TaskCompletionSource Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Scheduled.TrySetResult();
            return timer;
        }
    }

    private sealed class CatalogCatchUp : ICatalogApplyObserver
    {
        public string Collection => "catalog";

        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask AppliedAsync(IReadOnlyList<DocumentChange> documents, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask CaughtUpAsync(CancellationToken cancellationToken)
        {
            Ready.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
