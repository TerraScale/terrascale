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
        Exception? failure = upgraded switch
        {
            AuthCompleted => null,
            AuthUnavailable blocked => ApiException.Unavailable(new InvalidOperationException(blocked.Message)),
            SchemaScriptChanged changed => new InvalidOperationException($"Schema script {changed.Script} changed after it was applied; merged scripts are immutable"),
        };
        if (failure is not null)
        {
            throw failure;
        }
    }

    public Task<bool> CurrentAsync(CancellationToken cancellationToken) => runner.CurrentAsync(cancellationToken);

    internal void MarkSeeded() => seeded.TrySetResult();

    internal void MarkBootstrapFailed() => bootstrapFailed = true;
}
