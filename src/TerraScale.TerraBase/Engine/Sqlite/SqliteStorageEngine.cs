using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace TerraScale.TerraBase.Engine.Sqlite;

/// <summary>
/// One SQLite file per database (<c>&lt;data-directory&gt;/&lt;name&gt;.db</c>), WAL journal,
/// connections pooled by Microsoft.Data.Sqlite.
/// </summary>
/// <remarks>
/// ponytail: Microsoft.Data.Sqlite runs async calls synchronously on the caller's thread;
/// move disk work to a dedicated scheduler when request latency under write load matters.
/// </remarks>
internal sealed class SqliteStorageEngine(string dataDirectory) : IStorageEngine
{
    private const string FileExtension = ".db";

    public string Name => "sqlite";

    public async ValueTask<IDatabaseStore?> OpenDatabaseAsync(DatabaseName name, bool create, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataDirectory, name.Value + FileExtension);
        if (!create && !File.Exists(path))
        {
            return null;
        }

        Directory.CreateDirectory(dataDirectory);
        var store = new SqliteDatabaseStore(name, path);
        await store.MigrateAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async IAsyncEnumerable<DatabaseName> ListDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dataDirectory))
        {
            yield break;
        }

        var names = Directory.EnumerateFiles(dataDirectory, "*" + FileExtension)
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal);
        foreach (var file in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DatabaseName.TryParse(file, out var name))
            {
                yield return name;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        // Pooled connections keep files open; release them so the data directory can be removed.
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
