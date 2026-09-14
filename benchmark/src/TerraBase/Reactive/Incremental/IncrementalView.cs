namespace TerraBase.Reactive.Incremental;

/// <summary>
/// Maintains one reactive view from committed source document changes.
/// </summary>
public sealed class IncrementalView
{
    private readonly object gate = new();
    private readonly IncrementalViewPlan plan;
    private Dictionary<DocumentKey, Document> documents = new();
    private Dictionary<string, IncrementalViewRow> rows = new(StringComparer.Ordinal);
    private IncrementalWatermark watermark;

    /// <summary>Creates an empty view for a plan.</summary>
    /// <param name="plan">The supported or fallback plan.</param>
    public IncrementalView(IncrementalViewPlan plan)
    {
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    /// <summary>The plan being maintained.</summary>
    public IncrementalViewPlan Plan => plan;

    /// <summary>The latest transaction position incorporated by the view.</summary>
    public IncrementalWatermark Watermark
    {
        get
        {
            lock (gate)
            {
                return watermark;
            }
        }
    }

    /// <summary>Seeds the view at one consistent source position.</summary>
    /// <param name="source">The source snapshot.</param>
    /// <param name="watermark">The position represented by the source snapshot.</param>
    public void Seed(IEnumerable<Document> source, IncrementalWatermark watermark = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (gate)
        {
            if (watermark.Value < this.watermark.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(watermark), "A view cannot move to an older source watermark.");
            }

            var replacement = source.ToDictionary(document => document.Key, document => document);
            var replacementRows = EvaluateSeed(replacement);
            documents = replacement;
            rows = replacementRows;
            this.watermark = watermark;
        }
    }

    /// <summary>Returns an immutable view snapshot.</summary>
    public IncrementalViewSnapshot Snapshot()
    {
        lock (gate)
        {
            return new(watermark, OrderedRows(rows), plan.Mode);
        }
    }

    /// <summary>Applies one atomic committed transaction.</summary>
    /// <param name="transaction">The transaction and its complete before images.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The net materialized delta.</returns>
    public ValueTask<IncrementalApplyResult> ApplyAsync(
        IncrementalTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return ValueTask.FromResult(ApplyLocked(transaction, cancellationToken));
        }
    }

    /// <summary>Applies one atomic committed transaction synchronously.</summary>
    public IncrementalApplyResult Apply(
        IncrementalTransaction transaction,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(transaction, cancellationToken).GetAwaiter().GetResult();

    private IncrementalApplyResult ApplyLocked(
        IncrementalTransaction transaction,
        CancellationToken cancellationToken)
    {
        var incoming = transaction.Watermark;
        if (incoming.Value < watermark.Value)
        {
            return Result(IncrementalApplyStatus.Stale, watermark, [], [], false);
        }

        if (incoming.Value == watermark.Value)
        {
            return Result(IncrementalApplyStatus.Duplicate, watermark, [], [], false);
        }

        var nextDocuments = new Dictionary<DocumentKey, Document>(documents);
        try
        {
            foreach (var change in transaction.Changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextDocuments.TryGetValue(change.Key, out var current);
                if (!Equivalent(current, change.Before))
                {
                    return Result(IncrementalApplyStatus.Conflict, watermark, [], [], false);
                }

                if (change.After is null)
                {
                    nextDocuments.Remove(change.Key);
                }
                else
                {
                    nextDocuments[change.Key] = change.After;
                }
            }

            var oldRows = rows;
            var oldDocuments = ToSourceRows(documents);
            var newSourceRows = ToSourceRows(nextDocuments);
            var nextRows = plan.Evaluate(nextDocuments, oldSourceRows, newSourceRows, transaction.Changes, oldRows);
            var reset = plan.Mode == IncrementalMaintenanceMode.Fallback;
            var (added, removed) = Difference(oldRows, nextRows);

            documents = nextDocuments;
            rows = new Dictionary<string, IncrementalViewRow>(nextRows, StringComparer.Ordinal);
            watermark = incoming;
            return Result(IncrementalApplyStatus.Applied, incoming, added, removed, reset);
        }
        catch
        {
            // All derived state is assigned only after plan evaluation succeeds.
            // A failed evaluator therefore cannot advance the view watermark.
            throw;
        }
    }

    private Dictionary<string, IncrementalViewRow> EvaluateSeed(
        IReadOnlyDictionary<DocumentKey, Document> source)
    {
        if (plan.Mode == IncrementalMaintenanceMode.Fallback)
        {
            return plan.Evaluate(source, new Dictionary<string, Document>(), new Dictionary<string, Document>(), [], new Dictionary<string, IncrementalViewRow>(StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        var sourceRows = ToSourceRows(source);
        return plan switch
        {
            _ when plan.Collections.Count == 1 && plan.Mode == IncrementalMaintenanceMode.Incremental =>
                SeedSingleCollection(sourceRows),
            _ => SeedByEmptyTransaction(source),
        };
    }

    private Dictionary<string, IncrementalViewRow> SeedSingleCollection(
        IReadOnlyDictionary<string, Document> source)
    {
        var result = new Dictionary<string, IncrementalViewRow>(StringComparer.Ordinal);
        foreach (var document in source.Values)
        {
            if (!string.Equals(document.Key.Collection.Value, plan.Collections[0], StringComparison.Ordinal))
            {
                continue;
            }

            // Applying a synthetic create gives filters and aggregates their
            // normal operator semantics while retaining one consistent seed.
            var change = IncrementalDocumentChange.Upsert(document);
            var next = plan.Evaluate(
                new Dictionary<DocumentKey, Document> { [document.Key] = document },
                new Dictionary<string, Document>(),
                new Dictionary<string, Document> { [IncrementalViewPlan.SourceIdentity(document.Key)] = document },
                [change],
                result);
            result = new Dictionary<string, IncrementalViewRow>(next, StringComparer.Ordinal);
        }

        if (plan is not null && plan.Collections.Count == 1 && plan.Mode == IncrementalMaintenanceMode.Incremental)
        {
            // Aggregate seeding needs all rows in a group at once. Replaying
            // creates above still produces the same final state for filters;
            // aggregates are rebuilt from the complete source below.
            if (result.Values.Any(row => row.Value.ValueKind == System.Text.Json.JsonValueKind.Object && row.Value.TryGetProperty("count", out _)))
            {
                result.Clear();
                result = SeedByEmptyTransaction(source.ToDictionary(
                    pair => new DocumentKey(
                        new TenantId(pair.Value.Key.Tenant.Value),
                        new CollectionId(pair.Value.Key.Collection.Value),
                        pair.Value.Key.DocumentId),
                    pair => pair.Value));
            }
        }

        return result;
    }

    private Dictionary<string, IncrementalViewRow> SeedByEmptyTransaction(
        IReadOnlyDictionary<DocumentKey, Document> source)
    {
        var result = new Dictionary<string, IncrementalViewRow>(StringComparer.Ordinal);
        if (plan.Collections.Count == 1)
        {
            foreach (var document in source.Values.Where(document => string.Equals(document.Key.Collection.Value, plan.Collections[0], StringComparison.Ordinal)))
            {
                var change = IncrementalDocumentChange.Upsert(document);
                var next = plan.Evaluate(source, new Dictionary<string, Document>(), ToSourceRows(source), [change], result);
                result = new Dictionary<string, IncrementalViewRow>(next, StringComparer.Ordinal);
            }

            return result;
        }

        var changes = source.Values
            .Where(document => plan.Collections.Contains(document.Key.Collection.Value, StringComparer.Ordinal))
            .Select(IncrementalDocumentChange.Upsert)
            .ToArray();
        return plan.Evaluate(source, new Dictionary<string, Document>(), ToSourceRows(source), changes, result)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private Dictionary<string, Document> ToSourceRows(
        IReadOnlyDictionary<DocumentKey, Document> source) =>
        plan.SourceRows(source).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static (IReadOnlyList<IncrementalViewRow> Added, IReadOnlyList<IncrementalViewRow> Removed) Difference(
        IReadOnlyDictionary<string, IncrementalViewRow> oldRows,
        IReadOnlyDictionary<string, IncrementalViewRow> newRows)
    {
        var added = new List<IncrementalViewRow>();
        var removed = new List<IncrementalViewRow>();
        foreach (var pair in oldRows)
        {
            if (!newRows.TryGetValue(pair.Key, out var replacement) || !Equivalent(pair.Value, replacement))
            {
                removed.Add(pair.Value);
            }
        }

        foreach (var pair in newRows)
        {
            if (!oldRows.TryGetValue(pair.Key, out var previous) || !Equivalent(previous, pair.Value))
            {
                added.Add(pair.Value);
            }
        }

        return (OrderRows(added), OrderRows(removed));
    }

    private static IReadOnlyList<IncrementalViewRow> OrderedRows(
        IReadOnlyDictionary<string, IncrementalViewRow> rows) =>
        OrderRows(rows.Values);

    private static IReadOnlyList<IncrementalViewRow> OrderRows(IEnumerable<IncrementalViewRow> rows) =>
        rows.OrderBy(row => row.Identity, StringComparer.Ordinal).ToArray();

    private static IncrementalApplyResult Result(
        IncrementalApplyStatus status,
        IncrementalWatermark watermark,
        IReadOnlyList<IncrementalViewRow> added,
        IReadOnlyList<IncrementalViewRow> removed,
        bool reset) =>
        new(new IncrementalViewDelta(status, watermark, added, removed, reset, IncrementalMaintenanceMode.Incremental));

    private static bool Equivalent(Document? left, Document? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Key == right.Key && left.Version == right.Version && left.Body.GetRawText() == right.Body.GetRawText();
    }

    private static bool Equivalent(IncrementalViewRow left, IncrementalViewRow right) =>
        string.Equals(left.Identity, right.Identity, StringComparison.Ordinal)
        && left.Value.GetRawText() == right.Value.GetRawText();
}
