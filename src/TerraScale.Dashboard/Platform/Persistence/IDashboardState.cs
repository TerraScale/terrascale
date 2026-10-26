using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

public interface IDashboardState
{
    // Internal persistence primitive; pages retain provider IDs and opaque cursors, never natural keys.
    internal Task<TerraBaseDocumentPage> ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken cancellationToken);
    // Result IDs also remain provider IDs; combine assertions and writes to one target in its mutation precondition.
    internal Task<TerraBaseTransactionResult> TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken cancellationToken);
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

// Explicit preconditions only. Constructing an operation maps a natural key exactly once.
internal sealed record DashboardStateOperation
{
    internal TerraBaseTransactionOperation Value { get; }
    private DashboardStateOperation(TerraBaseTransactionOperation value) => Value = value;
    internal static DashboardStateOperation Create(string collection, string key, JsonElement document) =>
        new(TerraBaseTransactionOperation.Create(collection, document, TerraBaseDashboardState.DocumentId(collection, key)));
    internal static DashboardStateOperation Replace(string collection, string key, JsonElement document, string version) =>
        new(TerraBaseTransactionOperation.Put(collection, TerraBaseDashboardState.DocumentId(collection, key), document, Version(version)));
    internal static DashboardStateOperation Delete(string collection, string key, string version) =>
        new(TerraBaseTransactionOperation.Delete(collection, TerraBaseDashboardState.DocumentId(collection, key), Version(version)));
    internal static DashboardStateOperation DeleteListed(string collection, TerraBaseDocument document) =>
        new(TerraBaseTransactionOperation.Delete(collection, document.Id, Version(document.Version)));
    internal static DashboardStateOperation Assert(string collection, string key, JsonElement filter) =>
        new(TerraBaseTransactionOperation.Assert(collection, TerraBaseDashboardState.DocumentId(collection, key), filter));
    internal static DashboardStateOperation AssertAbsent(string collection, string key) =>
        new(TerraBaseTransactionOperation.AssertAbsent(collection, TerraBaseDashboardState.DocumentId(collection, key)));
    internal static DashboardStateOperation AssertCount(string collection, JsonElement? filter, long? gte, long? lte) =>
        new(TerraBaseTransactionOperation.AssertCount(collection, filter, gte, lte));
    private static string Version(string version)
    {
        if (version == "*") throw new ArgumentException("A specific document version is required.", nameof(version));
        _ = TerraBasePrecondition.Matches(version); return version;
    }
}
