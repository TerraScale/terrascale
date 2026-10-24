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
    Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken);
    Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken);
}
