using Microsoft.EntityFrameworkCore;

namespace TerraScale.Authentication.Storage;

public sealed class AuthSchema(IDbContextFactory<AuthDbContext> contexts)
{
    private readonly TaskCompletionSource seeded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool bootstrapFailed;

    public Task Ready => this.seeded.Task;

    public bool BootstrapPending => !this.bootstrapFailed && !this.seeded.Task.IsCompleted;

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void MarkSeeded() => this.seeded.TrySetResult();

    internal void MarkBootstrapFailed() => this.bootstrapFailed = true;
}
