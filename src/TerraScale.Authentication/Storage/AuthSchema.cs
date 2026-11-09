using Microsoft.EntityFrameworkCore;

namespace TerraScale.Authentication.Storage;

public sealed class AuthSchema(IDbContextFactory<AuthDbContext> contexts)
{
    private readonly TaskCompletionSource seeded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => this.seeded.Task;

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void MarkSeeded() => this.seeded.TrySetResult();
}
