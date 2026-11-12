using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Storage;

public sealed class AuthData(IDbContextFactory<AuthDbContext> contexts)
{
    private readonly AsyncLocal<WriteAudit?> audit = new();

    public async Task<T> AuditWritesAsync<T>(Change success, Func<CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentNullException.ThrowIfNull(write);
        var previous = this.audit.Value;
        var current = new WriteAudit(success);
        this.audit.Value = current;
        try
        {
            var result = await write(cancellationToken).ConfigureAwait(false);
            if (!current.Committed && !await this.SaveAsync([], cancellationToken).ConfigureAwait(false))
            {
                throw ApiException.Unavailable();
            }

            return result;
        }
        finally
        {
            this.audit.Value = previous;
        }
    }

    public async Task<T> ReadAsync<T>(Func<AuthDbContext, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await read(db, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> InBatchesAsync(int batchSize, Func<AuthDbContext, CancellationToken, Task<int>> statement, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentNullException.ThrowIfNull(statement);
        var total = 0;
        int changed;
        do
        {
            changed = await this.ReadAsync(statement, cancellationToken).ConfigureAwait(false);
            total += changed;
        }
        while (changed >= batchSize);

        return total;
    }

    public Task<bool> SaveAsync(Change change, CancellationToken cancellationToken) => this.SaveAsync([change], cancellationToken);

    public async Task<bool> SaveAsync(IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var currentAudit = this.audit.Value;
        if (currentAudit is { Committed: false })
        {
            changes = [.. changes, currentAudit.Success];
        }

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
            currentAudit?.Committed = true;
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

    private sealed class WriteAudit(Change success)
    {
        public Change Success { get; } = success;

        public bool Committed { get; set; }
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
