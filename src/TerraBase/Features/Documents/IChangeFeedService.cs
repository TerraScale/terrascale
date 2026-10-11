using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>A collection's ordered change feed page.</summary>
/// <param name="Changes">Writes in local commit and append order; inserts and updates are upserts.</param>
/// <param name="ResumeToken">Opaque position to use on the next poll, even for an empty page.</param>
/// <param name="HasMore">More retained changes existed in the read snapshot.</param>
public sealed record ChangeFeedBody(
    IReadOnlyList<ChangeFeedEvent> Changes,
    [property: JsonPropertyName("resume_token")] string ResumeToken,
    [property: JsonPropertyName("has_more")] bool HasMore);

/// <summary>One committed document state in a collection feed.</summary>
/// <param name="Operation">Upsert for insert/update; delete for a tombstone.</param>
/// <param name="Id">Document identifier.</param>
/// <param name="Offset">Local database commit offset, encoded as decimal text.</param>
/// <param name="Sequence">Append sequence within local history, encoded as decimal text.</param>
/// <param name="Document">Stored JSON for an upsert, null for a delete.</param>
public sealed record ChangeFeedEvent(string Operation, string Id, string Offset, string Sequence, JsonElement? Document);

/// <summary>Reads bounded pages of a collection's retained local history.</summary>
public interface IChangeFeedService
{
    /// <summary>Prepares a page whose memory admission remains held until response delivery completes.</summary>
    ValueTask<IResult> ReadAsync(TenantContext database, string collection, string? resumeToken, int limit, CancellationToken cancellationToken);
}
