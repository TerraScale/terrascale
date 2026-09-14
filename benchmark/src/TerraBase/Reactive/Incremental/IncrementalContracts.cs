using System.Text.Json;

namespace TerraBase.Reactive.Incremental;

/// <summary>The maintenance strategy selected for a view plan.</summary>
public enum IncrementalMaintenanceMode
{
    /// <summary>The plan has a delta maintenance implementation.</summary>
    Incremental,

    /// <summary>The plan is rebuilt from the transaction's new source state.</summary>
    Fallback,
}

/// <summary>The outcome of applying one committed transaction to a view.</summary>
public enum IncrementalApplyStatus
{
    /// <summary>The transaction was applied and the watermark advanced.</summary>
    Applied,

    /// <summary>The transaction has the current watermark and was already applied.</summary>
    Duplicate,

    /// <summary>The transaction is older than the current view watermark.</summary>
    Stale,

    /// <summary>A before image did not match the view's source state.</summary>
    Conflict,
}

/// <summary>A committed source position used by an incremental view.</summary>
public readonly record struct IncrementalWatermark : IComparable<IncrementalWatermark>
{
    /// <summary>Creates a source position.</summary>
    /// <param name="value">The monotonically increasing transaction position.</param>
    public IncrementalWatermark(ulong value) => Value = value;

    /// <summary>The monotonic transaction position.</summary>
    public ulong Value { get; }

    /// <summary>The initial source position.</summary>
    public static IncrementalWatermark Initial => new(0);

    /// <inheritdoc />
    public int CompareTo(IncrementalWatermark other) => Value.CompareTo(other.Value);

    /// <summary>Converts a numeric sequence to a source position.</summary>
    public static implicit operator IncrementalWatermark(ulong value) => new(value);

    /// <summary>Converts a source position to its numeric sequence.</summary>
    public static implicit operator ulong(IncrementalWatermark watermark) => watermark.Value;
}

/// <summary>A source document before and after one committed mutation.</summary>
public sealed record IncrementalDocumentChange
{
    private IncrementalDocumentChange(DocumentKey key, Document? before, Document? after)
    {
        if (before is null && after is null)
        {
            throw new ArgumentException("A document change must have a before or after image.");
        }

        if (before is not null && before.Key != key)
        {
            throw new ArgumentException("The before image key does not match the change key.", nameof(before));
        }

        if (after is not null && after.Key != key)
        {
            throw new ArgumentException("The after image key does not match the change key.", nameof(after));
        }

        Key = key;
        Before = before;
        After = after;
    }

    /// <summary>Creates a create or update change.</summary>
    /// <param name="after">The committed document.</param>
    /// <param name="before">The document replaced by the commit, if any.</param>
    public static IncrementalDocumentChange Upsert(Document after, Document? before = null)
    {
        ArgumentNullException.ThrowIfNull(after);
        return new(after.Key, before, after);
    }

    /// <summary>Creates a delete change.</summary>
    /// <param name="before">The document deleted by the commit.</param>
    public static IncrementalDocumentChange Delete(Document before)
    {
        ArgumentNullException.ThrowIfNull(before);
        return new(before.Key, before, null);
    }

    /// <summary>The key affected by the mutation.</summary>
    public DocumentKey Key { get; }

    /// <summary>The committed before image, or <see langword="null"/> for a create.</summary>
    public Document? Before { get; }

    /// <summary>The committed after image, or <see langword="null"/> for a delete.</summary>
    public Document? After { get; }
}

/// <summary>A set of source mutations committed at one transaction watermark.</summary>
public sealed class IncrementalTransaction
{
    /// <summary>Creates a transaction envelope.</summary>
    /// <param name="watermark">The source position assigned to the transaction.</param>
    /// <param name="changes">The complete atomic mutation set.</param>
    public IncrementalTransaction(IncrementalWatermark watermark, IEnumerable<IncrementalDocumentChange> changes)
    {
        if (watermark.Value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(watermark), "Committed transaction watermarks start at one.");
        }

        ArgumentNullException.ThrowIfNull(changes);
        Changes = changes.ToArray();
        Watermark = watermark;
    }

    /// <summary>Creates a transaction envelope from a numeric sequence.</summary>
    public IncrementalTransaction(ulong watermark, IEnumerable<IncrementalDocumentChange> changes)
        : this(new IncrementalWatermark(watermark), changes)
    {
    }

    /// <summary>The source position assigned to the transaction.</summary>
    public IncrementalWatermark Watermark { get; }

    /// <summary>The complete, atomically committed mutation set.</summary>
    public IReadOnlyList<IncrementalDocumentChange> Changes { get; }
}

/// <summary>One materialized row in a reactive view.</summary>
public sealed record IncrementalViewRow
{
    /// <summary>Creates a materialized row.</summary>
    /// <param name="identity">A stable identity for update and removal matching.</param>
    /// <param name="value">The projected JSON value.</param>
    public IncrementalViewRow(string identity, JsonElement value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A view row must contain valid JSON.", nameof(value));
        }

        Identity = identity;
        Value = value.Clone();
    }

    /// <summary>The stable row identity.</summary>
    public string Identity { get; }

    /// <summary>The immutable projected JSON value.</summary>
    public JsonElement Value { get; }
}

/// <summary>The net changes emitted by one view maintenance step.</summary>
public sealed record IncrementalViewDelta
{
    internal IncrementalViewDelta(
        IncrementalApplyStatus status,
        IncrementalWatermark watermark,
        IReadOnlyList<IncrementalViewRow> added,
        IReadOnlyList<IncrementalViewRow> removed,
        bool reset,
        IncrementalMaintenanceMode mode)
    {
        Status = status;
        Watermark = watermark;
        Added = added;
        Removed = removed;
        IsReset = reset;
        Mode = mode;
    }

    /// <summary>The transaction application outcome.</summary>
    public IncrementalApplyStatus Status { get; }

    /// <summary>The view watermark after the operation.</summary>
    public IncrementalWatermark Watermark { get; }

    /// <summary>Rows added by the operation.</summary>
    public IReadOnlyList<IncrementalViewRow> Added { get; }

    /// <summary>Rows removed by the operation.</summary>
    public IReadOnlyList<IncrementalViewRow> Removed { get; }

    /// <summary>Whether the rows represent a full replacement of the view.</summary>
    public bool IsReset { get; }

    /// <summary>The strategy used to maintain the view.</summary>
    public IncrementalMaintenanceMode Mode { get; }

    /// <summary>Whether the transaction changed the view.</summary>
    public bool HasChanges => Added.Count != 0 || Removed.Count != 0 || IsReset;
}

/// <summary>A stable snapshot of a maintained view.</summary>
public sealed record IncrementalViewSnapshot
{
    internal IncrementalViewSnapshot(
        IncrementalWatermark watermark,
        IReadOnlyList<IncrementalViewRow> rows,
        IncrementalMaintenanceMode mode)
    {
        Watermark = watermark;
        Rows = rows;
        Mode = mode;
    }

    /// <summary>The source position represented by this snapshot.</summary>
    public IncrementalWatermark Watermark { get; }

    /// <summary>The materialized rows in stable identity order.</summary>
    public IReadOnlyList<IncrementalViewRow> Rows { get; }

    /// <summary>The plan maintenance strategy.</summary>
    public IncrementalMaintenanceMode Mode { get; }
}

/// <summary>The result of a transaction maintenance attempt.</summary>
public sealed record IncrementalApplyResult
{
    internal IncrementalApplyResult(IncrementalViewDelta delta)
    {
        Delta = delta;
    }

    /// <summary>The emitted view delta.</summary>
    public IncrementalViewDelta Delta { get; }

    /// <summary>The transaction application outcome.</summary>
    public IncrementalApplyStatus Status => Delta.Status;

    /// <summary>The resulting view watermark.</summary>
    public IncrementalWatermark Watermark => Delta.Watermark;
}
