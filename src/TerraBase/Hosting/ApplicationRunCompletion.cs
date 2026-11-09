using System.Diagnostics;
using System.Runtime.ExceptionServices;
using TerraBase.Engine.Turso;
using ZLogger;

namespace TerraBase.Hosting;

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
        var joined = Task.WhenAll(background.Select(service => service.ExecuteTask).OfType<Task>());
        try
        {
            await joined.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (joined.IsCanceled)
        {
        }
        catch (Exception) when (joined.IsFaulted)
        {
            errors.AddRange(joined.Exception!.InnerExceptions);
        }

        await Task.WhenAll(observations).ConfigureAwait(false);
        var closingAt = Stopwatch.GetTimestamp();
        try
        {
            if (storage is not null)
            {
                await storage.DisposeAsync().ConfigureAwait(false);
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
