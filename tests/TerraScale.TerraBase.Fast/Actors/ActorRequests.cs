using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Engine;
using TerraBase.Engine.Actors;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Actors;

// Test-only middleware wraps the existing endpoint; every callback must serve the real health response.
internal sealed class ActorRequests(int actorCount = 2, int maxItems = 256, int maxBytes = 2_097_152, Meter? meter = null) : IStartupFilter, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Work> _requests = new(StringComparer.Ordinal);
    private readonly int[] _active = new int[actorCount];
    private readonly int[] _sequence = new int[actorCount];
    private readonly AsyncLocal<Work?> _requestContext = new();
    private TerraBaseTestServer? _server;
    private int _requestId;

    public IActorRouter Router => _server!.Services.GetRequiredService<IActorRouter>();

    public async Task StartAsync() => _server = await TerraBaseTestServer.StartAsync(services: services =>
    {
        services.AddTerraBaseActors(actorCount, maxItems, maxBytes);
        if (meter is not null)
        {
            services.Replace(ServiceDescriptor.Singleton<IActorRouter>(_ => new ChannelActorRouter(actorCount, maxItems, maxBytes, meter)));
        }

        services.AddSingleton<IStartupFilter>(this);
    });

    public Task<HttpResponseMessage> SendAsync(Work work)
    {
        var id = Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture);
        _requests[id] = work;
        return SendCoreAsync(id);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(string id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/healthz", UriKind.Relative));
        request.Headers.Add("X-Test-Actor-Request", id);
        try
        {
            return await _server!.Client.SendAsync(request, TestContext.Current!.Execution.CancellationToken);
        }
        finally
        {
            _requests.TryRemove(id, out _);
        }
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            if (context.Request.Path != "/healthz" || !_requests.TryGetValue(context.Request.Headers["X-Test-Actor-Request"].ToString(), out var work))
            {
                await continuation(context);
                return;
            }

            _requestContext.Value = work;
            var router = context.RequestServices.GetRequiredService<IActorRouter>();
            context.Response.Headers["X-Test-Actor-Count"] = router.ActorCount.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-Test-Executed"] = "false";
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, work.CancellationToken);
            try
            {
                var actor = work.Actor ?? router.GetActor(work.Key);
                context.Response.Headers["X-Test-Actor"] = actor.ToString(CultureInfo.InvariantCulture);
                work.Ready.TrySetResult();
                if (work.AdmissionGate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellation.Token);
                }

                var pending = router.InvokeAsync<byte[]?>(actor, work.RetainedBytes, work.NullCallback ? null! : async token =>
                {
                    context.Response.Headers["X-Test-Executed"] = "true";
                    context.Response.Headers["X-Test-Ambient"] = _requestContext.Value is null ? "none" : "request";
                    context.Response.Headers["X-Test-Active"] = Interlocked.Increment(ref _active[actor]).ToString(CultureInfo.InvariantCulture);
                    context.Response.Headers["X-Test-Sequence"] = Interlocked.Increment(ref _sequence[actor]).ToString(CultureInfo.InvariantCulture);
                    work.Entered.TrySetResult();
                    try
                    {
                        await work.Release.Task.WaitAsync(token);
                        if (work.Fail)
                        {
                            throw new InvalidOperationException("test callback failure");
                        }

                        await continuation(context);
                        return work.Payload;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _active[actor]);
                    }
                }, cancellation.Token);
                // InvokeAsync has returned after attempting admission, before waiting for its callback.
                // Behind a held callback, accepted work remains pending and immediate faults are rejections.
                work.Submitted.TrySetResult(!pending.IsCompleted || pending.IsCompletedSuccessfully || work.Entered.Task.IsCompleted);
                await pending;
            }
            catch (StorageBusyException)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers["X-Test-Failure"] = "busy";
            }
            catch (OperationCanceledException)
            {
                context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
                context.Response.Headers["X-Test-Failure"] = "cancelled";
            }
            catch (ObjectDisposedException)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers["X-Test-Failure"] = "disposed";
            }
            catch (InvalidOperationException)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.Headers["X-Test-Failure"] = "callback";
            }
            catch (ArgumentException error)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.Headers["X-Test-Failure"] = work.Actor is null ? "key" : "argument";
                context.Response.Headers["X-Test-Exception"] = error.GetType().Name;
                context.Response.Headers["X-Test-Parameter"] = error.ParamName;
            }
            finally
            {
                work.Submitted.TrySetResult(false);
                _requestContext.Value = null;
            }
        });
        next(app);
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var work in _requests.Values)
        {
            work.AdmissionGate?.TrySetResult();
            work.Release.TrySetResult();
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    public static ActorKey NewKey(Faker? fake = null)
    {
        fake ??= new Faker();
        var tenant = new TenantContext(fake.Random.Guid().ToString(), PlanTier.Hobby, fake.Random.Guid().ToString(), fake.Random.Guid().ToString());
        return new ActorKey(tenant, fake.Random.AlphaNumeric(12), fake.Random.AlphaNumeric(24));
    }

    internal sealed class Work(ActorKey? key = null, int? actor = 0, int retainedBytes = 1, bool held = false, bool fail = false, TaskCompletionSource? admissionGate = null, byte[]? payload = null, bool nullCallback = false, CancellationToken cancellationToken = default)
    {
        public ActorKey Key { get; } = key ?? NewKey();

        public int? Actor { get; } = actor;

        public int RetainedBytes { get; } = retainedBytes;

        public bool Fail { get; } = fail;

        public bool NullCallback { get; } = nullCallback;

        public byte[]? Payload { get; } = payload;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public TaskCompletionSource? AdmissionGate { get; } = admissionGate;

        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = ReleasedUnlessHeld(held);

        private static TaskCompletionSource ReleasedUnlessHeld(bool held)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!held)
            {
                signal.SetResult();
            }

            return signal;
        }
    }
}
