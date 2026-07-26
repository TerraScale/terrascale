using System.Collections.Frozen;
using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// An immutable query value visible to a Sync client.
/// </summary>
public sealed class TerraScaleSyncQuerySnapshot
{
    internal TerraScaleSyncQuerySnapshot(
        TerraScaleSyncId queryId,
        string? queryFingerprint,
        JsonElement? result,
        string? resultHash,
        TerraScaleSyncDatabaseRevision? executionRevision,
        TerraScaleSyncDatabaseRevision validatedThroughRevision,
        JsonElement? journal,
        TerraScaleSyncError? error,
        bool optimistic) : this(
            queryId,
            queryFingerprint,
            result,
            resultHash,
            executionRevision,
            validatedThroughRevision,
            journal,
            error,
            optimistic,
            cloneJson: true)
    {
    }

    private TerraScaleSyncQuerySnapshot(
        TerraScaleSyncId queryId,
        string? queryFingerprint,
        JsonElement? result,
        string? resultHash,
        TerraScaleSyncDatabaseRevision? executionRevision,
        TerraScaleSyncDatabaseRevision validatedThroughRevision,
        JsonElement? journal,
        TerraScaleSyncError? error,
        bool optimistic,
        bool cloneJson)
    {
        QueryId = queryId;
        QueryFingerprint = queryFingerprint;
        Result = cloneJson ? Clone(result) : result;
        ResultHash = resultHash;
        ExecutionRevision = executionRevision;
        ValidatedThroughRevision = validatedThroughRevision;
        Journal = cloneJson ? Clone(journal) : journal;
        Error = error;
        Optimistic = optimistic;
    }

    /// <summary>The stable query identifier.</summary>
    public TerraScaleSyncId QueryId { get; }

    /// <summary>The server query fingerprint, when known.</summary>
    public string? QueryFingerprint { get; }

    /// <summary>The current JSON result, when successful.</summary>
    public JsonElement? Result { get; }

    /// <summary>The server result hash, when known.</summary>
    public string? ResultHash { get; }

    /// <summary>The revision at which the query was executed, when known.</summary>
    public TerraScaleSyncDatabaseRevision? ExecutionRevision { get; }

    /// <summary>The revision through which the result or failure is valid.</summary>
    public TerraScaleSyncDatabaseRevision ValidatedThroughRevision { get; }

    /// <summary>The opaque server journal, when present.</summary>
    public JsonElement? Journal { get; }

    /// <summary>The current query error, when failed.</summary>
    public TerraScaleSyncError? Error { get; }

    /// <summary>Whether one or more local optimistic layers affect the result.</summary>
    public bool Optimistic { get; }

    internal TerraScaleSyncQuerySnapshot ValidatedThrough(
        TerraScaleSyncDatabaseRevision revision) =>
        new(
            QueryId,
            QueryFingerprint,
            Result,
            ResultHash,
            ExecutionRevision,
            revision,
            Journal,
            Error,
            Optimistic,
            cloneJson: false);

    internal TerraScaleSyncQuerySnapshot WithOptimisticResult(JsonElement result) =>
        new(
            QueryId,
            QueryFingerprint,
            result,
            ResultHash,
            ExecutionRevision,
            ValidatedThroughRevision,
            Journal,
            Error,
            optimistic: true,
            cloneJson: false);

    internal static TerraScaleSyncQuerySnapshot OptimisticOnly(
        TerraScaleSyncId queryId,
        JsonElement result,
        TerraScaleSyncDatabaseRevision revision) =>
        new(
            queryId,
            queryFingerprint: null,
            result,
            resultHash: null,
            executionRevision: null,
            revision,
            journal: null,
            error: null,
            optimistic: true,
            cloneJson: false);

    private static JsonElement? Clone(JsonElement? value) =>
        value is { } element ? element.Clone() : null;
}

/// <summary>
/// A local optimistic result transformation for one active query.
/// </summary>
/// <param name="QueryId">The query to transform.</param>
/// <param name="Apply">A pure transformation of the current JSON result.</param>
public sealed record TerraScaleSyncOptimisticPatch(
    TerraScaleSyncId QueryId,
    Func<JsonElement?, JsonElement> Apply);

/// <summary>
/// An immutable TerraScale Sync client state.
/// </summary>
public sealed class TerraScaleSyncClientState
{
    private readonly OptimisticLayer[] _optimisticLayers;

    private TerraScaleSyncClientState(
        TerraScaleSyncStateVersion version,
        FrozenDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot> authoritativeQueries,
        FrozenDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot> queries,
        string? ingressRegion,
        string? servingRegion,
        string? databaseRegion,
        TerraScaleSyncUInt64 routeGeneration,
        OptimisticLayer[] optimisticLayers)
    {
        Version = version;
        AuthoritativeQueries = authoritativeQueries;
        Queries = queries;
        IngressRegion = ingressRegion;
        ServingRegion = servingRegion;
        DatabaseRegion = databaseRegion;
        RouteGeneration = routeGeneration;
        _optimisticLayers = optimisticLayers;
    }

    /// <summary>
    /// An empty initial state.
    /// </summary>
    public static TerraScaleSyncClientState Empty { get; } =
        new(
            TerraScaleSyncStateVersion.Zero,
            FrozenDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot>.Empty,
            FrozenDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot>.Empty,
            ingressRegion: null,
            servingRegion: null,
            databaseRegion: null,
            TerraScaleSyncUInt64.Zero,
            []);

    /// <summary>The last atomically applied server version.</summary>
    public TerraScaleSyncStateVersion Version { get; }

    /// <summary>The immutable server-confirmed query state.</summary>
    public FrozenDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot> AuthoritativeQueries { get; }

    /// <summary>The immutable observable state after ordered optimistic replay.</summary>
    public FrozenDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot> Queries { get; }

    /// <summary>The public ingress region reported by the last transition.</summary>
    public string? IngressRegion { get; }

    /// <summary>The serving Platform region reported by the last transition.</summary>
    public string? ServingRegion { get; }

    /// <summary>The database authority region reported by the last transition.</summary>
    public string? DatabaseRegion { get; }

    /// <summary>The current route generation.</summary>
    public TerraScaleSyncUInt64 RouteGeneration { get; }

    internal IReadOnlyList<OptimisticLayer> OptimisticLayers => _optimisticLayers;

    internal static TerraScaleSyncClientState Create(
        TerraScaleSyncStateVersion version,
        IReadOnlyDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot> authoritativeQueries,
        string? ingressRegion,
        string? servingRegion,
        string? databaseRegion,
        TerraScaleSyncUInt64 routeGeneration,
        OptimisticLayer[] optimisticLayers)
    {
        var authoritative = authoritativeQueries.ToFrozenDictionary();
        var observable = Replay(authoritative, version.DatabaseRevision, optimisticLayers);
        return new(
            version,
            authoritative,
            observable,
            ingressRegion,
            servingRegion,
            databaseRegion,
            routeGeneration,
            optimisticLayers);
    }

    private static FrozenDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot> Replay(
        FrozenDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot> authoritative,
        TerraScaleSyncDatabaseRevision revision,
        IReadOnlyList<OptimisticLayer> layers)
    {
        if (layers.Count == 0)
        {
            return authoritative;
        }

        var queries = new Dictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot>(authoritative);
        foreach (var layer in layers)
        {
            foreach (var patch in layer.Patches)
            {
                queries.TryGetValue(patch.QueryId, out var current);
                var optimisticResult = patch.Apply(current?.Result);
                if (optimisticResult.ValueKind == JsonValueKind.Undefined)
                {
                    throw new InvalidOperationException(
                        "An optimistic patch must return a defined JSON value.");
                }

                var result = optimisticResult.Clone();
                queries[patch.QueryId] = current is null
                    ? TerraScaleSyncQuerySnapshot.OptimisticOnly(patch.QueryId, result, revision)
                    : current.WithOptimisticResult(result);
            }
        }

        return queries.ToFrozenDictionary();
    }

    internal sealed record OptimisticLayer(
        TerraScaleSyncId MutationId,
        TerraScaleSyncOptimisticPatch[] Patches,
        TerraScaleSyncDatabaseRevision? CommittedRevision);
}

/// <summary>
/// Applies validated server transitions and local optimistic mutation outcomes without mutating prior state.
/// </summary>
public static class TerraScaleSyncStateReducer
{
    private static readonly FrozenSet<TerraScaleSyncId> NoChangedQueries =
        FrozenSet<TerraScaleSyncId>.Empty;

    /// <summary>
    /// Atomically applies one complete server transition.
    /// </summary>
    public static bool TryApply(
        TerraScaleSyncClientState current,
        TerraScaleSyncTransition transition,
        out TerraScaleSyncClientState next,
        out IReadOnlySet<TerraScaleSyncId> changedQueryIds,
        out TerraScaleSyncValidationError? error)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(transition);

        error = Validate(transition);
        if (error is not null)
        {
            next = current;
            changedQueryIds = NoChangedQueries;
            return false;
        }

        if (transition.StartVersion != current.Version)
        {
            next = current;
            changedQueryIds = NoChangedQueries;
            error = new(
                "StartVersionMismatch",
                "Transition StartVersion must exactly equal the current client version.");
            return false;
        }

        var touched = new HashSet<TerraScaleSyncId>(
            transition.QueryUpdated.Select(static update => update.QueryId));
        touched.UnionWith(transition.QueryFailed.Select(static failure => failure.QueryId));
        touched.UnionWith(transition.QueryRemoved.Select(static removal => removal.QueryId));

        if (transition.EndVersion.IdentityVersion > current.Version.IdentityVersion &&
            current.AuthoritativeQueries.Keys.Any(queryId => !touched.Contains(queryId)))
        {
            next = current;
            changedQueryIds = NoChangedQueries;
            error = new(
                "IdentityTransitionIncomplete",
                "An identity transition must replace, fail, or remove every existing query.");
            return false;
        }

        var queries = new Dictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot>(
            current.AuthoritativeQueries.Count);
        foreach (var (queryId, snapshot) in current.AuthoritativeQueries)
        {
            if (!touched.Contains(queryId))
            {
                queries.Add(
                    queryId,
                    snapshot.ValidatedThrough(transition.EndVersion.DatabaseRevision));
            }
        }

        foreach (var update in transition.QueryUpdated)
        {
            queries.Add(
                update.QueryId,
                new TerraScaleSyncQuerySnapshot(
                    update.QueryId,
                    update.QueryFingerprint,
                    update.Result,
                    update.ResultHash,
                    update.ExecutionRevision,
                    update.ValidatedThroughRevision,
                    update.Journal,
                    error: null,
                    optimistic: false));
        }

        foreach (var failure in transition.QueryFailed)
        {
            current.AuthoritativeQueries.TryGetValue(failure.QueryId, out var previous);
            queries.Add(
                failure.QueryId,
                new TerraScaleSyncQuerySnapshot(
                    failure.QueryId,
                    previous?.QueryFingerprint,
                    result: null,
                    previous?.ResultHash,
                    previous?.ExecutionRevision,
                    failure.ValidatedThroughRevision,
                    previous?.Journal,
                    failure.Error,
                    optimistic: false));
        }

        var layers = current.OptimisticLayers
            .Where(layer =>
                layer.CommittedRevision is null ||
                transition.EndVersion.DatabaseRevision < layer.CommittedRevision.Value)
            .ToArray();
        next = TerraScaleSyncClientState.Create(
            transition.EndVersion,
            queries,
            transition.IngressRegion,
            transition.ServingRegion,
            transition.DatabaseRegion,
            transition.RouteGeneration,
            layers);
        changedQueryIds = Changed(current.Queries, next.Queries);
        error = null;
        return true;
    }

    /// <summary>
    /// Adds an ordered optimistic mutation layer and replays all layers over authoritative state.
    /// </summary>
    public static TerraScaleSyncClientState BeginOptimisticMutation(
        TerraScaleSyncClientState current,
        TerraScaleSyncId mutationId,
        IReadOnlyList<TerraScaleSyncOptimisticPatch> patches,
        out IReadOnlySet<TerraScaleSyncId> changedQueryIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(patches);
        if (mutationId.IsEmpty)
        {
            throw new ArgumentException("MutationId must be a non-empty ULID.", nameof(mutationId));
        }

        if (current.OptimisticLayers.Any(layer => layer.MutationId == mutationId))
        {
            throw new ArgumentException(
                $"Optimistic mutation '{mutationId}' already exists.",
                nameof(mutationId));
        }

        if (current.OptimisticLayers.Count >= 100)
        {
            throw new InvalidOperationException(
                "A Sync session cannot retain more than 100 optimistic mutations.");
        }

        var layerPatches = patches.ToArray();
        if (layerPatches.Length == 0 ||
            layerPatches.Any(static patch => patch is null || patch.QueryId.IsEmpty || patch.Apply is null))
        {
            throw new ArgumentException(
                "An optimistic mutation requires valid query patches.",
                nameof(patches));
        }

        var layers = new TerraScaleSyncClientState.OptimisticLayer[
            current.OptimisticLayers.Count + 1];
        for (var index = 0; index < current.OptimisticLayers.Count; index++)
        {
            layers[index] = current.OptimisticLayers[index];
        }

        layers[^1] = new(mutationId, layerPatches, CommittedRevision: null);
        var next = TerraScaleSyncClientState.Create(
            current.Version,
            current.AuthoritativeQueries,
            current.IngressRegion,
            current.ServingRegion,
            current.DatabaseRegion,
            current.RouteGeneration,
            layers);
        changedQueryIds = Changed(current.Queries, next.Queries);
        return next;
    }

    /// <summary>
    /// Records a mutation's commit revision while retaining its optimistic layer until authoritative
    /// state reaches that revision.
    /// </summary>
    public static TerraScaleSyncClientState MarkMutationCommitted(
        TerraScaleSyncClientState current,
        TerraScaleSyncId mutationId,
        TerraScaleSyncDatabaseRevision committedRevision,
        out IReadOnlySet<TerraScaleSyncId> changedQueryIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        var index = -1;
        for (var candidate = 0; candidate < current.OptimisticLayers.Count; candidate++)
        {
            if (current.OptimisticLayers[candidate].MutationId == mutationId)
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            throw new ArgumentException(
                $"Optimistic mutation '{mutationId}' does not exist.",
                nameof(mutationId));
        }

        var layers = current.OptimisticLayers
            .Select((layer, candidate) =>
                candidate == index ? layer with { CommittedRevision = committedRevision } : layer)
            .Where(layer =>
                layer.CommittedRevision is null ||
                current.Version.DatabaseRevision < layer.CommittedRevision.Value)
            .ToArray();
        var next = TerraScaleSyncClientState.Create(
            current.Version,
            current.AuthoritativeQueries,
            current.IngressRegion,
            current.ServingRegion,
            current.DatabaseRegion,
            current.RouteGeneration,
            layers);
        changedQueryIds = layers.Length == current.OptimisticLayers.Count
            ? NoChangedQueries
            : Changed(current.Queries, next.Queries);
        return next;
    }

    /// <summary>
    /// Removes a failed optimistic mutation and replays every later layer in original order.
    /// </summary>
    public static TerraScaleSyncClientState RollbackMutation(
        TerraScaleSyncClientState current,
        TerraScaleSyncId mutationId,
        out IReadOnlySet<TerraScaleSyncId> changedQueryIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        var layers = current.OptimisticLayers
            .Where(layer => layer.MutationId != mutationId)
            .ToArray();
        if (layers.Length == current.OptimisticLayers.Count)
        {
            changedQueryIds = NoChangedQueries;
            return current;
        }

        var next = TerraScaleSyncClientState.Create(
            current.Version,
            current.AuthoritativeQueries,
            current.IngressRegion,
            current.ServingRegion,
            current.DatabaseRegion,
            current.RouteGeneration,
            layers);
        changedQueryIds = Changed(current.Queries, next.Queries);
        return next;
    }

    internal static TerraScaleSyncClientState PrepareForResync(
        TerraScaleSyncClientState current,
        IReadOnlySet<TerraScaleSyncId> activeQueryIds)
    {
        var retained = current.AuthoritativeQueries
            .Where(pair => activeQueryIds.Contains(pair.Key))
            .ToDictionary();
        var layers = current.OptimisticLayers
            .Select(layer => layer with
            {
                Patches = layer.Patches
                    .Where(patch => activeQueryIds.Contains(patch.QueryId))
                    .ToArray()
            })
            .Where(static layer => layer.Patches.Length > 0)
            .ToArray();
        return TerraScaleSyncClientState.Create(
            TerraScaleSyncStateVersion.Zero,
            retained,
            current.IngressRegion,
            current.ServingRegion,
            current.DatabaseRegion,
            current.RouteGeneration,
            layers);
    }

    private static TerraScaleSyncValidationError? Validate(TerraScaleSyncTransition transition)
    {
        if (transition.TransitionId.IsEmpty)
        {
            return new("InvalidTransition", "TransitionId must be a non-empty ULID.");
        }

        if (transition.QueryUpdated is null ||
            transition.QueryFailed is null ||
            transition.QueryRemoved is null)
        {
            return new("MissingCollection", "Every transition query array is required.");
        }

        if (!transition.EndVersion.IsAtLeast(transition.StartVersion) ||
            transition.EndVersion == transition.StartVersion)
        {
            return new(
                "InvalidEndVersion",
                "EndVersion must strictly advance without regressing any component.");
        }

        if (transition.QueryUpdated.Count +
            transition.QueryFailed.Count +
            transition.QueryRemoved.Count > 100)
        {
            return new("QueryLimitExceeded", "A transition cannot touch more than 100 queries.");
        }

        var queryIds = new HashSet<TerraScaleSyncId>();
        foreach (var update in transition.QueryUpdated)
        {
            if (update is null || update.QueryId.IsEmpty || !queryIds.Add(update.QueryId))
            {
                return new("DuplicateQueryId", "Transition query IDs must be non-empty and unique.");
            }

            if (!IsSha256(update.QueryFingerprint) || !IsSha256(update.ResultHash))
            {
                return new(
                    "InvalidHash",
                    "QueryFingerprint and ResultHash must be lowercase SHA-256 hex.");
            }

            if (update.Result.ValueKind == JsonValueKind.Undefined ||
                update.Journal is { ValueKind: JsonValueKind.Undefined })
            {
                return new("InvalidJsonValue", "Result and Journal must contain defined JSON values.");
            }

            if (update.ExecutionRevision > update.ValidatedThroughRevision ||
                update.ValidatedThroughRevision != transition.EndVersion.DatabaseRevision)
            {
                return new(
                    "InvalidQueryRevision",
                    "Every updated query must be valid through the transition database revision.");
            }
        }

        foreach (var failure in transition.QueryFailed)
        {
            if (failure is null || failure.QueryId.IsEmpty || !queryIds.Add(failure.QueryId))
            {
                return new("DuplicateQueryId", "Transition query IDs must be non-empty and unique.");
            }

            if (failure.ValidatedThroughRevision != transition.EndVersion.DatabaseRevision)
            {
                return new(
                    "InvalidQueryRevision",
                    "Every failed query must be valid through the transition database revision.");
            }

            if (!IsValidError(failure.Error))
            {
                return new("InvalidError", "The query error is invalid.");
            }
        }

        foreach (var removal in transition.QueryRemoved)
        {
            if (removal is null || removal.QueryId.IsEmpty || !queryIds.Add(removal.QueryId))
            {
                return new("DuplicateQueryId", "Transition query IDs must be non-empty and unique.");
            }
        }

        if (transition.ServerTiming is { } timing &&
            (timing.QueryMilliseconds is < 0 or > 60_000 ||
             timing.ValidationMilliseconds is < 0 or > 60_000 ||
             timing.SerializationMilliseconds is < 0 or > 60_000))
        {
            return new(
                "InvalidServerTiming",
                "Server timing values must be between 0 and 60000 milliseconds.");
        }

        return IsRegion(transition.IngressRegion) &&
               IsRegion(transition.ServingRegion) &&
               IsRegion(transition.DatabaseRegion)
            ? null
            : new(
                "InvalidRegion",
                "Regions must be 1-32 lowercase ASCII letters, digits, or internal hyphens.");
    }

    private static IReadOnlySet<TerraScaleSyncId> Changed(
        IReadOnlyDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot> current,
        IReadOnlyDictionary<TerraScaleSyncId, TerraScaleSyncQuerySnapshot> next)
    {
        var changed = new HashSet<TerraScaleSyncId>();
        foreach (var queryId in current.Keys.Concat(next.Keys))
        {
            current.TryGetValue(queryId, out var currentSnapshot);
            next.TryGetValue(queryId, out var nextSnapshot);
            if (!SameObservable(currentSnapshot, nextSnapshot))
            {
                changed.Add(queryId);
            }
        }

        return changed.Count == 0 ? NoChangedQueries : changed.ToFrozenSet();
    }

    private static bool SameObservable(
        TerraScaleSyncQuerySnapshot? current,
        TerraScaleSyncQuerySnapshot? next)
    {
        if (ReferenceEquals(current, next))
        {
            return true;
        }

        if (current is null || next is null ||
            current.Error != next.Error ||
            current.Optimistic != next.Optimistic ||
            current.Result is null != (next.Result is null))
        {
            return false;
        }

        if (current.Result is null)
        {
            return true;
        }

        return JsonElement.DeepEquals(current.Result.Value, next.Result!.Value);
    }

    private static bool IsValidError(TerraScaleSyncError? error) =>
        error is not null &&
        !string.IsNullOrWhiteSpace(error.Code) &&
        error.Code.Length <= 128 &&
        error.Code.All(static character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-') &&
        !string.IsNullOrWhiteSpace(error.Message) &&
        System.Text.Encoding.UTF8.GetByteCount(error.Message) <= 4 * 1024;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsRegion(string? region) =>
        region is { Length: >= 1 and <= 32 } &&
        region[0] != '-' &&
        region[^1] != '-' &&
        region.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
