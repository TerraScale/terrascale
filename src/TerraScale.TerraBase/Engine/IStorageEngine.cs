namespace TerraScale.TerraBase.Engine;

/// <summary>
/// The database engine TerraBase stores JSON documents in. Each database is isolated; one
/// implementation per substrate (SQLite today, Turso or others later) lives under <c>Engine/&lt;Name&gt;/</c>.
/// </summary>
public interface IStorageEngine : IAsyncDisposable
{
    /// <summary>Short substrate name, for diagnostics (for example <c>sqlite</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Creates a database atomically: among concurrent calls for the same name exactly one returns
    /// <see langword="true"/>. Returns <see langword="false"/> when it already existed.
    /// </summary>
    ValueTask<bool> CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken);

    /// <summary>Opens an existing database, or <see langword="null"/> when it does not exist.</summary>
    ValueTask<IDatabaseStore?> OpenDatabaseAsync(DatabaseName name, CancellationToken cancellationToken);

    /// <summary>Lists client databases (never <see cref="DatabaseName.System"/>) in name order.</summary>
    IAsyncEnumerable<DatabaseName> ListDatabasesAsync(CancellationToken cancellationToken);
}
