using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

/// <summary>What a shape read names: the database, the collection incarnation and the filter (shape reads v1, section 5).</summary>
/// <param name="Database">The resolved tenant database.</param>
/// <param name="Collection">The collection; its id is the collection generation.</param>
/// <param name="CanonicalFilter">The compact JSON of <c>where</c>; empty when absent.</param>
/// <param name="Filter">The parsed filter; null when absent.</param>
internal sealed record ShapeScope(TenantContext Database, CollectionInfo Collection, string CanonicalFilter, DocumentFilter? Filter);

/// <summary>One machine's feed of one shape: its shape id and handle (section 5).</summary>
internal sealed record ShapeLineage(string ShapeId, string Handle);

/// <summary>A servable response: its exact body bytes, which the <c>ETag</c> hashes, and the headers they carry.</summary>
internal sealed record ShapeBody(ReadOnlyMemory<byte> Json, string NextOffset, bool UpToDate);

/// <summary>The client's state cannot continue on this lineage: <c>409 must_refetch</c> (section 8).</summary>
internal sealed record MustRefetch(string Message);

internal readonly union LineageResult(ShapeLineage, Unavailable);

/// <summary>The newest published snapshot's offset <c>S</c>, or why there is none.</summary>
internal readonly union SnapshotResult(long, MustRefetch, Unavailable);

/// <summary>A sealed object or fresh body, or why it is not served (<c>shape_busy</c>, <c>shape_too_large</c>, <c>unavailable</c>).</summary>
internal readonly union ShapeRead(ShapeBody, MustRefetch, QuotaExceeded, Unavailable);

/// <summary>The local commit boundary a proven resume redirects to, or why the resume is refused.</summary>
internal readonly union ResumeResult(long, MustRefetch, Unavailable);

/// <summary>Resolves the serving machine's lineage of a shape.</summary>
internal interface IShapeLineages
{
    ValueTask<LineageResult> ResolveAsync(ShapeScope scope, CancellationToken cancellationToken);
}

/// <summary>Sealed snapshots.</summary>
internal interface IShapeSnapshots
{
    /// <summary>The newest published snapshot, sealing one at the feed head when the lineage has none.</summary>
    ValueTask<SnapshotResult> NewestAsync(ShapeScope scope, ShapeLineage lineage, CancellationToken cancellationToken);

    /// <summary>The page of <paramref name="snapshot"/> after document <paramref name="afterId"/>; null for the first page.</summary>
    ValueTask<ShapeRead> PageAsync(ShapeScope scope, ShapeLineage lineage, long snapshot, string? afterId, CancellationToken cancellationToken);
}

/// <summary>Closed chunks below the sealed head, and the fresh body at it.</summary>
internal interface IShapeChunks
{
    ValueTask<ShapeRead> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken);
}

/// <summary>Live long-polls and their flights.</summary>
internal interface IShapeLiveTail
{
    /// <summary>Waits at <paramref name="offset"/>; <paramref name="cursor"/> is the request's live bucket, null when absent.</summary>
    ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, long? cursor, CancellationToken cancellationToken);
}

/// <summary>Cross-machine resumes.</summary>
internal interface IShapeResume
{
    /// <summary>Checks a resume envelope's 16-byte scope and inner changes cursor against this machine.</summary>
    ValueTask<ResumeResult> ResolveAsync(ShapeScope scope, ShapeLineage lineage, ReadOnlyMemory<byte> scope16, FeedCursor cursor, CancellationToken cancellationToken);
}

/// <summary>
/// The shape services until the real components are connected: nothing is served, and every resume is unproven, as the
/// protocol requires until resume proofs land (section 9, planned gate).
/// </summary>
internal sealed class UnavailableShapes : IShapeLineages, IShapeSnapshots, IShapeChunks, IShapeLiveTail, IShapeResume
{
    private static readonly Unavailable NotServed = new("shape reads are not served yet");

    public ValueTask<LineageResult> ResolveAsync(ShapeScope scope, CancellationToken cancellationToken) => ValueTask.FromResult<LineageResult>(NotServed);

    public ValueTask<SnapshotResult> NewestAsync(ShapeScope scope, ShapeLineage lineage, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SnapshotResult>(NotServed);

    public ValueTask<ShapeRead> PageAsync(ShapeScope scope, ShapeLineage lineage, long snapshot, string? afterId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ShapeRead>(NotServed);

    public ValueTask<ShapeRead> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ShapeRead>(NotServed);

    public ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, long? cursor, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ShapeRead>(NotServed);

    public ValueTask<ResumeResult> ResolveAsync(
        ShapeScope scope, ShapeLineage lineage, ReadOnlyMemory<byte> scope16, FeedCursor cursor, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ResumeResult>(new MustRefetch("resume prefix cannot be proven"));
}

/// <summary>A shape response body (section 6), documented for OpenAPI; served bodies are the sealed bytes, never re-serialized.</summary>
/// <param name="ShapeId">32 lowercase hex characters.</param>
/// <param name="Handle"><c>h1-</c> and 16 lowercase hex characters.</param>
/// <param name="Offset">The request's offset.</param>
/// <param name="NextOffset">Where to continue.</param>
/// <param name="CoveredOffset">The decimal offset the body covers.</param>
/// <param name="ValuesAt">The feed head the values were materialized at.</param>
/// <param name="UpToDate">Only fresh bodies at the sealed head are up to date.</param>
/// <param name="CursorResume">Opaque resume cursor.</param>
/// <param name="Changes">One change per commit, in order.</param>
internal sealed record ShapeResponse(
    string ShapeId, string Handle, string Offset, string NextOffset, string CoveredOffset, string ValuesAt, bool UpToDate, string CursorResume,
    IReadOnlyList<ShapeChange> Changes);

/// <summary>One commit's operations on the collection.</summary>
/// <param name="Offset">The commit's <c>commit_seq</c>, or <c>S</c> on snapshot pages.</param>
/// <param name="Ops">Each touched document once, in ordinal id order.</param>
internal sealed record ShapeChange(string Offset, IReadOnlyList<ShapeOperation> Ops);

/// <summary>An <c>upsert</c> with the document's version and value, or a <c>delete</c>.</summary>
internal sealed record ShapeOperation(
    string Op, string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Document = null);

/// <summary><c>409 must_refetch</c>: <c>{"error":{"code":"must_refetch","message":…,"handle":…}}</c>.</summary>
internal sealed record ShapeErrorBody(ShapeErrorDetail Error);

/// <summary>The refetch error and the lineage to restart on.</summary>
internal sealed record ShapeErrorDetail(string Code, string Message, string Handle);

/// <summary>Source-generated JSON metadata of the shape slice.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ShapeResponse))]
[JsonSerializable(typeof(ShapeErrorBody))]
internal sealed partial class ShapeJsonContext : JsonSerializerContext;
