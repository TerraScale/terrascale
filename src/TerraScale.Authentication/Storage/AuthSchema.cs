using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Storage.Schema;

namespace TerraScale.Authentication.Storage;

public sealed class AuthSchema(TerraBaseDbUpRunner runner)
{
    private readonly TaskCompletionSource seeded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool bootstrapFailed;

    public Task Ready => seeded.Task;

    public bool BootstrapPending => !bootstrapFailed && !seeded.Task.IsCompleted;

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if (await runner.UpgradeAsync(cancellationToken) switch
        {
            Done => null,
            Unavailable unavailable => unavailable,
        } is { } failure)
        {
            throw ApiException.Unavailable(new InvalidOperationException(failure.Message));
        }
    }

    public Task<bool> CurrentAsync(CancellationToken cancellationToken) => runner.CurrentAsync(cancellationToken);

    internal void MarkSeeded() => seeded.TrySetResult();

    internal void MarkBootstrapFailed() => bootstrapFailed = true;
}
