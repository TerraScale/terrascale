using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraBase.Replication;

/// <summary>How a register resolves two concurrent candidates.</summary>
public enum RegisterMergePolicy
{
    /// <summary>A concurrent removal takes precedence over an assignment.</summary>
    RemoveWins,

    /// <summary>A concurrent assignment takes precedence over a removal.</summary>
    AddWins,

    /// <summary>The candidate with the greatest deterministic timestamp wins.</summary>
    LastWriteWins,
}

/// <summary>A dotted causal version used by the native replication kernels.</summary>
public sealed record CausalVersion
{
    public CausalVersion(ReplicationDot dot, IReadOnlyDictionary<string, ulong>? context = null)
    {
        if (string.IsNullOrWhiteSpace(dot.Origin) || dot.Sequence == 0)
            throw new ArgumentException("A causal dot must have a non-empty origin and non-zero sequence.", nameof(dot));

        Dot = dot;
        Context = CopyContext(context);
    }

    public ReplicationDot Dot { get; }

    public IReadOnlyDictionary<string, ulong> Context { get; }

    /// <summary>Returns true when this version causally follows <paramref name="other"/>.</summary>
    public bool HappensAfter(CausalVersion other) => Contains(Context, other.Dot);

    /// <summary>Returns true when neither version causally follows the other.</summary>
    public bool IsConcurrentWith(CausalVersion other) =>
        Dot != other.Dot && !HappensAfter(other) && !other.HappensAfter(this);

    /// <summary>Returns a normalized union of two causal contexts and their dots.</summary>
    public static IReadOnlyDictionary<string, ulong> MergeContext(CausalVersion left, CausalVersion right)
    {
        var context = CopyContext(left.Context);
        MergeInto(context, right.Context);
        Advance(context, left.Dot);
        Advance(context, right.Dot);
        return new ReadOnlyDictionary<string, ulong>(context);
    }

    internal static Dictionary<string, ulong> CopyContext(IReadOnlyDictionary<string, ulong>? context)
    {
        var copy = new Dictionary<string, ulong>(StringComparer.Ordinal);
        if (context is null)
            return copy;

        foreach (var pair in context)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == 0)
                throw new ArgumentException("Causal contexts may only contain non-empty origins and non-zero counters.", nameof(context));
            copy[pair.Key] = Math.Max(copy.GetValueOrDefault(pair.Key), pair.Value);
        }

        return copy;
    }

    internal static void MergeInto(IDictionary<string, ulong> target, IReadOnlyDictionary<string, ulong> source)
    {
        foreach (var pair in source)
            target[pair.Key] = Math.Max(target.GetValueOrDefault(pair.Key), pair.Value);
    }

    internal static void Advance(IDictionary<string, ulong> target, ReplicationDot dot) =>
        target[dot.Origin] = Math.Max(target.GetValueOrDefault(dot.Origin), dot.Sequence);

    internal static bool Contains(IReadOnlyDictionary<string, ulong> context, ReplicationDot dot) =>
        context.TryGetValue(dot.Origin, out var sequence) && sequence >= dot.Sequence;
}

/// <summary>A deterministic candidate in a register or set lattice.</summary>
public sealed record CrdtCandidate<T>(
    T? Value,
    ReplicationDot Dot,
    IReadOnlyDictionary<string, ulong> Context,
    long Hlc,
    bool Deleted = false)
{
    public CausalVersion Version => new(Dot, Context);
}

/// <summary>
/// A last-write register with causal dominance before the deterministic HLC
/// tie-break. The full causal context is retained when a losing candidate is
/// discarded so that it cannot be resurrected by replay.
/// </summary>
public sealed class LwwRegister<T>
{
    private readonly CrdtCandidate<T> candidate;

    public LwwRegister(CrdtCandidate<T> candidate, RegisterMergePolicy policy = RegisterMergePolicy.RemoveWins)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate = candidate with { Context = CausalVersion.CopyContext(candidate.Context) };
        _ = candidate.Version;
        this.candidate = candidate;
        Policy = policy;
    }

    public RegisterMergePolicy Policy { get; }

    public CrdtCandidate<T> Candidate => candidate;

    public T? Value => candidate.Deleted ? default : candidate.Value;

    public bool IsDeleted => candidate.Deleted;

    public IReadOnlyDictionary<string, ulong> Context => candidate.Context;

    /// <summary>Creates an assignment with a fresh dotted version.</summary>
    public static LwwRegister<T> Assign(
        T value,
        ReplicationDot dot,
        IReadOnlyDictionary<string, ulong>? context,
        long hlc,
        RegisterMergePolicy policy = RegisterMergePolicy.RemoveWins) =>
        new(new CrdtCandidate<T>(value, dot, CausalVersion.CopyContext(context), hlc), policy);

    /// <summary>Creates a removal candidate.</summary>
    public static LwwRegister<T> Remove(
        ReplicationDot dot,
        IReadOnlyDictionary<string, ulong>? context,
        long hlc,
        RegisterMergePolicy policy = RegisterMergePolicy.RemoveWins) =>
        new(new CrdtCandidate<T>(default, dot, CausalVersion.CopyContext(context), hlc, true), policy);

    /// <summary>Joins two register states. Join is associative, commutative, and idempotent.</summary>
    public LwwRegister<T> Join(LwwRegister<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var mergedContext = CausalVersion.MergeContext(candidate.Version, other.candidate.Version);
        var winner = Select(candidate, other.candidate, Policy);
        return new(winner with { Context = mergedContext }, Policy);
    }

    private static CrdtCandidate<T> Select(
        CrdtCandidate<T> left,
        CrdtCandidate<T> right,
        RegisterMergePolicy policy)
    {
        var leftVersion = left.Version;
        var rightVersion = right.Version;
        if (CausalVersion.Contains(right.Context, left.Dot))
            return right;
        if (CausalVersion.Contains(left.Context, right.Dot))
            return left;

        if (left.Deleted != right.Deleted)
        {
            if (policy == RegisterMergePolicy.RemoveWins)
                return left.Deleted ? left : right;
            if (policy == RegisterMergePolicy.AddWins)
                return left.Deleted ? right : left;
        }

        return Compare(left, right) >= 0 ? left : right;
    }

    internal static int Compare(CrdtCandidate<T> left, CrdtCandidate<T> right) =>
        left.Hlc != right.Hlc
            ? left.Hlc.CompareTo(right.Hlc)
            : string.CompareOrdinal(left.Dot.Origin, right.Dot.Origin) != 0
                ? string.CompareOrdinal(left.Dot.Origin, right.Dot.Origin)
                : left.Dot.Sequence.CompareTo(right.Dot.Sequence);
}

/// <summary>A multi-value register retaining every causally maximal candidate.</summary>
public sealed class MvRegister<T>
{
    private readonly IReadOnlyList<CrdtCandidate<T>> candidates;

    public MvRegister(IEnumerable<CrdtCandidate<T>> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var values = candidates.ToArray();
        if (values.Length == 0)
            throw new ArgumentException("A multi-value register requires at least one candidate.", nameof(candidates));
        foreach (var value in values)
            _ = value.Version;
        this.candidates = values
            .GroupBy(value => value.Dot)
            .Select(group => group.OrderByDescending(value => value.Hlc).ThenByDescending(value => value.Dot.Origin, StringComparer.Ordinal).First())
            .OrderBy(value => value.Dot.Origin, StringComparer.Ordinal)
            .ThenBy(value => value.Dot.Sequence)
            .ToArray();
        Context = BuildContext(this.candidates);
    }

    public IReadOnlyList<CrdtCandidate<T>> Candidates => candidates;

    public IReadOnlyDictionary<string, ulong> Context { get; }

    public IReadOnlyList<T?> Values => candidates.Where(value => !value.Deleted).Select(value => value.Value).ToArray();

    /// <summary>Joins the causally maximal candidates from both registers.</summary>
    public MvRegister<T> Join(MvRegister<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var context = CausalVersion.CopyContext(Context);
        CausalVersion.MergeInto(context, other.Context);
        var all = candidates.Concat(other.candidates).GroupBy(value => value.Dot).Select(group => group.First());
        var survivors = all.Where(candidate =>
            !IsDominated(candidate, other.Context) && !IsDominated(candidate, Context)).ToArray();
        if (survivors.Length == 0)
            survivors = [candidates.OrderByDescending(value => value.Hlc).First()];
        var result = new MvRegister<T>(survivors);
        return result with { };
    }

    private static bool IsDominated(CrdtCandidate<T> candidate, IReadOnlyDictionary<string, ulong> context) =>
        CausalVersion.Contains(context, candidate.Dot);

    private static IReadOnlyDictionary<string, ulong> BuildContext(IEnumerable<CrdtCandidate<T>> values)
    {
        var context = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            CausalVersion.MergeInto(context, value.Context);
            CausalVersion.Advance(context, value.Dot);
        }
        return new ReadOnlyDictionary<string, ulong>(context);
    }
}

/// <summary>A grow-only positive/negative counter lattice.</summary>
public sealed class PnCounter
{
    private readonly IReadOnlyDictionary<string, (long Positive, long Negative)> components;

    public PnCounter(IReadOnlyDictionary<string, (long Positive, long Negative)>? components = null)
    {
        var values = new Dictionary<string, (long Positive, long Negative)>(StringComparer.Ordinal);
        if (components is not null)
        {
            foreach (var pair in components)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value.Positive < 0 || pair.Value.Negative < 0)
                    throw new ArgumentException("Counter actors and components must be valid non-negative values.", nameof(components));
                values[pair.Key] = pair.Value;
            }
        }
        this.components = new ReadOnlyDictionary<string, (long Positive, long Negative)>(values);
    }

    public IReadOnlyDictionary<string, (long Positive, long Negative)> Components => components;

    public long Value => components.Values.Aggregate(0L, (sum, component) => checked(sum + component.Positive - component.Negative));

    /// <summary>Applies an absolute actor component, making retries idempotent.</summary>
    public PnCounter SetComponent(string actor, long positive, long negative)
    {
        if (string.IsNullOrWhiteSpace(actor) || positive < 0 || negative < 0)
            throw new ArgumentOutOfRangeException(nameof(actor));
        var next = new Dictionary<string, (long Positive, long Negative)>(components, StringComparer.Ordinal)
        {
            [actor] = (Math.Max(components.GetValueOrDefault(actor).Positive, positive), Math.Max(components.GetValueOrDefault(actor).Negative, negative)),
        };
        return new(next);
    }

    public PnCounter Increment(string actor, long amount = 1)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var existing = components.GetValueOrDefault(actor);
        return SetComponent(actor, checked(existing.Positive + amount), existing.Negative);
    }

    public PnCounter Decrement(string actor, long amount = 1)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var existing = components.GetValueOrDefault(actor);
        return SetComponent(actor, existing.Positive, checked(existing.Negative + amount));
    }

    /// <summary>Joins by taking the maximum absolute component per actor.</summary>
    public PnCounter Join(PnCounter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var merged = new Dictionary<string, (long Positive, long Negative)>(components, StringComparer.Ordinal);
        foreach (var pair in other.components)
        {
            var current = merged.GetValueOrDefault(pair.Key);
            merged[pair.Key] = (Math.Max(current.Positive, pair.Value.Positive), Math.Max(current.Negative, pair.Value.Negative));
        }
        return new(merged);
    }
}

/// <summary>A tagged add-wins observed-remove set.</summary>
public sealed class AddWinsSet<T>
{
    private readonly IReadOnlyList<CrdtCandidate<T>> adds;

    public AddWinsSet(IEnumerable<CrdtCandidate<T>>? adds = null, IReadOnlyDictionary<string, ulong>? context = null)
    {
        this.adds = (adds ?? []).GroupBy(add => add.Dot).Select(group => group.First()).ToArray();
        Context = new ReadOnlyDictionary<string, ulong>(CausalVersion.CopyContext(context));
    }

    public IReadOnlyList<CrdtCandidate<T>> Adds => adds;

    public IReadOnlyDictionary<string, ulong> Context { get; }

    public IReadOnlyCollection<T> Values => adds
        .Where(add => !CausalVersion.Contains(Context, add.Dot))
        .Select(add => add.Value)
        .Where(value => value is not null)
        .Cast<T>()
        .Distinct()
        .ToArray();

    public AddWinsSet<T> Add(T value, ReplicationDot dot, IReadOnlyDictionary<string, ulong>? context)
    {
        var next = adds.Concat([new CrdtCandidate<T>(value, dot, CausalVersion.CopyContext(context), 0)]);
        return new(next, Context);
    }

    /// <summary>Removes only tags observed by the caller; concurrent adds survive.</summary>
    public AddWinsSet<T> Remove(T value)
    {
        var context = CausalVersion.CopyContext(Context);
        foreach (var add in adds.Where(add => EqualityComparer<T>.Default.Equals(add.Value, value)))
            CausalVersion.Advance(context, add.Dot);
        return new(adds, context);
    }

    public AddWinsSet<T> Join(AddWinsSet<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var context = CausalVersion.CopyContext(Context);
        CausalVersion.MergeInto(context, other.Context);
        var merged = adds.Concat(other.adds).GroupBy(add => add.Dot).Select(group => group.First()).ToArray();
        return new(merged, context);
    }
}

/// <summary>A remove-wins set whose remove event defeats concurrent additions.</summary>
public sealed class RemoveWinsSet<T>
{
    private readonly IReadOnlyList<CrdtCandidate<T>> adds;
    private readonly IReadOnlyList<CrdtCandidate<T>> removes;

    public RemoveWinsSet(
        IEnumerable<CrdtCandidate<T>>? adds = null,
        IEnumerable<CrdtCandidate<T>>? removes = null,
        IReadOnlyDictionary<string, ulong>? context = null)
    {
        this.adds = (adds ?? []).GroupBy(value => value.Dot).Select(group => group.First()).ToArray();
        this.removes = (removes ?? []).GroupBy(value => value.Dot).Select(group => group.First()).ToArray();
        Context = new ReadOnlyDictionary<string, ulong>(CausalVersion.CopyContext(context));
    }

    public IReadOnlyList<CrdtCandidate<T>> Adds => adds;

    public IReadOnlyList<CrdtCandidate<T>> Removes => removes;

    public IReadOnlyDictionary<string, ulong> Context { get; }

    public IReadOnlyCollection<T> Values => adds
        .Where(add => removes.All(remove =>
            !EqualityComparer<T>.Default.Equals(add.Value, remove.Value)
            || CausalVersion.Contains(add.Context, remove.Dot)))
        .Select(add => add.Value)
        .Where(value => value is not null)
        .Cast<T>()
        .Distinct()
        .ToArray();

    public RemoveWinsSet<T> Add(T value, ReplicationDot dot, IReadOnlyDictionary<string, ulong>? context) =>
        new(adds.Concat([new CrdtCandidate<T>(value, dot, CausalVersion.CopyContext(context), 0)]), removes, Context);

    public RemoveWinsSet<T> Remove(T value, ReplicationDot dot, IReadOnlyDictionary<string, ulong>? context) =>
        new(adds, removes.Concat([new CrdtCandidate<T>(value, dot, CausalVersion.CopyContext(context), 0)]), Context);

    public RemoveWinsSet<T> Join(RemoveWinsSet<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var context = CausalVersion.CopyContext(Context);
        CausalVersion.MergeInto(context, other.Context);
        var mergedAdds = adds.Concat(other.adds).GroupBy(value => value.Dot).Select(group => group.First());
        var mergedRemoves = removes.Concat(other.removes).GroupBy(value => value.Dot).Select(group => group.First());
        return new(mergedAdds, mergedRemoves, context);
    }
}

/// <summary>Pure merge helpers for replication records and schema-free JSON.</summary>
public static class ReplicationLattice
{
    /// <summary>
    /// Merges two records using causal dominance, remove-wins tombstones, and
    /// deterministic per-field HLC selection. The returned record is independent
    /// of arrival order.
    /// </summary>
    public static ReplicationRecord Merge(
        ReplicationRecord left,
        ReplicationRecord right,
        RegisterMergePolicy policy = RegisterMergePolicy.RemoveWins)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Key != right.Key)
            throw new ArgumentException("Only records for the same document can be merged.", nameof(right));

        var leftVersion = new CausalVersion(left.Dot, left.Context);
        var rightVersion = new CausalVersion(right.Dot, right.Context);
        var context = CausalVersion.MergeContext(leftVersion, rightVersion);
        if (CausalVersion.Contains(right.Context, left.Dot))
            return right with { Context = context };
        if (CausalVersion.Contains(left.Context, right.Dot))
            return left with { Context = context };

        var deleted = policy == RegisterMergePolicy.RemoveWins && (left.Deleted || right.Deleted)
            ? true
            : policy == RegisterMergePolicy.AddWins
                ? left.Deleted && right.Deleted
                : left.Deleted && right.Deleted;
        var body = deleted ? null : MergeObjects(left, right, policy);
        var canonical = body is { } value ? JsonSerializer.SerializeToUtf8Bytes(value) : null;
        var winner = Compare(left, right) >= 0 ? left : right;
        return winner with { Context = context, Deleted = deleted, Body = null, CanonicalBody = canonical };
    }

    /// <summary>Produces a stable SHA-256 digest for a canonical replication row.</summary>
    public static string Digest(ReplicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var body = record.CanonicalBody?.ToArray() ?? (record.Body is { } value ? JsonSerializer.SerializeToUtf8Bytes(value) : []);
        var builder = new StringBuilder(record.Key.ToString());
        builder.Append('|').Append(record.Dot.Origin).Append(':').Append(record.Dot.Sequence)
            .Append('|').Append(record.Hlc).Append('|').Append(record.Deleted ? '1' : '0');
        foreach (var context in record.Context.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append('|').Append(context.Key).Append(':').Append(context.Value);
        var metadata = Encoding.UTF8.GetBytes(builder.ToString());
        var combined = new byte[metadata.Length + body.Length];
        metadata.CopyTo(combined, 0);
        body.CopyTo(combined, metadata.Length);
        return Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
    }

    private static int Compare(ReplicationRecord left, ReplicationRecord right) =>
        left.Hlc != right.Hlc
            ? left.Hlc.CompareTo(right.Hlc)
            : string.CompareOrdinal(left.Dot.Origin, right.Dot.Origin) != 0
                ? string.CompareOrdinal(left.Dot.Origin, right.Dot.Origin)
                : left.Dot.Sequence.CompareTo(right.Dot.Sequence);

    private static JsonElement? MergeObjects(ReplicationRecord leftRecord, ReplicationRecord rightRecord, RegisterMergePolicy policy)
    {
        var left = ReadBody(leftRecord);
        var right = ReadBody(rightRecord);
        if (left is null) return right?.Clone();
        if (right is null) return left?.Clone();
        if (left.Value.ValueKind != JsonValueKind.Object || right.Value.ValueKind != JsonValueKind.Object)
            return (Compare(leftRecord, rightRecord) >= 0 ? left : right)?.Clone();

        var result = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in left.Value.EnumerateObject()) result[property.Name] = property.Value.Clone();
        foreach (var property in right.Value.EnumerateObject())
        {
            if (!result.ContainsKey(property.Name))
            {
                result[property.Name] = property.Value.Clone();
                continue;
            }

            // For an object field the dots identify an atomic field assignment.
            // A remove-wins document tombstone has already been handled above.
            if (policy == RegisterMergePolicy.RemoveWins || Compare(leftRecord, rightRecord) < 0)
                result[property.Name] = property.Value.Clone();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var pair in result)
            {
                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement? ReadBody(ReplicationRecord record)
    {
        if (record.Body is { } body) return body;
        if (record.CanonicalBody is not { } bytes) return null;
        using var document = JsonDocument.Parse(bytes.ToArray());
        return document.RootElement.Clone();
    }
}
