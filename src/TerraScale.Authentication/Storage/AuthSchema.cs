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
        var upgraded = await runner.UpgradeAsync(cancellationToken);
        var failure = upgraded switch
        {
            AuthCompleted => (AuthUnavailable?)null,
            AuthUnavailable blocked => blocked,
        };
        if (failure is { } unavailable)
        {
            throw ApiException.Unavailable(new InvalidOperationException(unavailable.Message));
        }
    }

    public Task<bool> CurrentAsync(CancellationToken cancellationToken) => runner.CurrentAsync(cancellationToken);

    internal void MarkSeeded() => seeded.TrySetResult();

    internal void MarkBootstrapFailed() => bootstrapFailed = true;
}
