using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow;

public sealed class BenchmarkReadinessTests
{
    [Test]
    public async Task Quiescence_joins_backgrounds_while_the_peer_listener_remains_live(CancellationToken cancellationToken)
    {
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<IHostedService>(new HeldBackground(stopping, release)), cancellationToken: cancellationToken);
        var quiescing = BenchmarkProcessHost.QuiesceAsync(api.Server.Services);
        try
        {
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await Assert.That(quiescing.IsCompleted).IsFalse();
            await ConnectPeerAsync(api, cancellationToken);
        }
        finally
        {
            release.TrySetResult();
            await quiescing;
        }

        await Assert.That(api.Server.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .All(service => service.ExecuteTask is null || service.ExecuteTask.IsCompleted)).IsTrue();
        await ConnectPeerAsync(api, cancellationToken);
        using var response = await api.Server.Client.GetAsync("/healthz", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Quiescence_and_full_disposal_retain_faulted_cancellation_and_stop_errors(CancellationToken cancellationToken)
    {
        var executionError = new OperationCanceledException("benchmark faulted ExecuteTask");
        var stopError = new InvalidOperationException("benchmark StopAsync failure");
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = await ManagementApi.StartAsync(services: services =>
        {
            services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
            services.AddSingleton<IHostedService>(new FaultingBackground(execution.Task, stopError));
        }, cancellationToken: cancellationToken);
        execution.SetException(executionError);
        Exception? quiescenceError = null;
        Exception? disposalError = null;
        try
        {
            await BenchmarkProcessHost.QuiesceAsync(api.Server.Services);
        }
        catch (Exception error)
        {
            quiescenceError = error;
        }
        finally
        {
            try
            {
                await api.DisposeAsync();
            }
            catch (Exception error)
            {
                disposalError = error;
            }

            try
            {
                await api.CleanupCompletion;
            }
            catch (Exception error)
            {
                disposalError = error;
            }
        }

        await Assert.That(quiescenceError).IsNotNull();
        await Assert.That(new AggregateException(quiescenceError!).Flatten().InnerExceptions.Contains(executionError)).IsTrue();
        await Assert.That(new AggregateException(quiescenceError!).Flatten().InnerExceptions.Contains(stopError)).IsTrue();
        await Assert.That(disposalError).IsNotNull();
        await Assert.That(new AggregateException(disposalError!).Flatten().InnerExceptions.Contains(executionError)).IsTrue();
    }

    private static async Task ConnectPeerAsync(ManagementApi api, CancellationToken cancellationToken)
    {
        using var peer = new TcpClient();
        await peer.ConnectAsync(api.Server.PeerAddress!.Host, api.Server.PeerAddress.Port, cancellationToken);
    }

    private sealed class HeldBackground(TaskCompletionSource stopping, TaskCompletionSource release) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            finally
            {
                stopping.TrySetResult();
                await release.Task;
            }
        }
    }

    private sealed class FaultingBackground(Task execution, Exception stopError) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => execution;

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            throw stopError;
        }
    }

    [Test]
    public async Task Registration_retries_503_with_the_original_commit(CancellationToken cancellationToken)
    {
        var unavailable = new UnavailableRegistration();
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru", services: services =>
            services.AddSingleton<IStartupFilter>(unavailable), cancellationToken: cancellationToken);
        var config = new JsonObject
        {
            ["region"] = "gru",
            ["organization"] = EntityIds.New().ToString(),
            ["project"] = EntityIds.New().ToString(),
            ["database"] = EntityIds.New().ToString(),
        };
        await BenchmarkProcessHost.RegisterAsync(api, config, cancellationToken);
        await Assert.That(unavailable.Payloads.Count).IsGreaterThan(1);
        await Assert.That(unavailable.Payloads.All(payload => payload == unavailable.Payloads[0])).IsTrue();
        await BenchmarkProcessHost.WaitForProjectBindingAsync(api, config["organization"]!.GetValue<string>(),
            config["project"]!.GetValue<string>(), cancellationToken);
    }

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

    private sealed class UnavailableRegistration : IStartupFilter
    {
        public List<string> Payloads { get; } = [];

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                if (context.Request.Path == "/v1/management/commits" && context.Request.Method == "POST")
                {
                    context.Request.EnableBuffering();
                    using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                    Payloads.Add(await reader.ReadToEndAsync(context.RequestAborted));
                    context.Request.Body.Position = 0;
                    if (Payloads.Count == 1)
                    {
                        context.Response.StatusCode = 503;
                        return;
                    }
                }

                await proceed(context);
            });
            next(app);
        };
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
