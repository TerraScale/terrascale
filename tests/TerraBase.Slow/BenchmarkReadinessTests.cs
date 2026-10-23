using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow;

public sealed class BenchmarkReadinessTests
{
    [Test]
    public async Task Binding_waits_for_the_project_to_be_registered(CancellationToken cancellationToken)
    {
        var missing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<IStartupFilter>(new MissingReadiness(missing)), cancellationToken: cancellationToken);
        var organization = await api.CreateOrganizationAsync("benchmark");
        var project = EntityIds.New().ToString();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = BenchmarkProcessHost.WaitForProjectBindingAsync(api, organization, project, deadline.Token);
        try
        {
            await missing.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await Assert.That(ready.IsCompleted).IsFalse();
            using var service = api.Service(api.User);
            using var created = await service.PostAsJsonAsync(new Uri("/v1/management/commits", UriKind.Relative), new
            {
                id = EntityIds.New().ToString(),
                ops = new[] { new { op = "create_project", organization, id = project, name = "benchmark" } },
            }, cancellationToken);
            created.EnsureSuccessStatusCode();
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
                // Join the readiness task if project registration or its assertion fails.
            }
        }
    }

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
                if ((context.Request.Path.Value?.Contains("/collections/bench-wait/", StringComparison.Ordinal) == true
                    || context.Request.Path.Value?.EndsWith("/binding", StringComparison.Ordinal) == true)
                    && context.Response.StatusCode == 404)
                {
                    missing.TrySetResult();
                }
            });
            next(app);
        };
    }

}
