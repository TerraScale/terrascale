using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

public interface IDashboardState
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task CheckReadyAsync(CancellationToken cancellationToken);
    Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken);
    // Every document of a collection, for background reconciliation; documents carry their own keys in their bodies.
    Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken cancellationToken);
    // A provider-filtered/projected page: callers must consume cursors, never buffer the collection.
    Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken cancellationToken);
    Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken);
    Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken);
    // List returns the provider's opaque document ID; it must not be hashed as a natural key again.
    Task DeleteListedAsync(string collection, TerraBaseDocument document, CancellationToken cancellationToken) =>
        DeleteAsync(collection, document.Id, document.Version, cancellationToken);
}
