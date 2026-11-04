using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public interface IDashboardState
    {
        Task InitializeAsync(CancellationToken cancellationToken);

        Task CheckReadyAsync(CancellationToken cancellationToken);

        Task<StateRecord?> GetAsync(string table, string id, CancellationToken cancellationToken);

        Task<IReadOnlyList<StateRecord>> ListAsync(string table, CancellationToken cancellationToken);

        Task<StatePage> PageAsync(string table, IReadOnlyDictionary<string, string> scope, IReadOnlyList<string>? fields, int limit, string? cursor,
            CancellationToken cancellationToken);

        Task PutAsync(string table, string id, JsonElement body, StatePrecondition precondition, CancellationToken cancellationToken);

        Task DeleteAsync(string table, string id, string version, CancellationToken cancellationToken);
    }
}
