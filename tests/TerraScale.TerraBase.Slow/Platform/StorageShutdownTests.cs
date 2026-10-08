using System.Runtime.ExceptionServices;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

public sealed class StorageShutdownTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Stopping_under_load_closes_every_database_file()
    {
        var api = await ManagementApi.StartAsync();
        var dataDirectory = api.Server.DataDirectory;
        Task? serverShutdown = null;
        Tenant? tenant = null;
        var testCancellation = TestContext.Current!.Execution.CancellationToken;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        var load = new List<Task>();
        Exception? failure = null;
        Exception? cleanupFailure = null;
        try
        {
            tenant = await api.CreateTenantAsync("documents:read", "documents:write");
            var (http, project) = (tenant.Data, tenant.ProjectId);

            // Writers keep opening new databases, so first opens race the shutdown. Only writes that
            // succeeded count, and the server stops once enough have, while the writers still run.
            var written = 0;
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            load.AddRange(Enumerable.Range(0, 16).Select(worker => Task.Run(async () =>
            {
                for (var round = 0; !stop.IsCancellationRequested; round++)
                {
                    var database = $"load-{worker}-{round}";
                    try
                    {
                        using var created = await http.PutAsync(new Uri($"/v1/projects/{project}/databases/{database}", UriKind.Relative), null, stop.Token);
                        using var collection = await http.PostAsJsonAsync(
                            new Uri($"/v1/projects/{project}/databases/{database}/collections", UriKind.Relative), new { name = "items" }, stop.Token);
                        using var put = await http.PutAsJsonAsync(
                            new Uri($"/v1/projects/{project}/databases/{database}/collections/items/documents/{EntityIds.New()}", UriKind.Relative),
                            new { title = Faker.Commerce.ProductName() },
                            stop.Token);
                        if (created.IsSuccessStatusCode && collection.IsSuccessStatusCode && put.IsSuccessStatusCode
                            && Interlocked.Increment(ref written) == 64)
                        {
                            loaded.TrySetResult();
                        }
                    }
                    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
                    {
                        // The server is stopping, or the test is.
                    }
                }
            })));

            await loaded.Task.WaitAsync(testCancellation);
            // The pool keeps database files open while the server runs, so the final check is not vacuous.
            await Assert.That(OpenFiles(dataDirectory)).IsNotEmpty();
            serverShutdown = api.DisposeAsync().AsTask();
            await serverShutdown.WaitAsync(testCancellation);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            // Caller cancellation bounds the wait, but cleanup owns the writers through their unwind.
            var cleanup = CleanupAsync();
            try
            {
                await TerraBaseTestServer.WaitForCleanupAsync(cleanup, TerraBaseTestServer.ReportCleanupFailure, testCancellation);
            }
            catch (Exception error)
            {
                cleanupFailure = error;
            }
        }

        ThrowFailures(failure, cleanupFailure);
        await Assert.That(OpenFiles(dataDirectory)).IsEmpty();

        async Task CleanupAsync()
        {
            var errors = new List<Exception>();
            try
            {
                await ObserveAsync(Task.WhenAll(stop.CancelAsync(), Task.WhenAll(load)));
                try
                {
                    tenant?.Dispose();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }

                await ObserveAsync(serverShutdown ??= api.DisposeAsync().AsTask());
                // The public disposal wait may already be canceled; retain the actual server owner too.
                await ObserveAsync(api.Server.CleanupCompletion);
            }
            finally
            {
                stop.Dispose();
            }

            ThrowFailures([.. errors]);

            async Task ObserveAsync(Task task)
            {
                try
                {
                    await task;
                }
                catch (Exception error)
                {
                    errors.Add(task.Exception ?? error);
                }
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Canceling_the_cleanup_wait_reports_a_late_host_disposal_failure(bool cancelResourceWait)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception[] failures = [new IOException("late host disposal failure"), new InvalidOperationException("another late host disposal failure")];
        var probe = new HeldDisposal(new AggregateException(failures));
        var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<IHostedService>(_ => probe));
        var reported = new TaskCompletionSource<AggregateException>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? cleanup = null;
        Task? resources = null;
        Exception? failure = null;
        Exception? cleanupFailure = null;
        try
        {
            using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
            health.EnsureSuccessStatusCode();
            cleanup = server.CleanupCompletion;
            var wait = TerraBaseTestServer.WaitForCleanupAsync(cleanup, error => reported.TrySetResult(error), caller.Token);
            await probe.Entered.Task.WaitAsync(token);
            await caller.CancelAsync();
            await Assert.That(async () => await wait).Throws<OperationCanceledException>();
            await Assert.That(cleanup.IsCompleted).IsFalse();
            await Assert.That(reported.Task.IsCompleted).IsFalse();
            if (!cancelResourceWait)
            {
                probe.Release.TrySetResult();
                var error = await reported.Task.WaitAsync(token);
                await Assert.That(error.InnerExceptions).IsEquivalentTo(failures);
                await Assert.That(Directory.Exists(server.DataDirectory)).IsTrue();
                await Assert.That(OpenFiles(server.DataDirectory)).IsEmpty();
            }
            else
            {
                resources = ReleaseResourcesAsync();
                using var resourceCaller = CancellationTokenSource.CreateLinkedTokenSource(token);
                var resourceReported = new TaskCompletionSource<AggregateException>(TaskCreationOptions.RunContinuationsAsynchronously);
                await resourceCaller.CancelAsync();
                await Assert.That(async () => await TerraBaseTestServer.WaitForCleanupAsync(
                    resources, error => resourceReported.TrySetResult(error), resourceCaller.Token)).Throws<OperationCanceledException>();
                await Assert.That(resources.IsCompleted).IsFalse();
                await Assert.That(Directory.Exists(server.DataDirectory)).IsTrue();
                probe.Release.TrySetResult();
                var reportedError = await resourceReported.Task.WaitAsync(token);
                await Assert.That(reportedError.InnerExceptions).IsEquivalentTo(failures);
            }
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            probe.Release.TrySetResult();
            resources ??= ReleaseResourcesAsync();
            try
            {
                await TerraBaseTestServer.WaitForCleanupAsync(resources, TerraBaseTestServer.ReportCleanupFailure, token);
            }
            catch (Exception error)
            {
                cleanupFailure = error;
            }
        }

        if (failure is not null)
        {
            ThrowFailures(failure, cleanupFailure);
        }

        await Assert.That(cleanupFailure is AggregateException).IsTrue();
        await Assert.That(((AggregateException)cleanupFailure!).Flatten().InnerExceptions).IsEquivalentTo(failures);
        await Assert.That(Directory.Exists(server.DataDirectory)).IsFalse();

        async Task ReleaseResourcesAsync()
        {
            try
            {
                await (cleanup ?? server.CleanupCompletion);
            }
            finally
            {
                server.Client.Dispose();
                if (Directory.Exists(server.DataDirectory))
                {
                    Directory.Delete(server.DataDirectory, recursive: true);
                }
            }
        }
    }

    private static void ThrowFailures(params Exception?[] errors)
    {
        var failures = new AggregateException(errors.OfType<Exception>()).Flatten().InnerExceptions
            .Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
        if (failures.Length == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        else if (failures.Length > 1)
        {
            throw new AggregateException(failures);
        }
    }

    private sealed class HeldDisposal(Exception failure) : IHostedService, IAsyncDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            Entered.TrySetResult();
            await Release.Task;
            throw failure;
        }
    }

    // Files of this process under the directory, from /proc/self/fd.
    private static List<string> OpenFiles(string directory) =>
        [.. new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos()
            .Select(descriptor => descriptor.LinkTarget)
            .OfType<string>()
            .Where(target => target.StartsWith(directory + "/", StringComparison.Ordinal))];
}
