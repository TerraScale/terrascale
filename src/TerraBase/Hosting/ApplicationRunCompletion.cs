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
    public static async Task RunAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.Services.GetRequiredService<ILogger<ApplicationRunCompletion>>();
        var faults = app.Services.GetRequiredService<HostFaults>();
        BackgroundService[] background = [.. app.Services.GetServices<IHostedService>().OfType<BackgroundService>()];
        long stoppingAt = 0;
        using var stopping = app.Lifetime.ApplicationStopping.Register(() =>
        {
            Interlocked.Exchange(ref stoppingAt, Stopwatch.GetTimestamp());
            logger.ZLogInformation($"Shutdown started");
        });
        if (await StartedAsync(app, logger, faults))
        {
            var observations = background.Where(static service => service.ExecuteTask is not null).Select(ObserveAsync).ToArray();
            await ShutdownAsync(app, logger, faults);
            await Task.WhenAll(observations);
        }

        var disposingAt = Stopwatch.GetTimestamp();
        await app.DisposeAsync();
        logger.ZLogInformation($"Shutdown completed: services and storage closed after {Stopwatch.GetElapsedTime(disposingAt).TotalMilliseconds:@ElapsedMilliseconds} ms");
        foreach (var service in background)
        {
            if (service.ExecuteTask is { IsFaulted: true } faulted)
            {
                logger.ZLogCritical(faulted.Exception, $"Background service {service.GetType().Name:@Service} faulted; the host exits with a failure code");
                faults.Record(service.GetType().Name);
            }
        }

        async Task ObserveAsync(BackgroundService service)
        {
            if (service.ExecuteTask is { } running)
            {
                await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (Interlocked.Read(ref stoppingAt) is var at and not 0)
                {
                    logger.ZLogInformation($"Shutdown step {service.GetType().Name:@Step} completed after {Stopwatch.GetElapsedTime(at).TotalMilliseconds:@ElapsedMilliseconds} ms");
                }
            }
        }
    }

    private static async Task<bool> StartedAsync(WebApplication app, ILogger logger, HostFaults faults)
    {
        try
        {
            await app.StartAsync();
            return true;
        }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            logger.ZLogCritical(error, $"TerraBase failed to start; the host exits with a failure code");
            faults.Record("startup");
            return false;
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
