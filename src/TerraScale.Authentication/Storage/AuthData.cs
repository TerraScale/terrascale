using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Storage;

public sealed class AuthData(IDbContextFactory<AuthDbContext> contexts)
{
    public async Task<T> ReadAsync<T>(Func<AuthDbContext, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await read(db, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> SaveAsync(Change change, CancellationToken cancellationToken) => this.SaveAsync([change], cancellationToken);

    public async Task<bool> SaveAsync(IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return true;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var change in changes.Where(change => !change.Assertion))
        {
            change.Apply(db);
        }

        foreach (var change in changes.Where(change => change.Assertion))
        {
            if (!change.Apply(db))
            {
                return false;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException failure) when (IsConflict(failure))
        {
            return false;
        }
    }

    public async Task<T> TransactAsync<T>(Func<AuthDbContext, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            var result = await work(db, token).ConfigureAwait(false);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public static bool IsConflict(DbUpdateException failure) => failure switch
    {
        DbUpdateConcurrencyException => true,
        { InnerException: TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.UniqueViolation or TerraBaseSqlErrorCodes.PrimaryKeyViolation or TerraBaseSqlErrorCodes.ForeignKeyViolation } } => true,
        _ => false,
    };

    public static bool IsUnavailable(Exception failure) => failure switch
    {
        ApiException { Status: StatusCodes.Status503ServiceUnavailable } => true,
        TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.SerializationConflict or TerraBaseSqlErrorCodes.TenantBusy or TerraBaseSqlErrorCodes.Unavailable } => true,
        TerraBaseUnavailableException or TimeoutException => true,
        _ when failure.InnerException is { } inner => IsUnavailable(inner),
        _ => false,
    };
}
