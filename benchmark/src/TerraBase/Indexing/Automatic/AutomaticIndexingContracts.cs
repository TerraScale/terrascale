using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace TerraBase.Indexing.Automatic;

/// <summary>The kind of predicate in a normalized query shape.</summary>
public enum QueryPredicateKind
{
    /// <summary>An equality predicate such as <c>=</c> or <c>IN</c>.</summary>
    Equality,

    /// <summary>A range predicate such as <c>&gt;</c> or <c>BETWEEN</c>.</summary>
    Range,

    /// <summary>A full-text predicate.</summary>
    FullText,
}

/// <summary>The direction of an ordered index key.</summary>
public enum IndexSortDirection
{
    /// <summary>Ascending order.</summary>
    Ascending,

    /// <summary>Descending order.</summary>
    Descending,
}

/// <summary>The physical implementation proposed for a candidate.</summary>
public enum AutomaticIndexKind
{
    /// <summary>An ordered index over extracted fields.</summary>
    BTree,

    /// <summary>A full-text index.</summary>
    FullText,
}

/// <summary>Controls whether automatic index actions are allowed for a scope.</summary>
public enum AutomaticIndexMode
{
    /// <summary>Observe and apply eligible recommendations.</summary>
    On,

    /// <summary>Observe and recommend, but wait for an explicit apply command.</summary>
    RecommendOnly,

    /// <summary>Do not create automatic indexes.</summary>
    Off,
}

/// <summary>The durable lifecycle phase of an automatic index.</summary>
public enum AutomaticIndexPhase
{
    /// <summary>The candidate is retained for consideration.</summary>
    Candidate,

    /// <summary>The candidate has been admitted for construction.</summary>
    Selected,

    /// <summary>The physical index is being built invisibly.</summary>
    Building,

    /// <summary>The complete build is being verified against a baseline.</summary>
    Shadowing,

    /// <summary>The index is visible to the planner.</summary>
    Active,

    /// <summary>The index is hidden but retained during the grace period.</summary>
    Invisible,

    /// <summary>The physical index was removed and the candidate is terminal.</summary>
    Retired,

    /// <summary>The candidate was rejected and cannot serve queries.</summary>
    Abandoned,

    /// <summary>The candidate is blocked by a manual control or failed operation.</summary>
    Blocked,
}

/// <summary>A tenant, database, collection, and schema generation boundary.</summary>
public sealed record AutomaticIndexScope
{
    /// <summary>Creates a scope identity.</summary>
    public AutomaticIndexScope(string tenant, string database, string collection, ulong schemaGeneration = 0)
    {
        Tenant = Validate(tenant, nameof(tenant));
        Database = Validate(database, nameof(database));
        Collection = Validate(collection, nameof(collection));
        SchemaGeneration = schemaGeneration;
    }

    /// <summary>The owning tenant.</summary>
    public string Tenant { get; }

    /// <summary>The owning database.</summary>
    public string Database { get; }

    /// <summary>The collection being indexed.</summary>
    public string Collection { get; }

    /// <summary>The schema generation from which the definition is valid.</summary>
    public ulong SchemaGeneration { get; }

    /// <summary>Returns the deterministic scope identity.</summary>
    public string CanonicalKey =>
        $"{Escape(Tenant)}|{Escape(Database)}|{Escape(Collection)}|{SchemaGeneration}";

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string Escape(string value) => value.Replace("|", "||", StringComparison.Ordinal);
}

/// <summary>A normalized field predicate.</summary>
public sealed record QueryPredicate
{
    /// <summary>Creates a normalized predicate.</summary>
    public QueryPredicate(string path, QueryPredicateKind kind)
    {
        Path = Validate(path, nameof(path));
        Kind = kind;
    }

    /// <summary>The extracted JSON path.</summary>
    public string Path { get; }

    /// <summary>The predicate class.</summary>
    public QueryPredicateKind Kind { get; }

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

/// <summary>A normalized ordered field in a query shape.</summary>
public sealed record QueryOrdering
{
    /// <summary>Creates an ordered field.</summary>
    public QueryOrdering(string path, IndexSortDirection direction = IndexSortDirection.Ascending)
    {
        Path = Validate(path, nameof(path));
        Direction = direction;
    }

    /// <summary>The extracted JSON path.</summary>
    public string Path { get; }

    /// <summary>The requested sort direction.</summary>
    public IndexSortDirection Direction { get; }

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

/// <summary>A canonical, authorization-filtered query shape.</summary>
public sealed class QueryShape : IEquatable<QueryShape>
{
    /// <summary>Creates a query shape from normalized predicates and projections.</summary>
    public QueryShape(
        AutomaticIndexScope scope,
        IEnumerable<QueryPredicate>? predicates = null,
        IEnumerable<QueryOrdering>? ordering = null,
        IEnumerable<string>? projection = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Predicates = new ReadOnlyCollection<QueryPredicate>((predicates ?? []).ToArray());
        Ordering = new ReadOnlyCollection<QueryOrdering>((ordering ?? []).ToArray());
        Projection = new ReadOnlyCollection<string>((projection ?? [])
            .Select(path => Validate(path, nameof(projection)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray());
        if (Predicates.Any(predicate => predicate is null))
        {
            throw new ArgumentException("Predicates cannot contain null values.", nameof(predicates));
        }

        if (Ordering.Any(order => order is null))
        {
            throw new ArgumentException("Ordering cannot contain null values.", nameof(ordering));
        }

        CanonicalKey = BuildCanonicalKey();
    }

    /// <summary>The tenant/database/collection boundary.</summary>
    public AutomaticIndexScope Scope { get; }

    /// <summary>The normalized predicates.</summary>
    public IReadOnlyList<QueryPredicate> Predicates { get; }

    /// <summary>The normalized ordering keys.</summary>
    public IReadOnlyList<QueryOrdering> Ordering { get; }

    /// <summary>The projected fields, sorted for identity.</summary>
    public IReadOnlyList<string> Projection { get; }

    /// <summary>The canonical identity used for workload compression and pins.</summary>
    public string CanonicalKey { get; }

    /// <summary>Whether this shape contains a full-text predicate.</summary>
    public bool HasFullText => Predicates.Any(predicate => predicate.Kind == QueryPredicateKind.FullText);

    /// <inheritdoc />
    public bool Equals(QueryShape? other) => other is not null && CanonicalKey == other.CanonicalKey;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is QueryShape other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalKey);

    private string BuildCanonicalKey()
    {
        var predicates = Predicates
            .OrderBy(predicate => predicate.Kind)
            .ThenBy(predicate => predicate.Path, StringComparer.Ordinal)
            .Select(predicate => $"{predicate.Kind}:{Escape(predicate.Path)}");
        var ordering = Ordering.Select(order => $"{Escape(order.Path)}:{order.Direction}");
        return string.Join("|", [Scope.CanonicalKey, string.Join(",", predicates), string.Join(",", ordering), string.Join(",", Projection.Select(Escape))]);
    }

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string Escape(string value) => value.Replace("|", "||", StringComparison.Ordinal).Replace(",", ",,", StringComparison.Ordinal);
}

/// <summary>Logical work measured while executing one query.</summary>
public readonly record struct LogicalWork(ulong PagesRead, ulong RowsExamined)
{
    /// <summary>Returns the bounded sum used by the benefit estimator.</summary>
    public ulong TotalUnits => PagesRead > ulong.MaxValue - RowsExamined ? ulong.MaxValue : PagesRead + RowsExamined;
}

/// <summary>One bounded execution observation supplied by the query path.</summary>
public sealed record QueryExecutionObservation
{
    /// <summary>Creates an execution observation.</summary>
    public QueryExecutionObservation(QueryShape shape, LogicalWork work, ulong rowsReturned, DateTimeOffset observedAt)
    {
        Shape = shape ?? throw new ArgumentNullException(nameof(shape));
        Work = work;
        RowsReturned = rowsReturned;
        ObservedAt = observedAt;
    }

    /// <summary>The normalized query shape.</summary>
    public QueryShape Shape { get; }

    /// <summary>The logical pages and rows measured by the execution.</summary>
    public LogicalWork Work { get; }

    /// <summary>The number of rows returned.</summary>
    public ulong RowsReturned { get; }

    /// <summary>The observation timestamp.</summary>
    public DateTimeOffset ObservedAt { get; }
}

/// <summary>Aggregated evidence for one normalized query template.</summary>
public sealed record WeightedWorkloadTemplate
{
    /// <summary>Creates weighted template evidence.</summary>
    public WeightedWorkloadTemplate(QueryShape shape, ulong frequency, double averageWork, double workVariance)
    {
        Shape = shape ?? throw new ArgumentNullException(nameof(shape));
        Frequency = frequency;
        AverageWork = double.IsFinite(averageWork) && averageWork >= 0 ? averageWork : throw new ArgumentOutOfRangeException(nameof(averageWork));
        WorkVariance = double.IsFinite(workVariance) && workVariance >= 0 ? workVariance : throw new ArgumentOutOfRangeException(nameof(workVariance));
    }

    /// <summary>The normalized query shape.</summary>
    public QueryShape Shape { get; }

    /// <summary>The execution count in the analysis window.</summary>
    public ulong Frequency { get; }

    /// <summary>The average logical work.</summary>
    public double AverageWork { get; }

    /// <summary>The sample variance of logical work.</summary>
    public double WorkVariance { get; }

    /// <summary>The AIM workload weight.</summary>
    public double Weight => Frequency * AverageWork;
}

/// <summary>Bounded per-field statistics used by RIB benefit estimation.</summary>
public sealed record FieldStatistics(string Path, ulong Observations, double DistinctEstimate, double Selectivity);

/// <summary>The bounded workload view consumed by candidate scoring.</summary>
public sealed class WorkloadProfile
{
    /// <summary>Creates a workload profile.</summary>
    public WorkloadProfile(IEnumerable<WeightedWorkloadTemplate> templates, IEnumerable<FieldStatistics>? fields = null)
    {
        Templates = new ReadOnlyCollection<WeightedWorkloadTemplate>((templates ?? throw new ArgumentNullException(nameof(templates))).ToArray());
        Fields = new ReadOnlyCollection<FieldStatistics>((fields ?? []).ToArray());
    }

    /// <summary>The compressed templates in descending workload weight.</summary>
    public IReadOnlyList<WeightedWorkloadTemplate> Templates { get; }

    /// <summary>The field sketches known to the analyzer.</summary>
    public IReadOnlyList<FieldStatistics> Fields { get; }

    /// <summary>The total execution count represented by the profile.</summary>
    public ulong TotalExecutions => Templates.Aggregate<WeightedWorkloadTemplate, ulong>(0, (sum, template) => sum > ulong.MaxValue - template.Frequency ? ulong.MaxValue : sum + template.Frequency);
}

/// <summary>A candidate's ordered field capabilities.</summary>
public sealed class AutomaticIndexCandidate : IEquatable<AutomaticIndexCandidate>
{
    /// <summary>Creates a deterministic candidate definition.</summary>
    public AutomaticIndexCandidate(
        AutomaticIndexScope scope,
        IEnumerable<string> fields,
        AutomaticIndexKind kind = AutomaticIndexKind.BTree,
        bool requiresDescriptor = false,
        ulong? estimatedMaintenanceUnits = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Fields = new ReadOnlyCollection<string>((fields ?? throw new ArgumentNullException(nameof(fields)))
            .Select(path => Validate(path, nameof(fields)))
            .ToArray());
        if (Fields.Count == 0)
        {
            throw new ArgumentException("An index candidate requires at least one field.", nameof(fields));
        }

        Kind = kind;
        RequiresDescriptor = requiresDescriptor;
        EstimatedMaintenanceUnits = estimatedMaintenanceUnits ?? checked((ulong)Fields.Count);
        CandidateId = ComputeId();
    }

    /// <summary>The scope owned by this candidate.</summary>
    public AutomaticIndexScope Scope { get; }

    /// <summary>The ordered indexed fields.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>The physical index kind.</summary>
    public AutomaticIndexKind Kind { get; }

    /// <summary>Whether a schema descriptor must be installed first.</summary>
    public bool RequiresDescriptor { get; }

    /// <summary>The estimated maintenance units per write.</summary>
    public ulong EstimatedMaintenanceUnits { get; }

    /// <summary>The deterministic domain-separated candidate identity.</summary>
    public string CandidateId { get; }

    /// <summary>Whether this candidate is an ordered prefix of another.</summary>
    public bool IsPrefixOf(AutomaticIndexCandidate other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind == other.Kind
            && Scope == other.Scope
            && Fields.Count < other.Fields.Count
            && Fields.SequenceEqual(other.Fields.Take(Fields.Count), StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool Equals(AutomaticIndexCandidate? other) => other is not null && CandidateId == other.CandidateId;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AutomaticIndexCandidate other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CandidateId);

    private string ComputeId()
    {
        var definition = $"terrabase:auto-index:v1|{Scope.CanonicalKey}|{Kind}|{string.Join(",", Fields.Select(Escape))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition))).ToLowerInvariant();
    }

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string Escape(string value) => value.Replace("|", "||", StringComparison.Ordinal).Replace(",", ",,", StringComparison.Ordinal);
}

/// <summary>The score produced by the AIM/RIB estimator.</summary>
public sealed record CandidateScore
{
    /// <summary>Creates a candidate score.</summary>
    public CandidateScore(double aimBenefit, double ribLowerBound, double uncertaintyBonus, double writePenalty)
    {
        AimBenefit = aimBenefit;
        RibLowerBound = ribLowerBound;
        UncertaintyBonus = uncertaintyBonus;
        WritePenalty = writePenalty;
    }

    /// <summary>The measured frequency-times-work benefit.</summary>
    public double AimBenefit { get; }

    /// <summary>The conservative RIB lower-bound benefit.</summary>
    public double RibLowerBound { get; }

    /// <summary>The uncertainty allowance used for bounded exploration.</summary>
    public double UncertaintyBonus { get; }

    /// <summary>The write maintenance penalty.</summary>
    public double WritePenalty { get; }

    /// <summary>The final deterministic ranking score.</summary>
    public double Total => RibLowerBound + UncertaintyBonus - WritePenalty;
}

/// <summary>A candidate and its score.</summary>
public sealed record ScoredIndexCandidate(AutomaticIndexCandidate Candidate, CandidateScore Score);

/// <summary>Collection write and resource limits for automatic indexing.</summary>
public sealed record AutomaticIndexBudget
{
    /// <summary>Creates finite non-negative budget limits.</summary>
    public AutomaticIndexBudget(double cpuUnits, ulong memoryBytes, double ioUnits, int maxVisibleIndexes = 12)
    {
        if (!double.IsFinite(cpuUnits) || cpuUnits < 0) throw new ArgumentOutOfRangeException(nameof(cpuUnits));
        if (!double.IsFinite(ioUnits) || ioUnits < 0) throw new ArgumentOutOfRangeException(nameof(ioUnits));
        if (maxVisibleIndexes < 0) throw new ArgumentOutOfRangeException(nameof(maxVisibleIndexes));
        CpuUnits = cpuUnits;
        MemoryBytes = memoryBytes;
        IoUnits = ioUnits;
        MaxVisibleIndexes = maxVisibleIndexes;
    }

    /// <summary>Maximum CPU maintenance units.</summary>
    public double CpuUnits { get; }

    /// <summary>Maximum retained index memory.</summary>
    public ulong MemoryBytes { get; }

    /// <summary>Maximum I/O maintenance units.</summary>
    public double IoUnits { get; }

    /// <summary>Maximum visible index count.</summary>
    public int MaxVisibleIndexes { get; }
}

/// <summary>Estimated resource cost for one candidate.</summary>
public readonly record struct AutomaticIndexCost
{
    /// <summary>Creates a non-negative finite resource cost.</summary>
    public AutomaticIndexCost(double cpuUnits, ulong memoryBytes, double ioUnits)
    {
        if (!double.IsFinite(cpuUnits) || cpuUnits < 0) throw new ArgumentOutOfRangeException(nameof(cpuUnits));
        if (!double.IsFinite(ioUnits) || ioUnits < 0) throw new ArgumentOutOfRangeException(nameof(ioUnits));
        CpuUnits = cpuUnits;
        MemoryBytes = memoryBytes;
        IoUnits = ioUnits;
    }

    /// <summary>CPU maintenance units.</summary>
    public double CpuUnits { get; }

    /// <summary>Memory bytes.</summary>
    public ulong MemoryBytes { get; }

    /// <summary>I/O maintenance units.</summary>
    public double IoUnits { get; }
}

/// <summary>Current resource usage returned by a budget ledger.</summary>
public readonly record struct AutomaticIndexUsage(double CpuUnits, ulong MemoryBytes, double IoUnits, int VisibleIndexes);

/// <summary>The result of attempting to reserve candidate resources.</summary>
public sealed record BudgetAdmission(bool Allowed, string? Reason, AutomaticIndexUsage Usage);

/// <summary>Tracks tenant and collection reservations without owning storage.</summary>
public interface IAutomaticIndexBudgetLedger
{
    /// <summary>Attempts an atomic reservation for a candidate.</summary>
    BudgetAdmission TryReserve(AutomaticIndexCandidate candidate, AutomaticIndexCost cost, bool visible);

    /// <summary>Releases a previous reservation.</summary>
    void Release(AutomaticIndexCandidate candidate, AutomaticIndexCost cost, bool visible);

    /// <summary>Returns current collection usage.</summary>
    AutomaticIndexUsage Usage(AutomaticIndexScope scope);
}

/// <summary>One durable lifecycle transition written by the catalog adapter.</summary>
public sealed record AutomaticIndexTransition(
    string CandidateId,
    AutomaticIndexPhase From,
    AutomaticIndexPhase To,
    ulong Generation,
    ulong? AppliedPosition,
    ulong? TargetPosition,
    DateTimeOffset At,
    string Reason);

/// <summary>A physical index adapter owned by the storage engine.</summary>
public interface IAutomaticIndexBackend
{
    /// <summary>Builds an invisible physical index at a snapshot target.</summary>
    ValueTask BuildInvisibleAsync(AutomaticIndexCandidate candidate, ulong targetPosition, CancellationToken cancellationToken = default);

    /// <summary>Backfills a contiguous sequence range into an invisible index.</summary>
    ValueTask BackfillAsync(string candidateId, ulong firstPosition, ulong lastPosition, CancellationToken cancellationToken = default);

    /// <summary>Catches up and verifies the final tail in the write publication path.</summary>
    ValueTask<ulong> CatchUpAndVerifyAsync(string candidateId, ulong appliedPosition, ulong targetPosition, CancellationToken cancellationToken = default);

    /// <summary>Changes planner visibility for an already-built index.</summary>
    ValueTask SetVisibilityAsync(string candidateId, bool visible, CancellationToken cancellationToken = default);

    /// <summary>Drops physical pages after the invisible grace period.</summary>
    ValueTask DropPhysicalAsync(string candidateId, CancellationToken cancellationToken = default);

    /// <summary>Installs or removes a plan pin for an exact query template.</summary>
    ValueTask SetPlanPinAsync(string templateKey, string candidateId, bool pinned, CancellationToken cancellationToken = default);
}

/// <summary>A journal adapter that makes lifecycle transitions durable.</summary>
public interface IAutomaticIndexLifecycleStore
{
    /// <summary>Appends a transition before it is exposed by the coordinator.</summary>
    ValueTask AppendAsync(AutomaticIndexTransition transition, CancellationToken cancellationToken = default);
}

/// <summary>An in-memory lifecycle journal useful for tests and local adapters.</summary>
public sealed class InMemoryAutomaticIndexLifecycleStore : IAutomaticIndexLifecycleStore
{
    private readonly List<AutomaticIndexTransition> transitions = [];

    /// <summary>The transitions in append order.</summary>
    public IReadOnlyList<AutomaticIndexTransition> Transitions
    {
        get
        {
            lock (transitions)
            {
                return transitions.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(AutomaticIndexTransition transition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (transitions)
        {
            transitions.Add(transition);
        }

        return ValueTask.CompletedTask;
    }
}
