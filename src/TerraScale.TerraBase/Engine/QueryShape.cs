using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Hosting;

namespace TerraBase.Engine;

/// <summary>Literal-free, bounded workload identity; never a result-cache key.</summary>
public static class QueryShape
{
    // Reject oversized paths before allocating the canonical tree. JSON escaping can use six characters per code unit.
    private const int MaxWeight = 1024;

    /// <summary>Normalizes JSON predicates and effective sorting, or declines an oversized shape.</summary>
    public static string? Normalize(DocumentQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Weight(query.Filter) + query.Sort.Sum(key => Weight(key.Path)) > MaxWeight ? null
            : Serialize("json", Filter(query.Filter), Sort(query.Sort));
    }

    /// <summary>Normalizes SQL predicates and effective sorting, separately from JSON comparison semantics.</summary>
    public static string? Normalize(SqlQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Weight(query.Filter) + query.Order.Sum(key => key.Field.Path.Length + 16) > MaxWeight ? null
            : Serialize("sql", Filter(query.Filter), Order(query.Order));
    }

    private static string Serialize(string language, JsonNode? filter, JsonArray sort) =>
        JsonSerializer.Serialize(new JsonArray("alpha", language, filter, sort), TerraBaseJsonContext.Default.JsonArray);

    private static JsonNode? Filter(DocumentFilter? filter) => filter switch
    {
        null => null,
        AndFilter and => Group("and", Flatten(and, true).Select(Filter)),
        OrFilter or => Group("or", Flatten(or, false).Select(Filter)),
        NotFilter not => new JsonArray("not", Filter(not.Child)),
        CompareFilter compare => new JsonArray(compare.Op.ToString(), Path(compare.Path), compare.Value.Kind.ToString()),
        InFilter inside => new JsonArray("in", Path(inside.Path), Types(inside.Values.Select(value => value.Kind.ToString()))),
        ExistsFilter exists => new JsonArray("exists", Path(exists.Path)),
        PrefixFilter prefix => new JsonArray("prefix", Path(prefix.Path), "Text"),
        ContainsFilter contains => new JsonArray("contains", Path(contains.Path), contains.Value.Kind.ToString()),
        _ => throw new InvalidOperationException("unknown document filter"),
    };

    private static JsonNode? Filter(SqlFilter? filter) => filter switch
    {
        null => null,
        SqlAndFilter and => Group("and", Flatten(and, true).Select(Filter)),
        SqlOrFilter or => Group("or", Flatten(or, false).Select(Filter)),
        SqlNotFilter not => new JsonArray("not", Filter(not.Filter)),
        SqlCompareFilter compare => new JsonArray(compare.Operator.ToString(), compare.Field.Path, Types(compare.Values.Select(Type))),
        _ => throw new InvalidOperationException("unknown SQL filter"),
    };

    private static IEnumerable<DocumentFilter> Flatten(DocumentFilter filter, bool and) => filter switch
    {
        AndFilter group when and => group.Children.SelectMany(child => Flatten(child, and)),
        OrFilter group when !and => group.Children.SelectMany(child => Flatten(child, and)),
        _ => [filter],
    };

    private static IEnumerable<SqlFilter> Flatten(SqlFilter filter, bool and) => filter switch
    {
        SqlAndFilter group when and => group.Filters.SelectMany(child => Flatten(child, and)),
        SqlOrFilter group when !and => group.Filters.SelectMany(child => Flatten(child, and)),
        _ => [filter],
    };

    private static JsonNode? Group(string op, IEnumerable<JsonNode?> children)
    {
        var ordered = children.OrderBy(child => JsonSerializer.Serialize(child, TerraBaseJsonContext.Default.JsonNode), StringComparer.Ordinal).ToArray();
        return ordered.Length == 1 ? ordered[0] : new JsonArray(op, new JsonArray(ordered));
    }

    private static JsonArray Types(IEnumerable<string> kinds) =>
        [.. kinds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(kind => (JsonNode?)JsonValue.Create(kind))];

    private static string Type(object value) => value switch
    {
        bool => "Boolean",
        string => "Text",
        long or double => "Number",
        _ => throw new InvalidOperationException("unknown SQL scalar"),
    };

    private static JsonArray? Path(DocumentPath path) => path.IsId ? null
        : [.. path.Segments!.Select(segment => (JsonNode?)JsonValue.Create(segment))];

    private static JsonArray Sort(IReadOnlyList<SortKey> keys)
    {
        var sort = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var path = Path(key.Path);
            if (seen.Add(JsonSerializer.Serialize(path, TerraBaseJsonContext.Default.JsonNode)))
            {
                sort.Add(new JsonArray(path, key.Descending));
                if (key.Path.IsId)
                {
                    return sort;
                }
            }
        }

        sort.Add(new JsonArray(null, false));
        return sort;
    }

    private static JsonArray Order(IReadOnlyList<SqlOrder> keys)
    {
        var sort = new JsonArray();
        var seen = new HashSet<DocumentField>();
        foreach (var key in keys)
        {
            if (seen.Add(key.Field))
            {
                sort.Add(new JsonArray(key.Field.Path, key.Descending));
                if (key.Field == DocumentField.Id)
                {
                    return sort;
                }
            }
        }

        sort.Add(new JsonArray(DocumentField.Id.Path, keys is [{ Descending: true }, ..]));
        return sort;
    }

    private static int Weight(DocumentPath path) => 16 + (path.Segments?.Sum(segment => segment.Length + 4) ?? 0);

    private static int Weight(DocumentFilter? filter) => 32 + (filter switch
    {
        AndFilter and => and.Children.Sum(Weight),
        OrFilter or => or.Children.Sum(Weight),
        NotFilter not => Weight(not.Child),
        CompareFilter compare => Weight(compare.Path),
        InFilter inside => Weight(inside.Path),
        ExistsFilter exists => Weight(exists.Path),
        PrefixFilter prefix => Weight(prefix.Path),
        ContainsFilter contains => Weight(contains.Path),
        _ => 0,
    });

    private static int Weight(SqlFilter? filter) => 32 + (filter switch
    {
        SqlAndFilter and => and.Filters.Sum(Weight),
        SqlOrFilter or => or.Filters.Sum(Weight),
        SqlNotFilter not => Weight(not.Filter),
        SqlCompareFilter compare => compare.Field.Path.Length,
        _ => 0,
    });
}
