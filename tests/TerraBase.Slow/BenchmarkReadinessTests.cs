using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Testing;

namespace TerraBase.Slow;

public sealed class BenchmarkReadinessTests
{
    [Test]
    public async Task Readiness_waits_for_the_existing_marker_collection(CancellationToken cancellationToken)
    {
        var missing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<IStartupFilter>(new MissingReadiness(missing)), cancellationToken: cancellationToken);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = BenchmarkProcessHost.WaitForWritesAsync(data, new Uri($"{database.Data}/collections", UriKind.Relative), "bench-wait", deadline.Token);
        try
        {
            await missing.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await Assert.That(ready.IsCompleted).IsFalse();
            await ManagementApi.CreateCollectionAsync(data, database, "bench-wait", cancellationToken);
            await ready;
        }
        finally
        {
            await deadline.CancelAsync();
            try
            {
                await ready;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // Join the readiness task even if the assertion or collection creation fails.
            }
        }
    }

    private sealed class MissingReadiness(TaskCompletionSource missing) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                await proceed(context);
                if (context.Request.Path.Value?.Contains("/collections/bench-wait/", StringComparison.Ordinal) == true
                    && context.Response.StatusCode == 404)
                {
                    missing.TrySetResult();
                }
            });
            next(app);
        };
    }

}
