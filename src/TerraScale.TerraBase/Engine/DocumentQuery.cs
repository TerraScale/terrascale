namespace TerraBase.Engine;

/// <summary>A path into a document: object member names from the root, or the document id.</summary>
/// <param name="Segments">Member names; <see langword="null"/> for the document id (<c>$id</c>).</param>
public sealed record DocumentPath(IReadOnlyList<string>? Segments)
{
    /// <summary>The document id pseudo-path.</summary>
    public static DocumentPath Id { get; } = new((IReadOnlyList<string>?)null);

    /// <summary>Whether this is the document id.</summary>
    public bool IsId => Segments is null;
}

/// <summary>The JSON type of a scalar query value.</summary>
public enum ScalarKind
{
    /// <summary>JSON <c>null</c>.</summary>
    Null,

    /// <summary>JSON <c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary>A JSON number.</summary>
    Number,

    /// <summary>A JSON string.</summary>
    Text,
}

/// <summary>A JSON scalar compared against document values.</summary>
/// <param name="Kind">JSON type.</param>
/// <param name="Value">A <see cref="bool"/>, a <see cref="long"/> or <see cref="double"/>, a <see cref="string"/>, or <see langword="null"/>.</param>
public sealed record ScalarValue(ScalarKind Kind, object? Value);

/// <summary>Comparison operators.</summary>
public enum Comparison
{
    /// <summary>Equal.</summary>
    Equal,

    /// <summary>Less than.</summary>
    Less,

    /// <summary>Less than or equal.</summary>
    LessOrEqual,

    /// <summary>Greater than.</summary>
    Greater,

    /// <summary>Greater than or equal.</summary>
    GreaterOrEqual,
}

/// <summary>A predicate over one document (query v1 filter grammar).</summary>
public abstract record DocumentFilter;

/// <summary>Every child matches.</summary>
/// <param name="Children">At least one child.</param>
public sealed record AndFilter(IReadOnlyList<DocumentFilter> Children) : DocumentFilter;

/// <summary>Any child matches.</summary>
/// <param name="Children">At least one child.</param>
public sealed record OrFilter(IReadOnlyList<DocumentFilter> Children) : DocumentFilter;

/// <summary>The child does not match.</summary>
/// <param name="Child">Negated filter.</param>
public sealed record NotFilter(DocumentFilter Child) : DocumentFilter;

/// <summary>
/// The value at <paramref name="Path"/> has the JSON type of <paramref name="Value"/> and compares
/// to it as <paramref name="Op"/>. Arrays, objects and other types never match.
/// </summary>
/// <param name="Path">Compared value.</param>
/// <param name="Op">Operator; ordered operators never take <see cref="ScalarKind.Null"/>.</param>
/// <param name="Value">Scalar to compare with.</param>
public sealed record CompareFilter(DocumentPath Path, Comparison Op, ScalarValue Value) : DocumentFilter;

/// <summary>The value at <paramref name="Path"/> equals one of <paramref name="Values"/> (same JSON type and value).</summary>
/// <param name="Path">Compared value.</param>
/// <param name="Values">At least one scalar.</param>
public sealed record InFilter(DocumentPath Path, IReadOnlyList<ScalarValue> Values) : DocumentFilter;

/// <summary>The member at <paramref name="Path"/> is present, including JSON <c>null</c>.</summary>
/// <param name="Path">Tested member.</param>
public sealed record ExistsFilter(DocumentPath Path) : DocumentFilter;

/// <summary>The value at <paramref name="Path"/> is a string starting with <paramref name="Prefix"/>.</summary>
/// <param name="Path">Tested value.</param>
/// <param name="Prefix">Required prefix.</param>
public sealed record PrefixFilter(DocumentPath Path, string Prefix) : DocumentFilter;

/// <summary>The value at <paramref name="Path"/> is an array with an element equal to <paramref name="Value"/>.</summary>
/// <param name="Path">Tested array.</param>
/// <param name="Value">Scalar element.</param>
public sealed record ContainsFilter(DocumentPath Path, ScalarValue Value) : DocumentFilter;

/// <summary>One sort key.</summary>
/// <param name="Path">Sorted value.</param>
/// <param name="Descending">Whether the order is descending.</param>
public sealed record SortKey(DocumentPath Path, bool Descending);

/// <summary>
/// A keyset position: the sort values that <see cref="QueryRow.Keys"/> reported for the last row of a
/// page, and that row's id. The next page starts strictly after it.
/// </summary>
/// <param name="Keys">Opaque sort values (<see langword="null"/>, <see cref="long"/>, <see cref="double"/> or <see cref="string"/>).</param>
/// <param name="Id">Document id of the last row.</param>
public sealed record QueryPosition(IReadOnlyList<object?> Keys, string Id);

/// <summary>
/// A query over the live documents of one collection, ordered by <paramref name="Sort"/> and then by id.
/// A page holds at most <paramref name="Limit"/> documents and, beyond the first, at most
/// <paramref name="MaxBytes"/> of JSON.
/// </summary>
/// <param name="Collection">Queried collection.</param>
/// <param name="Filter">Predicate; <see langword="null"/> matches every document.</param>
/// <param name="Sort">Sort keys before the final id order.</param>
/// <param name="After">Position to continue after; <see langword="null"/> for the first page.</param>
/// <param name="Limit">Most documents per page.</param>
/// <param name="MaxBytes">Most JSON bytes per page beyond the first document.</param>
public sealed record DocumentQuery(
    string Collection, DocumentFilter? Filter, IReadOnlyList<SortKey> Sort, QueryPosition? After, int Limit, long MaxBytes);

/// <summary>One matching document with its sort values.</summary>
/// <param name="Document">The live document.</param>
/// <param name="Keys">Sort values to build a <see cref="QueryPosition"/> from.</param>
public sealed record QueryRow(StoredDocument Document, IReadOnlyList<object?> Keys);

/// <summary>One page of query results.</summary>
/// <param name="Rows">Matching documents in order.</param>
/// <param name="HasMore">Whether more matching documents follow.</param>
public sealed record QueryPage(IReadOnlyList<QueryRow> Rows, bool HasMore);
