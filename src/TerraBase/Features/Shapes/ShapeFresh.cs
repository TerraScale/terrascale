using TerraBase.Features.Memory;

namespace TerraBase.Features.Shapes;

/// <summary>
/// The sealed head and fresh bodies (shape-reads section 4), read in one transaction. Engine-implemented beside
/// <see cref="IShapeStorageTransaction"/>, which keeps the sealing contract unchanged.
/// </summary>
internal interface IShapeFreshStorage
{
    /// <summary>The sealed head: the largest commit boundary at or below the feed head.</summary>
    ValueTask<long> ReadShapeHeadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A fresh body at <paramref name="offset"/>: no changes at the head, or the sealed chunk's changes re-framed at its end
    /// with a fresh cursor and <c>up_to_date</c> only when that end is the head. With <paramref name="remote"/> the head is
    /// the remote durable frontier read in the same transaction (FR-25). Null when the offset is above the head, its chunk is
    /// not sealed, or the chunk's end or values_at is above the head. The caller owns the returned lease.
    /// </summary>
    ValueTask<ShapeBody?> ReadShapeFreshAsync(ShapeSealDefinition shape, long offset, bool remote, IMemoryBudget memory, CancellationToken cancellationToken);
}
