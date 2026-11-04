using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

// Retained only until tests/TerraScale.Dashboard.E2E stops calling these point routes; delete this file then.

/// <summary>A stored JSON value read by key.</summary>
/// <param name="Id">Key.</param>
/// <param name="Version">Opaque version, also the ETag. Pass it to <see cref="TerraBasePrecondition.Matches"/>.</param>
/// <param name="State">Read state.</param>
/// <param name="Document">The JSON object.</param>
public sealed record TerraBaseDocument(string Id, string Version, string State, JsonElement Document);

/// <summary>The result of one keyed write.</summary>
/// <param name="Id">Key.</param>
/// <param name="Version">The new version.</param>
/// <param name="Offset">Commit offset of the write.</param>
/// <param name="Durability">Durability label.</param>
/// <param name="Document">Stored value after PATCH; omitted for other writes.</param>
public sealed record TerraBaseWriteResult(string Id, string Version, long Offset, string Durability, JsonElement? Document = null)
{
    /// <summary>Whether the write created the value.</summary>
    [JsonIgnore]
    public bool Created { get; init; }
}

/// <summary>The condition a keyed write requires; a failed condition throws <see cref="TerraBasePreconditionFailedException"/>.</summary>
public sealed class TerraBasePrecondition
{
    private readonly EntityTagHeaderValue? ifMatch;
    private readonly bool ifNoneMatch;

    private TerraBasePrecondition(EntityTagHeaderValue? ifMatch, bool ifNoneMatch)
    {
        this.ifMatch = ifMatch;
        this.ifNoneMatch = ifNoneMatch;
    }

    /// <summary>Create-only (<c>If-None-Match: *</c>).</summary>
    public static TerraBasePrecondition Absent { get; } = new(null, ifNoneMatch: true);

    /// <summary>The current version must be <paramref name="version"/> (<c>If-Match</c>).</summary>
    public static TerraBasePrecondition Matches(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        try
        {
            return new(new EntityTagHeaderValue($"\"{version}\""), ifNoneMatch: false);
        }
        catch (FormatException error)
        {
            throw new ArgumentException("version must be a TerraBase version.", nameof(version), error);
        }
    }

    internal void Apply(HttpRequestHeaders headers)
    {
        if (ifMatch is not null) headers.IfMatch.Add(ifMatch);
        if (ifNoneMatch) headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
    }
}

public sealed partial class TerraBaseDatabase
{
    private const string MergePatchMediaType = "application/merge-patch+json";

    /// <summary>Reads a value by key; <see langword="null"/> when it is absent.</summary>
    public async Task<TerraBaseDocument?> GetDocumentAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await connection.SendAsync(HttpMethod.Get, KeyPath(collection, id), null, Json.TerraBaseDocument, null, cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (TerraBaseNotFoundException error) when (error.ErrorCode == "not_found")
        {
            return null;
        }
    }

    /// <summary>Inserts or replaces a value by key (<c>PUT</c>).</summary>
    public async Task<TerraBaseWriteResult> PutDocumentAsync(
        string collection, string id, JsonElement document, TerraBasePrecondition? precondition = null, CancellationToken cancellationToken = default)
    {
        var body = TerraBaseConnection.Json(document, Json.JsonElement);
        var (result, status) = await connection.SendAsync(HttpMethod.Put, KeyPath(collection, id), body, Json.TerraBaseWriteResult, precondition is null ? null : precondition.Apply, cancellationToken)
            .ConfigureAwait(false);
        return result with { Created = status == HttpStatusCode.Created };
    }

    /// <summary>Applies an RFC 7396 merge patch to an existing value.</summary>
    public async Task<TerraBaseWriteResult> PatchDocumentAsync(
        string collection, string id, JsonElement mergePatch, TerraBasePrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Patch, KeyPath(collection, id), TerraBaseConnection.Json(mergePatch, Json.JsonElement, MergePatchMediaType),
            Json.TerraBaseWriteResult, precondition is null ? null : precondition.Apply, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Deletes an existing value by key.</summary>
    public async Task<TerraBaseWriteResult> DeleteDocumentAsync(
        string collection, string id, TerraBasePrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Delete, KeyPath(collection, id), null, Json.TerraBaseWriteResult, precondition is null ? null : precondition.Apply, cancellationToken)
            .ConfigureAwait(false)).Value;

    private string KeyPath(string collection, string id) =>
        $"{path}/collections/{TerraBaseConnection.Segment(collection, nameof(collection))}/documents/by-key" + TerraBaseConnection.Query(("id", TerraScaleIds.Validate(id)));
}
