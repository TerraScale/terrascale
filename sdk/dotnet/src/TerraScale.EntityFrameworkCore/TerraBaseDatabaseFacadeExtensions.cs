using Microsoft.EntityFrameworkCore.Infrastructure;
using TerraScale.DotNet.TerraBase;

namespace Microsoft.EntityFrameworkCore;

public static class TerraBaseDatabaseFacadeExtensions
{
    extension(DatabaseFacade database)
    {
        public string GetTerraBaseBookmark() => Connection(database).Bookmark;

        public void ObserveTerraBaseBookmark(string bookmark) => Connection(database).ObserveBookmark(bookmark);
    }

    private static TerraBaseDbConnection Connection(DatabaseFacade database) =>
        database.GetDbConnection() as TerraBaseDbConnection ?? throw new InvalidOperationException("The context does not use the TerraBase provider.");
}
