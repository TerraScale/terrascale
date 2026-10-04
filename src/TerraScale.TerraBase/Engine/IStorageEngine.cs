using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Engine;

/// <summary>
/// The database engine TerraBase stores JSON documents in. Each database is isolated; one
/// implementation per substrate (SQLite today, Turso or others later) lives under <c>Engine/&lt;Name&gt;/</c>.
/// </summary>
/// <remarks>
/// A client database is addressed only by a <see cref="TenantContext"/> naming its organization, project and
/// database, and is stored under that organization: storage of one tenant is never reachable through another's.
/// </remarks>
public interface IStorageEngine : IAsyncDisposable
{
    /// <summary>Short substrate name, for diagnostics (for example <c>sqlite</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Creates the database of <paramref name="database"/> atomically within this process: among concurrent calls for the same
    /// database exactly one returns <see langword="true"/>. Returns <see langword="false"/> when it already existed.
    /// </summary>
    ValueTask<bool> CreateDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>Opens an existing database, or <see langword="null"/> when it does not exist.</summary>
    ValueTask<IDatabaseStore?> OpenDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Creates <paramref name="target"/> as a consistent snapshot copy of <paramref name="source"/> in the same organization.
    /// Returns <see langword="false"/> when <paramref name="source"/> does not exist; throws when <paramref name="target"/>
    /// already exists or belongs to another organization.
    /// </summary>
    ValueTask<bool> CopyDatabaseAsync(TenantContext source, TenantContext target, CancellationToken cancellationToken);

    /// <summary>Deletes a database and its files; <see langword="false"/> when it did not exist.</summary>
    ValueTask<bool> DeleteDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>Opens TerraBase's own database (catalog, API keys), creating it on first use. No tenant can address it.</summary>
    ValueTask<IDatabaseStore> OpenSystemDatabaseAsync(CancellationToken cancellationToken);
}
