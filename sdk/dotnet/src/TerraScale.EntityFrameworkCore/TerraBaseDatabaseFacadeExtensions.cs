using Microsoft.EntityFrameworkCore.Infrastructure;
using TerraScale.DotNet.TerraBase;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Reads and extends the <c>TerraScale-Bookmark</c> a TerraBase context's database tracks.</summary>
public static class TerraBaseDatabaseFacadeExtensions
{
    extension(DatabaseFacade database)
    {
        /// <summary>
        /// The newest <c>TerraScale-Bookmark</c> observed for the context's database, shared by every context and
        /// connection of the same <see cref="TerraBaseSqlDatabase"/>; empty before the first answer. Hand it to another
        /// process's <see cref="ObserveTerraBaseBookmark"/> so its reads include this context's saves.
        /// </summary>
        /// <returns>The encoded bookmark, or empty.</returns>
        public string GetTerraBaseBookmark() => Connection(database).Bookmark;

        /// <summary>
        /// Merges a <c>TerraScale-Bookmark</c> from another process into the database's tracked bookmark; later queries and
        /// saves observe it. Throws <see cref="ArgumentException"/> when <paramref name="bookmark"/> is not one TerraScale returned.
        /// </summary>
        /// <param name="bookmark">The bookmark another process read with <see cref="GetTerraBaseBookmark"/>.</param>
        public void ObserveTerraBaseBookmark(string bookmark) => Connection(database).ObserveBookmark(bookmark);
    }

    private static TerraBaseDbConnection Connection(DatabaseFacade database) =>
        database.GetDbConnection() as TerraBaseDbConnection ?? throw new InvalidOperationException("The context does not use the TerraBase provider.");
}
