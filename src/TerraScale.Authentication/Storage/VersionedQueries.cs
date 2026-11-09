using Microsoft.EntityFrameworkCore;

namespace TerraScale.Authentication.Storage;

public static class VersionedQueries
{
    extension<T>(IQueryable<T> query)
        where T : class
    {
        public IQueryable<Versioned<T>> WithVersion() =>
            query.Select(row => new Versioned<T>(row, EF.Property<string>(row, AuthDbContext.VersionProperty)));
    }
}
