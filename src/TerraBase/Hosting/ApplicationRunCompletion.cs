using System.Diagnostics;
using ZLogger;

namespace TerraBase.Hosting;

internal sealed record ServicesStopped;

internal sealed record ServiceFaulted(string Service);

internal readonly union ApplicationRunOutcome(ServicesStopped, ServiceFaulted);

internal sealed class ApplicationRunCompletion
{
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<ApplicationRunOutcome> RunAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.Services.GetRequiredService<ILogger<ApplicationRunCompletion>>();
        long stoppingAt = 0;
        using var stopping = app.Lifetime.ApplicationStopping.Register(() =>
        {
            Interlocked.Exchange(ref stoppingAt, Stopwatch.GetTimestamp());
            logger.ZLogInformation($"Shutdown started");
        });
        if (!await StartedAsync(app))
        {
            await app.DisposeAsync();
            return new ServicesStopped();
        }

        BackgroundService[] background = [.. app.Services.GetServices<IHostedService>().OfType<BackgroundService>()];
        var observations = background.Where(static service => service.ExecuteTask is not null).Select(ObserveAsync).ToArray();
        await app.WaitForShutdownAsync();
        await Task.WhenAll(observations);
        var disposingAt = Stopwatch.GetTimestamp();
        await app.DisposeAsync();
        logger.ZLogInformation($"Shutdown completed: services and storage closed after {Stopwatch.GetElapsedTime(disposingAt).TotalMilliseconds:@ElapsedMilliseconds} ms");
        return background.FirstOrDefault(static service => service.ExecuteTask is { IsFaulted: true }) is { } faulted
            ? new ServiceFaulted(faulted.GetType().Name)
            : new ServicesStopped();

        async Task ObserveAsync(BackgroundService service)
        {
            if (service.ExecuteTask is { } running)
            {
                await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (running.Exception is { } fault)
                {
                    logger.ZLogCritical(fault, $"Background service {service.GetType().Name:@Service} faulted; the host exits with a failure code");
                }

                if (Interlocked.Read(ref stoppingAt) is var at and not 0)
                {
                    logger.ZLogInformation($"Shutdown step {service.GetType().Name:@Step} completed after {Stopwatch.GetElapsedTime(at).TotalMilliseconds:@ElapsedMilliseconds} ms");
                }
            }
        }
    }

    private static async Task<bool> StartedAsync(WebApplication app)
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
    }
}
