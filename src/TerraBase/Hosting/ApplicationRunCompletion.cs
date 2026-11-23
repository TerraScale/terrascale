using System.Diagnostics;
using ZLogger;

namespace TerraBase.Hosting;

internal sealed class ApplicationRunCompletion
{
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task RunAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.Services.GetRequiredService<ILogger<ApplicationRunCompletion>>();
        long stoppingAt = 0;
        using var stopping = app.Lifetime.ApplicationStopping.Register(() =>
        {
            Interlocked.Exchange(ref stoppingAt, Stopwatch.GetTimestamp());
            logger.ZLogInformation($"Shutdown started");
        });
        await app.StartAsync();
        BackgroundService[] background = [.. app.Services.GetServices<IHostedService>().OfType<BackgroundService>()];
        var observations = background.Where(static service => service.ExecuteTask is not null).Select(ObserveAsync).ToArray();
        await app.WaitForShutdownAsync();
        await Task.WhenAll(observations);
        var disposingAt = Stopwatch.GetTimestamp();
        await app.DisposeAsync();
        logger.ZLogInformation($"Shutdown completed: services and storage closed after {Stopwatch.GetElapsedTime(disposingAt).TotalMilliseconds:@ElapsedMilliseconds} ms");

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
}
