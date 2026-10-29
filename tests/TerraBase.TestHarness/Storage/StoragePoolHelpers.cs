namespace TerraBase.Testing;

internal static class StoragePoolHelpers
{
    internal static async Task DisposeResponseAsync(Task<HttpResponseMessage> request)
    {
        try
        {
            using var response = await request;
        }
        catch (OperationCanceledException)
        {
            // A cancelled request has no response, but its task still needs to finish.
        }
    }

    internal static async Task JoinCleanupAsync(IEnumerable<Task> tasks, Exception? primaryFailure)
    {
        var joined = Task.WhenAll(tasks);
        try
        {
            // Finish tracked work before disposing its clients, sessions and listener.
            await joined;
        }
        catch (OperationCanceledException) when (joined.IsCanceled)
        {
        }
        catch (Exception error)
        {
            var failure = joined.Exception ?? error;
            if (primaryFailure is null)
            {
                throw new AggregateException("storage test cleanup failed", failure);
            }

            primaryFailure.Data["CleanupFailure"] = primaryFailure.Data["CleanupFailure"] is Exception earlier
                ? new AggregateException(earlier, failure) : failure;
        }
    }

}
