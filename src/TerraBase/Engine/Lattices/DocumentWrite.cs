namespace TerraBase.Engine.Lattices;

/// <summary>
/// One write of a document, the value its <see cref="MvRegister{T}"/> holds per dot: the hybrid-clock stamp it was
/// written at and its UTF-8 JSON object body, or no body for a delete (a tombstone). Writes with the same stamp and the
/// same bytes are the same write. A write is immutable: registers and their joins share it, so its body is copied in
/// and only read out.
/// </summary>
public sealed class DocumentWrite : IEquatable<DocumentWrite>
{
    private readonly byte[]? _body;

    private DocumentWrite(Hlc stamp, byte[]? body)
    {
        Stamp = stamp;
        _body = body;
    }

    /// <summary>When the write happened.</summary>
    public Hlc Stamp { get; }

    /// <summary>Whether the write deleted the document.</summary>
    public bool IsTombstone => _body is null;

    /// <summary>The written JSON object; empty for a tombstone.</summary>
    public ReadOnlySpan<byte> Body => _body;

    /// <summary>A write of a copy of <paramref name="body"/> at <paramref name="stamp"/>.</summary>
    public static DocumentWrite Live(Hlc stamp, ReadOnlySpan<byte> body) => new(stamp, body.ToArray());

    /// <summary>A delete at <paramref name="stamp"/>.</summary>
    public static DocumentWrite Tombstone(Hlc stamp) => new(stamp, null);

    /// <inheritdoc />
    public bool Equals(DocumentWrite? other) =>
        other is not null && Stamp == other.Stamp && (_body is null ? other._body is null : other._body is not null && _body.AsSpan().SequenceEqual(other._body));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DocumentWrite);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Stamp, _body?.Length ?? -1);

    // The decoder hands over a body it allocated for this write alone.
    internal static DocumentWrite Owning(Hlc stamp, byte[]? body) => new(stamp, body);
}
