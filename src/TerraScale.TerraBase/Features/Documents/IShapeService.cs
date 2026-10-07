using System.Buffers.Text;
using System.Globalization;
using System.Text;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>Kinds of shape offsets (docs/shape-reads.md).</summary>
public enum ShapeOffsetKind
{
    /// <summary><c>-1</c>: start a new sync.</summary>
    Initial,

    /// <summary>
    /// <c>snap.&lt;S&gt;.&lt;P&gt;</c>: a snapshot page of head <c>S</c> after document <c>P</c>, the unpadded base64url of the
    /// document id's UTF-8 (<c>0</c> for the first page; an encoded id is never a single character).
    /// </summary>
    Snapshot,

    /// <summary>A decimal log position: every change through that commit offset was delivered.</summary>
    Log,
}

/// <summary>A position in a shape's log.</summary>
/// <param name="Kind">Offset kind.</param>
/// <param name="Position">Snapshot head <c>S</c> or log position.</param>
/// <param name="AfterId">Last document id of the previous snapshot page; <see langword="null"/> for the first.</param>
public readonly record struct ShapeOffset(ShapeOffsetKind Kind, long Position, string? AfterId = null)
{
    /// <summary>The <c>-1</c> offset.</summary>
    public static ShapeOffset Initial { get; } = new(ShapeOffsetKind.Initial, -1);

    /// <summary>Parses the canonical text form; <see langword="null"/> when <paramref name="text"/> is not one.</summary>
    public static ShapeOffset? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == "-1")
        {
            return Initial;
        }

        var parts = text.Split('.');
        if (parts is ["snap", var head, var after] && Decimal(head) is { } s)
        {
            if (after == "0")
            {
                return new ShapeOffset(ShapeOffsetKind.Snapshot, s);
            }

            // Any stored id, legacy ones included, round-trips; only the canonical encoding is accepted.
            return Id(after) is { } id && Encode(id) == after ? new ShapeOffset(ShapeOffsetKind.Snapshot, s, id) : null;
        }

        return parts is [var log] && Decimal(log) is { } o ? new ShapeOffset(ShapeOffsetKind.Log, o) : null;
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        ShapeOffsetKind.Initial => "-1",
        ShapeOffsetKind.Snapshot => $"snap.{DocumentRoute.Decimal(Position)}.{(AfterId is null ? "0" : Encode(AfterId))}",
        _ => DocumentRoute.Decimal(Position),
    };

    private static string Encode(string id) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(id));

    private static string? Id(string encoded)
    {
        if (encoded.Length < 2 || !Base64Url.IsValid(encoded))
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(encoded));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // Canonical decimals only, so every offset has exactly one spelling and one cache key.
    private static long? Decimal(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && DocumentRoute.Decimal(value) == text
            ? value
            : null;
}

/// <summary>One shape read.</summary>
/// <param name="Database">The tenant's database: organization, project and database ids (TS-475).</param>
/// <param name="Collection">Collection.</param>
/// <param name="Filter">Parsed <c>where</c> filter; <see langword="null"/> for every document.</param>
/// <param name="CanonicalFilter">Compact JSON of the filter, part of the shape identity; empty for none.</param>
/// <param name="Offset">Requested offset.</param>
/// <param name="Handle">Requested handle; <see langword="null"/> only with <see cref="ShapeOffset.Initial"/>.</param>
/// <param name="Live">Whether to wait for a commit when the log position is the head.</param>
public sealed record ShapeRequest(
    TenantContext Database, string Collection, DocumentFilter? Filter, string CanonicalFilter, ShapeOffset Offset, string? Handle, bool Live)
{
    /// <summary>
    /// Everything that names the shape: organization (the tenant), project, database, collection and filter.
    /// It keys the shape id, the handle and request collapsing, so no two tenants ever share them.
    /// </summary>
    internal string Scope => string.Join('\0', Database.OrganizationId, Database.ProjectId, Database.DatabaseId, Collection, CanonicalFilter);
}

/// <summary>What a shape read answers.</summary>
public enum ShapeStatus
{
    /// <summary><c>307</c> to <see cref="ShapeResult.NextOffset"/>, the first snapshot page.</summary>
    Redirect,

    /// <summary><c>409 must_refetch</c>: the requested handle is stale; <see cref="ShapeResult.Handle"/> is the current one.</summary>
    MustRefetch,

    /// <summary>A snapshot page or closed log chunk: every copy of it stays valid, so caches keep it.</summary>
    Immutable,

    /// <summary>The open tail of the log: it ends at the head and grows with new commits.</summary>
    OpenTail,
}

/// <summary>A shape read result, shared by every collapsed request.</summary>
/// <param name="Status">What to answer.</param>
/// <param name="ShapeId">32 lowercase hex characters naming the shape.</param>
/// <param name="Handle">Current handle of the shape's log lineage.</param>
/// <param name="NextOffset">Offset to request next.</param>
/// <param name="UpToDate">Whether <paramref name="NextOffset"/> is the head.</param>
/// <param name="Body">Serialized <see cref="ShapeBody"/>; empty unless a page.</param>
/// <param name="Digest">16 lowercase hex characters of SHA-256 over <paramref name="Body"/>; empty unless a page.</param>
public sealed record ShapeResult(
    ShapeStatus Status, string ShapeId, string Handle, string NextOffset, bool UpToDate, ReadOnlyMemory<byte> Body, string Digest);

/// <summary>
/// A shape read result, or why there is none: an unknown database or collection (<c>404</c>), a filter over the engine
/// read budget (<c>400 invalid_query</c>), shape reads at capacity (<c>429 shape_busy</c>) or a core that cannot serve
/// shapes yet (<c>503</c>, <c>core=anna</c>).
/// </summary>
public readonly union ShapeReadResult(ShapeResult, NotFound, Validation, QuotaExceeded, Unavailable);

/// <summary>
/// Reads shapes: a collection and filter served as a snapshot plus a log addressed by offsets
/// (docs/shape-reads.md). Concurrent identical reads share one storage read.
/// </summary>
public interface IShapeService
{
    /// <summary>Reads one snapshot page or log chunk, waiting up to 20 seconds for a commit when live at the head.</summary>
    ValueTask<ShapeReadResult> ReadAsync(ShapeRequest request, CancellationToken cancellationToken);
}
