using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Management.Usage;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

// Exact memory assertions exclude persisted observations, but must still detect leaked operation/cache leases.
internal static class RequestLogMemoryFixture
{
    internal static Task<ManagementApi> StartAsync(Action<Dictionary<string, string>>? configure = null,
        Action<IServiceCollection>? services = null, TimeProvider? time = null) =>
        ManagementApi.StartAsync(configure, collection =>
        {
            collection.AddSingleton(new RequestCompletion());
            collection.AddSingleton<IStartupFilter>(provider => provider.GetRequiredService<RequestCompletion>());
            services?.Invoke(collection);
        }, time ?? new FakeTimeProvider(DateTimeOffset.UtcNow));

    internal static async Task DrainAsync(TerraBaseTestServer server, CancellationToken cancellationToken = default)
    {
        var token = cancellationToken.CanBeCanceled ? cancellationToken : TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await WaitForRequestsAsync(server, token);
        await server.Services.GetRequiredService<QueryShapeRecorder>().FlushAsync(token);
        await server.Services.GetRequiredService<IRequestLogs>().FlushAsync(token);
        await server.Services.GetRequiredService<UsageMeter>().FlushAsync(token);
    }

    internal static Task WaitForRequestsAsync(TerraBaseTestServer server, CancellationToken cancellationToken) =>
        server.Services.GetRequiredService<RequestCompletion>().WaitAsync(cancellationToken);

    private sealed class RequestCompletion : IStartupFilter
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource? _idle;
        private int _active;

        internal Task WaitAsync(CancellationToken cancellationToken)
        {
            lock (_gate) { return (_idle?.Task ?? Task.CompletedTask).WaitAsync(cancellationToken); }
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                lock (_gate)
                {
                    _active++;
                    _idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                // Response delivery can precede usage middleware's finally block and QoS cleanup.
                context.Response.OnCompleted(() =>
                {
                    lock (_gate)
                    {
                        if (--_active == 0)
                        {
                            _idle!.TrySetResult();
                            _idle = null;
                        }
                    }
                    return Task.CompletedTask;
                });
                await following(context);
            });
            next(app);
        };
    }
}
