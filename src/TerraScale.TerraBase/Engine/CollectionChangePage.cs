namespace TerraBase.Engine;

/// <summary>One retained collection write or tombstone in this database instance's local history order.</summary>
/// <param name="Offset">Local history commit offset, independent of the merge version; the first resume cursor part.</param>
/// <param name="Sequence">Append sequence that orders writes sharing an offset; the second part of the resume cursor.</param>
/// <param name="Document">The stored document or tombstone, preserving its merge version and JSON bytes.</param>
public sealed record CollectionChange(long Offset, long Sequence, StoredDocument Document);

/// <summary>One collection history page, bounded by row count and bytes including per-row framing.</summary>
/// <param name="Changes">Every retained write in local (offset, sequence) order, including repeated writes of one id.</param>
/// <param name="HasMore">Whether another retained row follows the last returned (offset, sequence) cursor.</param>
public sealed record CollectionChangePage(IReadOnlyList<CollectionChange> Changes, bool HasMore);
