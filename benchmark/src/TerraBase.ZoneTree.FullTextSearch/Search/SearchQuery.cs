namespace TerraBase.ZoneTree.FullTextSearch.Search;

/// <summary>
/// A query tree with bounded pagination.
/// </summary>
public sealed class SearchQuery<TToken> : IEquatable<SearchQuery<TToken>>
{
    /// <summary>
    /// Creates a query.
    /// </summary>
    public SearchQuery(QueryNode<TToken> queryNode, int skip = 0, int limit = 0)
    {
        ArgumentNullException.ThrowIfNull(queryNode);
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip));
        }

        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        QueryNode = queryNode;
        Skip = skip;
        Limit = limit;
    }

    /// <summary>Gets the query tree.</summary>
    public QueryNode<TToken> QueryNode { get; }

    /// <summary>Gets the number of matching records to skip.</summary>
    public int Skip { get; }

    /// <summary>Gets the maximum number of records, or zero for no limit.</summary>
    public int Limit { get; }

    /// <summary>Gets whether the query has positive criteria.</summary>
    public bool HasAnyPositiveCriteria => QueryNode.HasAnyPositiveCriteria;

    /// <summary>Gets whether the query tree has no criteria.</summary>
    public bool IsEmpty => QueryNode.IsEmpty;

    /// <inheritdoc />
    public bool Equals(SearchQuery<TToken>? other) => other is not null &&
        Skip == other.Skip && Limit == other.Limit && QueryNode == other.QueryNode;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SearchQuery<TToken> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(QueryNode, Skip, Limit);

    public static bool operator ==(SearchQuery<TToken>? left, SearchQuery<TToken>? right) =>
        EqualityComparer<SearchQuery<TToken>>.Default.Equals(left, right);

    public static bool operator !=(SearchQuery<TToken>? left, SearchQuery<TToken>? right) => !(left == right);
}
