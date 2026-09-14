using System.Globalization;
using System.Text.Json;

namespace TerraBase.Reactive.Incremental;

/// <summary>The standard aggregate maintained by an incremental plan.</summary>
public enum IncrementalAggregateKind
{
    /// <summary>Number of documents in each group.</summary>
    Count,

    /// <summary>Sum of a numeric document field in each group.</summary>
    Sum,

    /// <summary>Minimum numeric document field in each group.</summary>
    Min,

    /// <summary>Maximum numeric document field in each group.</summary>
    Max,
}

/// <summary>Defines one supported grouped aggregate.</summary>
public sealed record IncrementalAggregateDefinition
{
    private IncrementalAggregateDefinition(
        IncrementalAggregateKind kind,
        Func<Document, string?> groupSelector,
        Func<Document, decimal?>? valueSelector)
    {
        Kind = kind;
        GroupSelector = groupSelector;
        ValueSelector = valueSelector;
    }

    /// <summary>Creates a grouped count.</summary>
    public static IncrementalAggregateDefinition Count(Func<Document, string?> groupSelector) =>
        new(IncrementalAggregateKind.Count, groupSelector ?? throw new ArgumentNullException(nameof(groupSelector)), null);

    /// <summary>Creates a grouped sum.</summary>
    public static IncrementalAggregateDefinition Sum(
        Func<Document, string?> groupSelector,
        Func<Document, decimal?> valueSelector) =>
        new(
            IncrementalAggregateKind.Sum,
            groupSelector ?? throw new ArgumentNullException(nameof(groupSelector)),
            valueSelector ?? throw new ArgumentNullException(nameof(valueSelector)));

    /// <summary>Creates a grouped minimum.</summary>
    public static IncrementalAggregateDefinition Min(
        Func<Document, string?> groupSelector,
        Func<Document, decimal?> valueSelector) =>
        new(
            IncrementalAggregateKind.Min,
            groupSelector ?? throw new ArgumentNullException(nameof(groupSelector)),
            valueSelector ?? throw new ArgumentNullException(nameof(valueSelector)));

    /// <summary>Creates a grouped maximum.</summary>
    public static IncrementalAggregateDefinition Max(
        Func<Document, string?> groupSelector,
        Func<Document, decimal?> valueSelector) =>
        new(
            IncrementalAggregateKind.Max,
            groupSelector ?? throw new ArgumentNullException(nameof(groupSelector)),
            valueSelector ?? throw new ArgumentNullException(nameof(valueSelector)));

    /// <summary>The aggregate operation.</summary>
    public IncrementalAggregateKind Kind { get; }

    /// <summary>Extracts the group identity; null excludes a document.</summary>
    public Func<Document, string?> GroupSelector { get; }

    /// <summary>Extracts a value for numeric aggregates.</summary>
    public Func<Document, decimal?>? ValueSelector { get; }
}

/// <summary>A query shape that can be maintained from source document changes.</summary>
public sealed class IncrementalViewPlan
{
    private enum PlanKind
    {
        Filter,
        Join,
        Aggregate,
        Fallback,
    }

    private readonly PlanKind kind;
    private readonly Func<Document, bool>? filter;
    private readonly Func<Document, JsonElement>? projection;
    private readonly Func<Document, string?>? leftKey;
    private readonly Func<Document, string?>? rightKey;
    private readonly Func<Document, Document, JsonElement>? joinProjection;
    private readonly IncrementalAggregateDefinition? aggregate;
    private readonly Func<IReadOnlyDictionary<DocumentKey, Document>, IEnumerable<IncrementalViewRow>>? fallback;

    private IncrementalViewPlan(
        PlanKind kind,
        IEnumerable<string> collections,
        Func<Document, bool>? filter = null,
        Func<Document, JsonElement>? projection = null,
        Func<Document, string?>? leftKey = null,
        Func<Document, string?>? rightKey = null,
        Func<Document, Document, JsonElement>? joinProjection = null,
        IncrementalAggregateDefinition? aggregate = null,
        string? fallbackReason = null,
        Func<IReadOnlyDictionary<DocumentKey, Document>, IEnumerable<IncrementalViewRow>>? fallback = null)
    {
        this.kind = kind;
        Collections = collections.Distinct(StringComparer.Ordinal).ToArray();
        if (Collections.Count == 0)
        {
            throw new ArgumentException("A view plan must name at least one source collection.", nameof(collections));
        }

        this.filter = filter;
        this.projection = projection;
        this.leftKey = leftKey;
        this.rightKey = rightKey;
        this.joinProjection = joinProjection;
        this.aggregate = aggregate;
        FallbackReason = fallbackReason;
        this.fallback = fallback;
    }

    /// <summary>Creates a filter and optional projection plan.</summary>
    public static IncrementalViewPlan Filter(
        string collection,
        Func<Document, bool> predicate,
        Func<Document, JsonElement>? projection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentNullException.ThrowIfNull(predicate);
        return new(PlanKind.Filter, [collection], predicate, projection);
    }

    /// <summary>Creates a filter using a collection identifier.</summary>
    public static IncrementalViewPlan Filter(
        CollectionId collection,
        Func<Document, bool> predicate,
        Func<Document, JsonElement>? projection = null) =>
        Filter(collection.Value, predicate, projection);

    /// <summary>Creates an inner join over two collections.</summary>
    public static IncrementalViewPlan Join(
        string leftCollection,
        string rightCollection,
        Func<Document, string?> leftKey,
        Func<Document, string?> rightKey,
        Func<Document, Document, JsonElement>? projection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftCollection);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightCollection);
        ArgumentNullException.ThrowIfNull(leftKey);
        ArgumentNullException.ThrowIfNull(rightKey);
        return new(
            PlanKind.Join,
            [leftCollection, rightCollection],
            leftKey: leftKey,
            rightKey: rightKey,
            joinProjection: projection);
    }

    /// <summary>Creates a grouped aggregate plan.</summary>
    public static IncrementalViewPlan Aggregate(string collection, IncrementalAggregateDefinition aggregate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentNullException.ThrowIfNull(aggregate);
        return new(PlanKind.Aggregate, [collection], aggregate: aggregate);
    }

    /// <summary>Creates a grouped count plan.</summary>
    public static IncrementalViewPlan Count(string collection, Func<Document, string?> groupSelector) =>
        Aggregate(collection, IncrementalAggregateDefinition.Count(groupSelector));

    /// <summary>Creates a grouped sum plan.</summary>
    public static IncrementalViewPlan Sum(
        string collection,
        Func<Document, string?> groupSelector,
        Func<Document, decimal?> valueSelector) =>
        Aggregate(collection, IncrementalAggregateDefinition.Sum(groupSelector, valueSelector));

    /// <summary>Creates a plan maintained through a complete rebuild.</summary>
    /// <param name="collections">The collections read by the evaluator.</param>
    /// <param name="reason">The unsupported operator or reason for fallback.</param>
    /// <param name="evaluator">The full evaluator used after each commit.</param>
    public static IncrementalViewPlan Fallback(
        IEnumerable<string> collections,
        string reason,
        Func<IReadOnlyDictionary<DocumentKey, Document>, IEnumerable<IncrementalViewRow>> evaluator)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(evaluator);
        return new(PlanKind.Fallback, collections, fallbackReason: reason, fallback: evaluator);
    }

    /// <summary>The source collections read by this plan.</summary>
    public IReadOnlyList<string> Collections { get; }

    /// <summary>The selected maintenance strategy.</summary>
    public IncrementalMaintenanceMode Mode => kind == PlanKind.Fallback
        ? IncrementalMaintenanceMode.Fallback
        : IncrementalMaintenanceMode.Incremental;

    /// <summary>The reason a plan uses fallback maintenance, if any.</summary>
    public string? FallbackReason { get; }

    internal IReadOnlyDictionary<string, Document> Evaluate(
        IReadOnlyDictionary<DocumentKey, Document> documents,
        IReadOnlyDictionary<string, Document> oldDocuments,
        IReadOnlyDictionary<string, Document> newDocuments,
        IReadOnlyList<IncrementalDocumentChange> changes,
        IReadOnlyDictionary<string, IncrementalViewRow> currentRows)
    {
        var rows = kind switch
        {
            PlanKind.Filter => ApplyFilter(oldDocuments, newDocuments, changes, currentRows),
            PlanKind.Join => ApplyJoin(oldDocuments, newDocuments, changes, currentRows),
            PlanKind.Aggregate => ApplyAggregate(oldDocuments, newDocuments, changes, currentRows),
            PlanKind.Fallback => EvaluateFallback(documents),
            _ => throw new InvalidOperationException("Unknown incremental plan kind."),
        };

        return rows.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    internal IReadOnlyDictionary<string, Document> SourceRows(
        IReadOnlyDictionary<DocumentKey, Document> documents) =>
        documents
            .Where(pair => Collections.Contains(pair.Key.Collection.Value, StringComparer.Ordinal))
            .ToDictionary(pair => SourceIdentity(pair.Key), pair => pair.Value, StringComparer.Ordinal);

    private Dictionary<string, IncrementalViewRow> ApplyFilter(
        IReadOnlyDictionary<string, Document> oldDocuments,
        IReadOnlyDictionary<string, Document> newDocuments,
        IReadOnlyList<IncrementalDocumentChange> changes,
        IReadOnlyDictionary<string, IncrementalViewRow> currentRows)
    {
        var rows = new Dictionary<string, IncrementalViewRow>(currentRows, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!string.Equals(change.Key.Collection.Value, Collections[0], StringComparison.Ordinal))
            {
                continue;
            }

            var identity = SourceIdentity(change.Key);
            rows.Remove(identity);
            if (newDocuments.TryGetValue(identity, out var document) && filter!(document))
            {
                rows[identity] = Project(identity, document);
            }
        }

        return rows;
    }

    private Dictionary<string, IncrementalViewRow> ApplyJoin(
        IReadOnlyDictionary<string, Document> oldDocuments,
        IReadOnlyDictionary<string, Document> newDocuments,
        IReadOnlyList<IncrementalDocumentChange> changes,
        IReadOnlyDictionary<string, IncrementalViewRow> currentRows)
    {
        var leftCollection = Collections[0];
        var rightCollection = Collections[1];
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (string.Equals(change.Key.Collection.Value, leftCollection, StringComparison.Ordinal))
            {
                AddJoinIdentities(affected, change.Before, oldDocuments, rightCollection, true);
                AddJoinIdentities(affected, change.After, newDocuments, rightCollection, true);
            }

            if (string.Equals(change.Key.Collection.Value, rightCollection, StringComparison.Ordinal))
            {
                AddJoinIdentities(affected, change.Before, oldDocuments, leftCollection, false);
                AddJoinIdentities(affected, change.After, newDocuments, leftCollection, false);
            }
        }

        var rows = new Dictionary<string, IncrementalViewRow>(currentRows, StringComparer.Ordinal);
        foreach (var identity in affected)
        {
            rows.Remove(identity);
        }

        var leftDocuments = newDocuments.Values
            .Where(document => string.Equals(document.Key.Collection.Value, leftCollection, StringComparison.Ordinal))
            .ToArray();
        var rightDocuments = newDocuments.Values
            .Where(document => string.Equals(document.Key.Collection.Value, rightCollection, StringComparison.Ordinal))
            .ToArray();
        foreach (var left in leftDocuments)
        {
            foreach (var right in rightDocuments)
            {
                if (left.Key.Tenant != right.Key.Tenant || !string.Equals(leftKey!(left), rightKey!(right), StringComparison.Ordinal))
                {
                    continue;
                }

                var identity = JoinIdentity(left.Key, right.Key);
                if (!affected.Contains(identity))
                {
                    continue;
                }

                var value = joinProjection is null ? JoinValue(left, right) : joinProjection(left, right).Clone();
                rows[identity] = new IncrementalViewRow(identity, value);
            }
        }

        return rows;
    }

    private Dictionary<string, IncrementalViewRow> ApplyAggregate(
        IReadOnlyDictionary<string, Document> oldDocuments,
        IReadOnlyDictionary<string, Document> newDocuments,
        IReadOnlyList<IncrementalDocumentChange> changes,
        IReadOnlyDictionary<string, IncrementalViewRow> currentRows)
    {
        var collection = Collections[0];
        var affectedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!string.Equals(change.Key.Collection.Value, collection, StringComparison.Ordinal))
            {
                continue;
            }

            AddGroup(affectedGroups, change.Before);
            AddGroup(affectedGroups, change.After);
        }

        var rows = new Dictionary<string, IncrementalViewRow>(currentRows, StringComparer.Ordinal);
        foreach (var group in affectedGroups)
        {
            rows.Remove(group);
        }

        var grouped = newDocuments.Values
            .Where(document => string.Equals(document.Key.Collection.Value, collection, StringComparison.Ordinal))
            .Select(document => (Document: document, Group: aggregate!.GroupSelector(document)))
            .Where(item => item.Group is not null)
            .GroupBy(item => GroupIdentity(item.Document.Key.Tenant, item.Group!), StringComparer.Ordinal);
        foreach (var group in grouped)
        {
            if (!affectedGroups.Contains(group.Key))
            {
                continue;
            }

            var documents = group.Select(item => item.Document).ToArray();
            var value = AggregateValue(group.Key, documents);
            rows[group.Key] = new IncrementalViewRow(group.Key, value);
        }

        return rows;

        void AddGroup(HashSet<string> groups, Document? document)
        {
            if (document is null || !string.Equals(document.Key.Collection.Value, collection, StringComparison.Ordinal))
            {
                return;
            }

            var group = aggregate!.GroupSelector(document);
            if (group is not null)
            {
                groups.Add(GroupIdentity(document.Key.Tenant, group));
            }
        }
    }

    private IReadOnlyDictionary<string, Document> EvaluateFallback(
        IReadOnlyDictionary<DocumentKey, Document> documents)
    {
        return fallback!(documents)
            .Select(row => new KeyValuePair<string, Document>(row.Identity, new Document(
                new DocumentKey(new TenantId("fallback"), new CollectionId("rows"), row.Identity),
                row.Value,
                1,
                DateTimeOffset.UnixEpoch)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private IncrementalViewRow Project(string identity, Document document)
    {
        var value = projection is null ? document.Body.Clone() : projection(document).Clone();
        return new IncrementalViewRow(identity, value);
    }

    private JsonElement AggregateValue(string groupIdentity, IReadOnlyList<Document> documents)
    {
        var separator = groupIdentity.IndexOf('\u001f');
        var group = separator < 0 ? groupIdentity : groupIdentity[(separator + 1)..];
        var result = new Dictionary<string, object?>
        {
            ["group"] = group,
        };
        switch (aggregate!.Kind)
        {
            case IncrementalAggregateKind.Count:
                result["count"] = documents.Count;
                break;
            case IncrementalAggregateKind.Sum:
                result["sum"] = documents.Sum(document => aggregate.ValueSelector!(document) ?? 0m);
                break;
            case IncrementalAggregateKind.Min:
                result["min"] = documents.Select(document => aggregate.ValueSelector!(document)).Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Min();
                break;
            case IncrementalAggregateKind.Max:
                result["max"] = documents.Select(document => aggregate.ValueSelector!(document)).Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max();
                break;
            default:
                throw new InvalidOperationException("Unknown aggregate kind.");
        }

        return JsonSerializer.SerializeToElement(result);
    }

    private void AddJoinIdentities(
        HashSet<string> identities,
        Document? changed,
        IReadOnlyDictionary<string, Document> documents,
        string counterpartCollection,
        bool changedLeft)
    {
        if (changed is null)
        {
            return;
        }

        var key = (changedLeft ? leftKey : rightKey)!(changed);
        if (key is null)
        {
            return;
        }

        foreach (var counterpart in documents.Values.Where(document =>
                     string.Equals(document.Key.Collection.Value, counterpartCollection, StringComparison.Ordinal)
                     && document.Key.Tenant == changed.Key.Tenant
                     && string.Equals((changedLeft ? rightKey : leftKey)!(document), key, StringComparison.Ordinal)))
        {
            identities.Add(changedLeft
                ? JoinIdentity(changed.Key, counterpart.Key)
                : JoinIdentity(counterpart.Key, changed.Key));
        }
    }

    private static JsonElement JoinValue(Document left, Document right) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
        {
            ["left"] = left.Body.Clone(),
            ["right"] = right.Body.Clone(),
        });

    internal static string SourceIdentity(DocumentKey key) =>
        string.Concat(key.Tenant.Value, '\u001f', key.Collection.Value, '\u001f', key.DocumentId);

    internal static string JoinIdentity(DocumentKey left, DocumentKey right) =>
        string.Concat(SourceIdentity(left), '\u001e', SourceIdentity(right));

    internal static string GroupIdentity(TenantId tenant, string group) =>
        string.Concat(tenant.Value, '\u001f', group);
}
