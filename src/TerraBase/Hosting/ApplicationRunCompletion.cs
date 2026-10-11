using System.Runtime.ExceptionServices;

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
        var started = false;
        try
        {
            // Retain instances even if startup fails after launching only some of their tasks.
            background = [.. app.Services.GetServices<IHostedService>().OfType<BackgroundService>()];
            await app.StartAsync().ConfigureAwait(false);
            started = true;
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

        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

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
    }
}
