using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Changes
{
    /// <summary>The changes API, authorized and metered through the corresponding REST routes.</summary>
    public interface IChangesService : IService<IChangesService>
    {
        /// <summary><c>GET …/changes</c>, streamed with backpressure; LastEventId overrides cursor. A terminal refusal retains the REST fault.</summary>
        Task<ServerStreamingResult<TerraBaseChangesEvent>> StreamChangesAsync(string projectId, string database, string? cursor, string? lastEventId, IReadOnlyList<string>? collections);

        /// <summary><c>GET …/export</c>, one read snapshot, streamed with backpressure.</summary>
        Task<ServerStreamingResult<TerraBaseExportLine>> ExportAsync(string projectId, string database);
    }
}
