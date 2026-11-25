using System.Diagnostics;
using ZLogger;

namespace TerraBase.Hosting;

internal sealed class HostFaults
{
    private string? _faulted;

    public string? Faulted => Volatile.Read(ref _faulted);

    public void Record(string source) => Interlocked.CompareExchange(ref _faulted, source, null);
}

internal sealed class ApplicationRunCompletion
{
    public static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(25);

    private enum Start
    {
        Running,
        Stopped,
        Failed,
    }

    public static async Task RunAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        ILogger? completed = null;
        long stoppingAt = 0;
        try
        {
            var logger = app.Services.GetRequiredService<ILogger<ApplicationRunCompletion>>();
            completed = logger;
            var faults = app.Services.GetRequiredService<HostFaults>();
            using var stopping = app.Lifetime.ApplicationStopping.Register(() =>
            {
                Interlocked.Exchange(ref stoppingAt, Stopwatch.GetTimestamp());
                logger.ZLogInformation($"Shutdown started");
            });
            var started = await StartAsync(app, logger, faults);
            if (started == Start.Failed)
            {
                return;
            }

            BackgroundService[] background = [.. app.Services.GetServices<IHostedService>().OfType<BackgroundService>()];
            if (started == Start.Running)
            {
                var observations = background.Where(static service => service.ExecuteTask is not null).Select(ObserveAsync).ToArray();
                await ShutdownAsync(app, logger, faults);
                var remaining = ShutdownBudget - Stopwatch.GetElapsedTime(Interlocked.Read(ref stoppingAt) is var at and not 0 ? at : Stopwatch.GetTimestamp());
                var observed = Task.WhenAll(observations);
                using var budget = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, app.Services.GetRequiredService<TimeProvider>());
                await observed.WaitAsync(budget.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (!observed.IsCompleted)
                {
                    logger.ZLogWarning($"Shutdown budget elapsed with background services still running; disposing the host");
                }
            }

            foreach (var service in background)
            {
                if (service.ExecuteTask is { IsFaulted: true } faulted)
                {
                    logger.ZLogCritical(faulted.Exception, $"Background service {service.GetType().Name:@Service} faulted; the host exits with a failure code");
                    faults.Record(service.GetType().Name);
                }
            }
        }
        finally
        {
            var disposingAt = Stopwatch.GetTimestamp();
            await app.DisposeAsync();
            completed?.ZLogInformation($"Shutdown completed: services and storage closed after {Stopwatch.GetElapsedTime(disposingAt).TotalMilliseconds:@ElapsedMilliseconds} ms");
        }

        async Task ObserveAsync(BackgroundService service)
        {
            if (service.ExecuteTask is { } running)
            {
                await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (Interlocked.Read(ref stoppingAt) is var at and not 0)
                {
                    completed?.ZLogInformation($"Shutdown step {service.GetType().Name:@Step} completed after {Stopwatch.GetElapsedTime(at).TotalMilliseconds:@ElapsedMilliseconds} ms");
                }
            }
        }
    }

    private static async Task<Start> StartAsync(WebApplication app, ILogger logger, HostFaults faults)
    {
        try
        {
            await app.StartAsync();
            return Start.Running;
        }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return Start.Stopped;
        }
        catch (Exception error)
        {
            logger.ZLogCritical(error, $"TerraBase failed to start; the host exits with a failure code");
            faults.Record("startup");
            return Start.Failed;
        }
    }

    private static async Task ShutdownAsync(WebApplication app, ILogger logger, HostFaults faults)
    {
        try
        {
            await app.WaitForShutdownAsync();
        }
        catch (Exception error)
        {
            logger.ZLogCritical(error, $"TerraBase stopped with a fault; the host exits with a failure code");
            faults.Record("shutdown");
        }
    }
}
