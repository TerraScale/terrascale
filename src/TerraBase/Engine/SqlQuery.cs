using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TerraBase.Engine;

/// <summary>
/// A document field a query or index reads: <c>_id</c> (the document id), <c>_rev</c> (its version) or a
/// dotted path of JSON object member names (<c>email</c>, <c>address.city</c>). Members are ASCII letters,
/// digits and <c>_</c>, start with a letter or <c>_</c>, are at most 64 characters, and a path has at most 8.
/// The grammar keeps every field safe to embed in engine statements.
/// </summary>
public readonly partial record struct DocumentField
{
    private DocumentField(string path)
    {
        Path = path;
    }

    /// <summary>The document id.</summary>
    public static DocumentField Id { get; } = new("_id");

    /// <summary>The document version.</summary>
    public static DocumentField Version { get; } = new("_rev");

    /// <summary>The field as written.</summary>
    public string Path { get; }

    /// <summary>The path's member names; empty for <see cref="Id"/> and <see cref="Version"/>.</summary>
    public IReadOnlyList<string> Members => IsMetadata ? [] : Path.Split('.');

    /// <summary>Whether this is <see cref="Id"/> or <see cref="Version"/>.</summary>
    public bool IsMetadata => Path is "_id" or "_rev";

    /// <summary>Parses a client-supplied field.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out DocumentField field)
    {
        field = value is not null && Pattern().IsMatch(value) ? new DocumentField(value) : default;
        return field.Path is not null;
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,63}(\.[A-Za-z_][A-Za-z0-9_]{0,63}){0,7}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>How a <see cref="SqlCompareFilter"/> compares a field.</summary>
public enum SqlOperator
{
    /// <summary><c>=</c>, one value.</summary>
    Equal,

    /// <summary><c>!=</c>, one value. Like SQL, never matches a missing or null field.</summary>
    NotEqual,

    /// <summary><c>&lt;</c>, one value.</summary>
    Less,

    /// <summary><c>&lt;=</c>, one value.</summary>
    LessOrEqual,

    /// <summary><c>&gt;</c>, one value.</summary>
    Greater,

    /// <summary><c>&gt;=</c>, one value.</summary>
    GreaterOrEqual,

    /// <summary><c>IN (…)</c>, one or more values.</summary>
    In,

    /// <summary><c>IS NULL</c> (null or missing), no value.</summary>
    IsNull,

    /// <summary><c>IS NOT NULL</c>, no value.</summary>
    IsNotNull,

    /// <summary><c>LIKE 'prefix%'</c>: a string starting with the one value, compared case-sensitively.</summary>
    Prefix,
}

/// <summary>
/// A condition of the dashboard SQL subset. Unlike <see cref="DocumentFilter"/> (JSON-typed, query v1) it has SQL
/// semantics over extracted values, so an expression index on the same fields can answer it.
/// </summary>
public abstract record SqlFilter;

/// <summary>Compares a field with literal values: <see cref="string"/>, <see cref="long"/>, <see cref="double"/> or <see cref="bool"/>.</summary>
/// <param name="Field">The compared field.</param>
/// <param name="Operator">The comparison.</param>
/// <param name="Values">Operands; see <see cref="SqlOperator"/> for how many.</param>
public sealed record SqlCompareFilter(DocumentField Field, SqlOperator Operator, IReadOnlyList<object> Values) : SqlFilter;

/// <summary>Every child matches.</summary>
/// <param name="Filters">At least one child.</param>
public sealed record SqlAndFilter(IReadOnlyList<SqlFilter> Filters) : SqlFilter;

/// <summary>Any child matches.</summary>
/// <param name="Filters">At least one child.</param>
public sealed record SqlOrFilter(IReadOnlyList<SqlFilter> Filters) : SqlFilter;

/// <summary>The child does not match.</summary>
/// <param name="Filter">The negated condition.</param>
public sealed record SqlNotFilter(SqlFilter Filter) : SqlFilter;

/// <summary>One sort key.</summary>
/// <param name="Field">Sorted field.</param>
/// <param name="Descending">Whether larger values come first.</param>
public sealed record SqlOrder(DocumentField Field, bool Descending);

/// <summary>
/// Selects live documents of one collection for the dashboard SQL subset. Results are in <paramref name="Order"/>,
/// then id order; at most <paramref name="Limit"/> documents.
/// </summary>
/// <param name="Collection">Collection name (validated like a database name).</param>
/// <param name="Filter">Condition, or <see langword="null"/> for every document.</param>
/// <param name="Order">Sort keys.</param>
/// <param name="Limit">Maximum documents.</param>
public sealed record SqlQuery(string Collection, SqlFilter? Filter, IReadOnlyList<SqlOrder> Order, int Limit);

/// <summary>A collection and its size.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="Documents">Live documents (tombstones are not counted).</param>
/// <param name="Bytes">Total JSON bytes of those documents.</param>
public sealed record CollectionStats(string Name, long Documents, long Bytes);

/// <summary>A secondary index over document fields of one collection.</summary>
/// <param name="Name">Index name: a lowercase letter, then lowercase letters, digits or <c>_</c>, at most 63 characters; unique per database.</param>
/// <param name="Collection">Indexed collection.</param>
/// <param name="Fields">Key fields in order, 1 to 4.</param>
public sealed record IndexDefinition(string Name, string Collection, IReadOnlyList<DocumentField> Fields);

/// <summary>How the engine would run a <see cref="SqlQuery"/>.</summary>
/// <param name="Steps">Engine-specific plan steps, one per line.</param>
/// <param name="Indexes">Names of the secondary indexes (<see cref="IndexDefinition.Name"/>) the plan reads.</param>
public sealed record SqlQueryPlan(IReadOnlyList<string> Steps, IReadOnlyList<string> Indexes);
