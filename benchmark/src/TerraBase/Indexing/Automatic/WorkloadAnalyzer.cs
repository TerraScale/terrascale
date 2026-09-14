using System.Collections.ObjectModel;

namespace TerraBase.Indexing.Automatic;

/// <summary>
/// Bounded workload observation and compression for automatic index tuning.
/// The analyzer keeps query shape and fixed statistics only; document bodies
/// never cross this boundary.
/// </summary>
public sealed class AutomaticWorkloadAnalyzer
{
    /// <summary>The default observation bound per analyzer.</summary>
    public const int DefaultObservationCapacity = 4096;

    /// <summary>The maximum templates retained in one compressed profile.</summary>
    public const int DefaultCompressedCapacity = 256;

    private readonly object sync = new();
    private readonly Queue<QueryExecutionObservation> observations;
    private readonly Dictionary<(AutomaticIndexScope Scope, string Path), FieldSketch> fields = [];
    private readonly int capacity;

    /// <summary>Creates a bounded analyzer.</summary>
    /// <param name="observationCapacity">The maximum retained observations.</param>
    public AutomaticWorkloadAnalyzer(int observationCapacity = DefaultObservationCapacity)
    {
        if (observationCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(observationCapacity));
        }

        capacity = Math.Min(observationCapacity, DefaultObservationCapacity);
        observations = new Queue<QueryExecutionObservation>(capacity);
    }

    /// <summary>The number of observations currently retained.</summary>
    public int ObservationCount
    {
        get
        {
            lock (sync)
            {
                return observations.Count;
            }
        }
    }

    /// <summary>Records an execution and evicts the oldest observation at the bound.</summary>
    public void Observe(QueryExecutionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (sync)
        {
            observations.Enqueue(observation);
            while (observations.Count > capacity)
            {
                observations.Dequeue();
            }
        }
    }

    /// <summary>
    /// Adds an already-extracted field value to a bounded distinct sketch.
    /// Values are represented by hashes and are not retained.
    /// </summary>
    public void ObserveField(AutomaticIndexScope scope, string path, ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (sync)
        {
            var key = (scope, path.Trim());
            if (!fields.TryGetValue(key, out var sketch))
            {
                sketch = new FieldSketch();
                fields.Add(key, sketch);
            }

            sketch.Observe(value);
        }
    }

    /// <summary>Analyzes a default 24-hour window up to 95 percent of workload weight.</summary>
    public WorkloadProfile Analyze() => Analyze(DateTimeOffset.UtcNow, TimeSpan.FromHours(24), 0.95, DefaultCompressedCapacity);

    /// <summary>
    /// Compresses observations by normalized template. AIM weights execution
    /// frequency by logical work, while RIB receives the retained variance and
    /// field selectivity evidence.
    /// </summary>
    public WorkloadProfile Analyze(
        DateTimeOffset now,
        TimeSpan window,
        double coverage = 0.95,
        int maxTemplates = DefaultCompressedCapacity)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        if (!double.IsFinite(coverage) || coverage <= 0 || coverage > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(coverage));
        }

        if (maxTemplates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTemplates));
        }

        QueryExecutionObservation[] snapshot;
        Dictionary<(AutomaticIndexScope Scope, string Path), FieldSketch> fieldSnapshot;
        lock (sync)
        {
            snapshot = observations.ToArray();
            fieldSnapshot = fields.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        }

        var start = now - window;
        var grouped = snapshot
            .Where(observation => observation.ObservedAt >= start && observation.ObservedAt <= now)
            .GroupBy(observation => observation.Shape.CanonicalKey, StringComparer.Ordinal)
            .Select(group => Aggregate(group))
            .GroupBy(template => template.Shape.Scope, EqualityComparer<AutomaticIndexScope>.Default)
            .ToArray();

        var selected = new List<WeightedWorkloadTemplate>();
        foreach (var collection in grouped)
        {
            var ranked = collection
                .OrderByDescending(template => template.Weight)
                .ThenBy(template => template.Shape.CanonicalKey, StringComparer.Ordinal)
                .ToArray();
            var totalWeight = ranked.Sum(template => template.Weight);
            var targetWeight = totalWeight * coverage;
            var covered = 0.0;
            foreach (var template in ranked)
            {
                if (selected.Count >= maxTemplates)
                {
                    break;
                }

                selected.Add(template);
                covered += template.Weight;
                if (covered + (totalWeight * 1e-12) >= targetWeight)
                {
                    break;
                }
            }
        }

        selected.Sort((left, right) =>
        {
            var weight = right.Weight.CompareTo(left.Weight);
            return weight != 0 ? weight : StringComparer.Ordinal.Compare(left.Shape.CanonicalKey, right.Shape.CanonicalKey);
        });

        var statistics = fieldSnapshot
            .Select(pair => new FieldStatistics(
                pair.Key.Path,
                pair.Value.Total,
                pair.Value.DistinctEstimate,
                pair.Value.Selectivity))
            .OrderBy(statistic => statistic.Path, StringComparer.Ordinal)
            .ToArray();
        return new WorkloadProfile(new ReadOnlyCollection<WeightedWorkloadTemplate>(selected), statistics);
    }

    private static WeightedWorkloadTemplate Aggregate(IEnumerable<QueryExecutionObservation> source)
    {
        var values = source.ToArray();
        var shape = values[0].Shape;
        var count = (ulong)values.Length;
        var mean = values.Average(value => (double)value.Work.TotalUnits);
        var variance = values.Length == 1
            ? 0
            : values.Sum(value => Math.Pow(value.Work.TotalUnits - mean, 2)) / (values.Length - 1);
        return new WeightedWorkloadTemplate(shape, count, mean, variance);
    }

    private sealed class FieldSketch
    {
        private const int DistinctCapacity = 256;
        private readonly HashSet<ulong> values = [];

        public ulong Total { get; private set; }

        public double DistinctEstimate => values.Count < DistinctCapacity ? values.Count : DistinctCapacity;

        public double Selectivity => Total == 0 ? 1 : Math.Clamp(DistinctEstimate / Total, 0, 1);

        public void Observe(ReadOnlySpan<byte> value)
        {
            Total = Total == ulong.MaxValue ? ulong.MaxValue : Total + 1;
            if (values.Count < DistinctCapacity)
            {
                values.Add(Fnv1A(value));
            }
        }

        public FieldSketch Clone()
        {
            var clone = new FieldSketch { Total = Total };
            clone.values.UnionWith(values);
            return clone;
        }

        private static ulong Fnv1A(ReadOnlySpan<byte> bytes)
        {
            var hash = 14695981039346656037UL;
            foreach (var value in bytes)
            {
                hash = (hash ^ value) * 1099511628211UL;
            }

            return hash;
        }
    }
}

/// <summary>Aliases the analyzer under the AIM/RIB design vocabulary.</summary>
public sealed class AimRibWorkloadAnalyzer
{
    private readonly AutomaticWorkloadAnalyzer analyzer;

    /// <summary>Creates an AIM/RIB analyzer.</summary>
    public AimRibWorkloadAnalyzer(int observationCapacity = AutomaticWorkloadAnalyzer.DefaultObservationCapacity)
    {
        analyzer = new AutomaticWorkloadAnalyzer(observationCapacity);
    }

    /// <summary>Records an execution.</summary>
    public void Observe(QueryExecutionObservation observation) => analyzer.Observe(observation);

    /// <summary>Records an extracted field value.</summary>
    public void ObserveField(AutomaticIndexScope scope, string path, ReadOnlySpan<byte> value) => analyzer.ObserveField(scope, path, value);

    /// <summary>Returns the compressed AIM/RIB profile.</summary>
    public WorkloadProfile Analyze(
        DateTimeOffset now,
        TimeSpan window,
        double coverage = 0.95,
        int maxTemplates = AutomaticWorkloadAnalyzer.DefaultCompressedCapacity) => analyzer.Analyze(now, window, coverage, maxTemplates);
}

/// <summary>Builds ordered index candidates from compressed query shapes.</summary>
public static class AutomaticIndexCandidateFactory
{
    /// <summary>Derives B-tree, covering, or full-text candidates for one template.</summary>
    public static IReadOnlyList<AutomaticIndexCandidate> Derive(
        WeightedWorkloadTemplate template,
        ISet<string>? extractedFields = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        extractedFields ??= new HashSet<string>(StringComparer.Ordinal);
        if (template.Shape.HasFullText)
        {
            var fullText = template.Shape.Predicates.FirstOrDefault(predicate => predicate.Kind == QueryPredicateKind.FullText);
            return fullText is null
                ? []
                : [new AutomaticIndexCandidate(template.Shape.Scope, [fullText.Path], AutomaticIndexKind.FullText, !extractedFields.Contains(fullText.Path), 1)];
        }

        var fields = new List<string>();
        foreach (var predicate in template.Shape.Predicates.Where(predicate => predicate.Kind == QueryPredicateKind.Equality || predicate.Kind == QueryPredicateKind.Range))
        {
            if (!fields.Contains(predicate.Path, StringComparer.Ordinal))
            {
                fields.Add(predicate.Path);
            }

            if (predicate.Kind == QueryPredicateKind.Range)
            {
                break;
            }
        }

        foreach (var ordering in template.Shape.Ordering)
        {
            if (!fields.Contains(ordering.Path, StringComparer.Ordinal))
            {
                fields.Add(ordering.Path);
            }
        }

        if (fields.Count == 0)
        {
            return [];
        }

        var needsDescriptor = fields.Any(path => !extractedFields.Contains(path));
        var candidates = new List<AutomaticIndexCandidate>
        {
            new(template.Shape.Scope, fields, AutomaticIndexKind.BTree, needsDescriptor, (ulong)fields.Count),
        };
        var covering = fields.Concat(template.Shape.Projection).Distinct(StringComparer.Ordinal).ToArray();
        if (covering.Length > fields.Count && covering.Length <= 40)
        {
            candidates.Add(new AutomaticIndexCandidate(template.Shape.Scope, covering, AutomaticIndexKind.BTree, needsDescriptor, (ulong)covering.Length));
        }

        return candidates;
    }

    /// <summary>Derives and de-duplicates candidates for an entire profile.</summary>
    public static IReadOnlyList<AutomaticIndexCandidate> DeriveAll(WorkloadProfile profile, ISet<string>? extractedFields = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Templates
            .SelectMany(template => Derive(template, extractedFields))
            .Distinct()
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>Deterministic AIM/RIB candidate scoring.</summary>
public static class AutomaticIndexCandidateScorer
{
    /// <summary>Scores one candidate against a compressed workload.</summary>
    public static CandidateScore Score(
        AutomaticIndexCandidate candidate,
        WorkloadProfile profile,
        AutomaticIndexBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);
        var aim = 0.0;
        var uncertainty = 0.0;
        foreach (var template in profile.Templates.Where(template => template.Shape.Scope == candidate.Scope))
        {
            var coverage = Coverage(candidate, template.Shape);
            if (coverage <= 0)
            {
                continue;
            }

            var selectivity = profile.Fields
                .Where(statistic => candidate.Fields.Contains(statistic.Path, StringComparer.Ordinal))
                .Select(statistic => statistic.Selectivity)
                .DefaultIfEmpty(0.5)
                .Average();
            var benefit = template.Weight * coverage * (1 - (0.35 * selectivity));
            aim += Math.Max(0, benefit);
            var sampleUncertainty = Math.Sqrt(Math.Log(template.Frequency + 2.0) / (template.Frequency + 1.0));
            uncertainty += benefit * sampleUncertainty * 0.25;
        }

        var variance = profile.Templates
            .Where(template => template.Shape.Scope == candidate.Scope)
            .Sum(template => template.WorkVariance);
        var standardError = profile.TotalExecutions == 0 ? 0 : Math.Sqrt(variance / profile.TotalExecutions);
        var ribLowerBound = Math.Max(0, aim - (1.96 * standardError));
        var writePenalty = candidate.EstimatedMaintenanceUnits;
        if (budget is not null && budget.CpuUnits > 0)
        {
            writePenalty *= 1 + (candidate.EstimatedMaintenanceUnits / budget.CpuUnits);
        }

        return new CandidateScore(aim, ribLowerBound, uncertainty, writePenalty);
    }

    /// <summary>Scores and sorts candidates by descending total score.</summary>
    public static IReadOnlyList<ScoredIndexCandidate> ScoreAll(
        IEnumerable<AutomaticIndexCandidate> candidates,
        WorkloadProfile profile,
        AutomaticIndexBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(profile);
        return candidates
            .Select(candidate => new ScoredIndexCandidate(candidate, Score(candidate, profile, budget)))
            .OrderByDescending(scored => scored.Score.Total)
            .ThenBy(scored => scored.Candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();
    }

    private static double Coverage(AutomaticIndexCandidate candidate, QueryShape shape)
    {
        if (candidate.Kind == AutomaticIndexKind.FullText)
        {
            return shape.HasFullText && shape.Predicates.Any(predicate => predicate.Kind == QueryPredicateKind.FullText && candidate.Fields.Contains(predicate.Path, StringComparer.Ordinal)) ? 1 : 0;
        }

        var predicateFields = shape.Predicates
            .Where(predicate => predicate.Kind == QueryPredicateKind.Equality || predicate.Kind == QueryPredicateKind.Range)
            .Select(predicate => predicate.Path)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (predicateFields.Length == 0)
        {
            return 0;
        }

        var covered = predicateFields.TakeWhile((field, index) => index < candidate.Fields.Count && candidate.Fields[index] == field).Count();
        var orderingBonus = shape.Ordering.Count > 0 && shape.Ordering.All(order => candidate.Fields.Contains(order.Path, StringComparer.Ordinal)) ? 0.15 : 0;
        return Math.Min(1, (double)covered / predicateFields.Length + orderingBonus);
    }
}

/// <summary>A verified widening opportunity between compatible candidates.</summary>
public sealed record CandidateConsolidation(
    AutomaticIndexCandidate Replacement,
    IReadOnlyList<string> SupersededCandidateIds,
    double NetBenefit);

/// <summary>The retained candidates and widening plans from consolidation.</summary>
public sealed record CandidateConsolidationResult(
    IReadOnlyList<ScoredIndexCandidate> Retained,
    IReadOnlyList<CandidateConsolidation> Plans);

/// <summary>Consolidates compatible prefix candidates only when evidence improves.</summary>
public static class AutomaticIndexCandidateConsolidator
{
    /// <summary>
    /// Returns widening plans that improve measured score by at least the
    /// supplied fraction. Existing candidates remain untouched when evidence
    /// is insufficient.
    /// </summary>
    public static CandidateConsolidationResult Consolidate(
        IEnumerable<ScoredIndexCandidate> scoredCandidates,
        double minimumImprovement = 0.05)
    {
        ArgumentNullException.ThrowIfNull(scoredCandidates);
        if (!double.IsFinite(minimumImprovement) || minimumImprovement < 0 || minimumImprovement > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumImprovement));
        }

        var ordered = scoredCandidates
            .OrderByDescending(candidate => candidate.Score.Total)
            .ThenBy(candidate => candidate.Candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        var superseded = new HashSet<string>(StringComparer.Ordinal);
        var plans = new List<CandidateConsolidation>();
        foreach (var narrow in ordered)
        {
            var wider = ordered
                .Where(candidate => !superseded.Contains(candidate.Candidate.CandidateId))
                .Where(candidate => narrow.Candidate.IsPrefixOf(candidate.Candidate))
                .OrderByDescending(candidate => candidate.Score.Total)
                .ThenBy(candidate => candidate.Candidate.CandidateId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (wider is null)
            {
                continue;
            }

            var baseline = Math.Max(0, narrow.Score.Total);
            var required = baseline == 0 ? minimumImprovement : baseline * (1 + minimumImprovement);
            if (wider.Score.Total < required)
            {
                continue;
            }

            superseded.Add(narrow.Candidate.CandidateId);
            plans.Add(new CandidateConsolidation(wider.Candidate, [narrow.Candidate.CandidateId], wider.Score.Total - narrow.Score.Total));
        }

        return new CandidateConsolidationResult(
            ordered.Where(candidate => !superseded.Contains(candidate.Candidate.CandidateId)).ToArray(),
            plans);
    }
}
