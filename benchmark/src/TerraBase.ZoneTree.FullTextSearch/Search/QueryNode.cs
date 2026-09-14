namespace TerraBase.ZoneTree.FullTextSearch.Search;

/// <summary>
/// A provider-independent boolean query tree.
/// </summary>
public sealed class QueryNode<TToken> : IEquatable<QueryNode<TToken>>
{
    /// <summary>
    /// Creates a query node.
    /// </summary>
    public QueryNode(
        QueryNodeType nodeType,
        TToken[]? tokens = null,
        QueryNode<TToken>[]? children = null,
        bool respectTokenOrder = true,
        bool isFacetNode = false)
    {
        NodeType = nodeType;
        Tokens = tokens;
        Children = children;
        RespectTokenOrder = respectTokenOrder;
        IsFacetNode = isFacetNode;
    }

    /// <summary>Gets or sets the node's logical operation.</summary>
    public QueryNodeType NodeType { get; set; }

    /// <summary>Gets or sets the node's token groups.</summary>
    public TToken[]? Tokens { get; set; }

    /// <summary>Gets or sets the node's child expressions.</summary>
    public QueryNode<TToken>[]? Children { get; set; }

    /// <summary>Gets or sets whether token groups must be contiguous and ordered.</summary>
    public bool RespectTokenOrder { get; set; }

    /// <summary>Gets or sets whether node tokens represent facet values.</summary>
    public bool IsFacetNode { get; set; }

    /// <summary>Gets whether this node contains token groups.</summary>
    public bool HasTokens => Tokens is { Length: > 0 };

    /// <summary>Gets whether this node contains child expressions.</summary>
    public bool HasChildren => Children is { Length: > 0 };

    /// <summary>Gets whether this node contains no criteria.</summary>
    public bool IsEmpty => !HasTokens && !HasChildren;

    /// <summary>Gets whether this tree has a positive search criterion.</summary>
    public bool HasAnyPositiveCriteria => NodeType != QueryNodeType.Not &&
        (HasTokens || (Children?.Any(child => child.HasAnyPositiveCriteria) ?? false));

    /// <inheritdoc />
    public bool Equals(QueryNode<TToken>? other)
    {
        return other is not null &&
               NodeType == other.NodeType &&
               RespectTokenOrder == other.RespectTokenOrder &&
               IsFacetNode == other.IsFacetNode &&
               (Tokens ?? []).SequenceEqual(other.Tokens ?? []) &&
               (Children ?? []).SequenceEqual(other.Children ?? []);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is QueryNode<TToken> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(NodeType);
        hash.Add(RespectTokenOrder);
        hash.Add(IsFacetNode);
        foreach (var token in Tokens ?? [])
        {
            hash.Add(token);
        }

        foreach (var child in Children ?? [])
        {
            hash.Add(child);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(QueryNode<TToken>? left, QueryNode<TToken>? right) =>
        EqualityComparer<QueryNode<TToken>>.Default.Equals(left, right);

    public static bool operator !=(QueryNode<TToken>? left, QueryNode<TToken>? right) => !(left == right);
}
