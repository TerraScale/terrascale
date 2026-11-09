namespace TerraBase.Testing;

internal static class StoragePoolHelpers
{
    internal static async Task JoinCleanupAsync(IEnumerable<Task> tasks, Exception? primaryFailure)
    {
        var joined = Task.WhenAll(tasks);
        try
        {
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
