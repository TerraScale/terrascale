using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Common.Accounts;

internal static class RowSync
{
    public static void Sync<TRow>(DashboardDbContext db, IEnumerable<TRow> loaded, IEnumerable<TRow> desired, Func<TRow, string> key) where TRow : class
    {
        var rows = loaded.ToDictionary(key, StringComparer.Ordinal);
        foreach (var next in desired)
        {
            if (rows.Remove(key(next), out var row))
            {
                db.Entry(row).CurrentValues.SetValues(next);
            }
            else
            {
                db.Add(next);
            }
        }

        db.RemoveRange(rows.Values);
    }

    public static async Task<List<T>> QueryAsync<T>(IDbContextFactory<DashboardDbContext> storage, Func<DashboardDbContext, IQueryable<T>> query, CancellationToken cancellationToken) where T : class
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        return await query(db).AsNoTracking().ToListAsync(cancellationToken);
    }

    public static async Task CreateAsync(DashboardDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (Duplicate(error))
        {
            throw new StateConflictException();
        }
    }

    public static async Task ReplaceAsync(DashboardDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new StateConflictException();
        }
        catch (DbUpdateException error) when (Duplicate(error))
        {
            throw new AccountAccessException("The change conflicts with an existing record.", StatusCodes.Status409Conflict);
        }
    }

    private static bool Duplicate(DbUpdateException error)
    {
        return error.InnerException is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.PrimaryKeyViolation or TerraBaseSqlErrorCodes.UniqueViolation };
    }

    public static DateTimeOffset At(DateTime value)
    {
        return new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
