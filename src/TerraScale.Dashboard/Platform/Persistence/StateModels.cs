using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public sealed record StateRecord(string Id, string Version, JsonElement Body);

    public sealed record StatePage(IReadOnlyList<StateRecord> Items, string? NextCursor);

    public sealed record StatePrecondition
    {
        private StatePrecondition(string? version)
        {
            Version = version;
        }

        public static StatePrecondition Absent { get; } = new((string?)null);

        public string? Version { get; }

        public static StatePrecondition Matches(string version)
        {
            ArgumentException.ThrowIfNullOrEmpty(version);
            return new(version);
        }
    }

    public sealed class StateConflictException() : HttpRequestException("Dashboard state changed concurrently.", null, HttpStatusCode.PreconditionFailed);
}
