using System.Diagnostics;
using System.Runtime.ExceptionServices;
using TerraBase.Engine.Turso;
using ZLogger;

namespace TerraBase.Hosting;

// Per-host completion of Program, including service disposal and its errors. ApplicationStopped alone precedes
// disposal and does not carry StopAsync failures; an in-process owner must observe the full run.
internal sealed class ApplicationRunCompletion
{
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task RunAsync(WebApplication app)
    {
        var errors = new List<Exception>();
        BackgroundService[] background = [];
        Task[] observations = [];
        var logger = app.Services.GetRequiredService<ILogger<ApplicationRunCompletion>>();
        long stoppingAt = 0;
        using var stopping = app.Lifetime.ApplicationStopping.Register(() =>
        {
            Interlocked.Exchange(ref stoppingAt, Stopwatch.GetTimestamp());
            logger.ZLogInformation($"Shutdown started; application budget is {Shutdown.Budget.TotalSeconds:@BudgetSeconds} seconds");
        });
        var started = false;
        TursoStorageEngine? storage = null;
        try
        {
            // Retain instances even if startup fails after launching only some of their tasks.
            background = [.. app.Services.GetServices<IHostedService>().OfType<BackgroundService>()];
            storage = app.Services.GetRequiredService<TursoStorageEngine>();
            await app.StartAsync().ConfigureAwait(false);
            started = true;
            observations = [.. background.Where(service => service.ExecuteTask is not null).Select(ObserveWorkerAsync)];
            await app.WaitForShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        if (!started)
        {
            // StartAsync can fail after some services started. Stop/join them before disposing their dependencies.
            try
            {
                await app.StopAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        LogStep("hosted services", stoppingAt);
        // The host's stop deadline can expire while workers still use DI. Owner completion waits beyond that
        // grace, independently of a caller's cancellation, before releasing their dependencies.
        var joined = Task.WhenAll(background.Select(service => service.ExecuteTask).OfType<Task>());
        try
        {
            await joined.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (joined.IsCanceled)
        {
            // A genuinely canceled ExecuteTask is a normal background-service exit.
        }
        catch (Exception) when (joined.IsFaulted)
        {
            // Await throws only one failure; retain every worker's fault, including faulted cancellation exceptions.
            errors.AddRange(joined.Exception!.InnerExceptions);
        }

        await Task.WhenAll(observations).ConfigureAwait(false);
        // All requests and workers, including backup taps and final usage writes, have released storage first.
        var closingAt = Stopwatch.GetTimestamp();
        try
        {
            if (storage is not null)
            {
                await storage.DisposeAsync().ConfigureAwait(false);
                // Failed startup can return a lease to its caller; cleanup ownership outlives the reported start error.
                if (started)
                {
                    await storage.CleanupCompletion.ConfigureAwait(false);
                }
            }
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        LogStep(started ? "durable storage close" : "failed-start storage close grace", closingAt);
        var disposingAt = Stopwatch.GetTimestamp();
        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        LogStep("service and storage disposal", disposingAt);
        // StopAsync can report the same worker fault the execution join observes.
        var failures = new AggregateException(errors).Flatten().InnerExceptions.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
        if (failures.Length == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        else if (failures.Length > 1)
        {
            throw new AggregateException(failures);
        }

        async Task ObserveWorkerAsync(BackgroundService service)
        {
            await service.ExecuteTask!.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            LogStep(service.GetType().Name, Interlocked.Read(ref stoppingAt));
        }

        void LogStep(string step, long timestamp)
        {
            if (timestamp != 0)
            {
                logger.ZLogInformation($"Shutdown step {step:@Step} completed after {Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds:@ElapsedMilliseconds} ms");
            }
        }
    }
}
