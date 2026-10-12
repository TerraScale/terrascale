using System.Collections.Immutable;
using System.Text.Json;

namespace TerraBase.Engine.Lattices;

/// <summary>
/// One top-level field of a <see cref="DocumentState"/> (docs/terrabase/multi-region-replication.md section 4): a
/// last-writer-wins base, a PN-counter and an add-wins set of dots. Each part joins on its own; the document decides
/// what is dominated and what the field's value is.
/// </summary>
public sealed class FieldState : IEquatable<FieldState>
{
    internal static readonly ImmutableSortedDictionary<ReplicaId, CounterEntry> NoCounters =
        ImmutableSortedDictionary.Create<ReplicaId, CounterEntry>(Comparer<ReplicaId>.Create((x, y) => x.Value.CompareTo(y.Value)));

    internal static readonly ImmutableSortedDictionary<DotKey, DotEntry> NoDots = ImmutableSortedDictionary.Create<DotKey, DotEntry>(Utf8Ordinal.Instance);

    internal FieldState(BaseEntry? @base, ImmutableSortedDictionary<ReplicaId, CounterEntry> counters, ImmutableSortedDictionary<DotKey, DotEntry> dots)
    {
        Base = @base;
        Counters = counters;
        Dots = dots;
    }

    /// <summary>The base: the field's value or tombstone, absent when only counters or set operations wrote it.</summary>
    public BaseEntry? Base { get; }

    /// <summary>Each node's counter entry, by node.</summary>
    public ImmutableSortedDictionary<ReplicaId, CounterEntry> Counters { get; }

    /// <summary>The explicit adds of the set and their removes, by element and version.</summary>
    public ImmutableSortedDictionary<DotKey, DotEntry> Dots { get; }

    /// <inheritdoc />
    public bool Equals(FieldState? other) =>
        other is not null && Nullable.Equals(Base, other.Base) && Counters.SequenceEqual(other.Counters) && Dots.SequenceEqual(other.Dots);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FieldState);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Base, Counters.Count, Dots.Count);

    internal FieldState Join(FieldState other)
    {
        var counters = Counters.ToBuilder();
        foreach (var (node, theirs) in other.Counters)
        {
            counters[node] = counters.TryGetValue(node, out var mine) ? CounterEntry.Join(mine, theirs) : theirs;
        }

        var dots = Dots.ToBuilder();
        foreach (var (key, theirs) in other.Dots)
        {
            dots[key] = dots.TryGetValue(key, out var mine) ? DotEntry.Join(mine, theirs) : theirs;
        }

        return new(BaseEntry.Join(Base, other.Base), counters.ToImmutable(), dots.ToImmutable());
    }

    // Drops what the state already dominates: a base below the clear, counters of an older epoch, dots below the epoch.
    // Returns null when nothing is left.
    internal FieldState? Normalize(EntryVersion? clear)
    {
        var @base = Base is { } b && (clear is not { } c || b.Version >= c) ? Base : null;
        var epoch = Epoch(@base, clear);
        var counters = Counters.RemoveRange(Counters.Where(entry => entry.Value.Epoch < epoch).Select(entry => entry.Key));
        var dots = Dots.RemoveRange(Dots.Keys.Where(key => key.Version < epoch));
        if (@base is null && counters.IsEmpty && dots.IsEmpty)
        {
            return null;
        }

        return Nullable.Equals(@base, Base) && counters.Count == Counters.Count && dots.Count == Dots.Count ? this : new(@base, counters, dots);
    }

    internal static EntryVersion Epoch(BaseEntry? @base, EntryVersion? clear) => @base?.Version ?? clear ?? default;

    // The value rules: a set when live dots exist (or dots exist over an array base), else a counter when live counter
    // entries exist, else the base. Null when the field is not live.
    internal FieldValue Value(EntryVersion? clear)
    {
        var epoch = Epoch(Base, clear);
        var baseIsArray = Base is { Value: ['[', ..] };
        if (!Dots.IsEmpty && (baseIsArray || Dots.Values.Any(dot => dot.RemovedAt is null)))
        {
            return new(FieldKind.Set, Elements(baseIsArray));
        }

        var live = Counters.Values.Where(counter => counter.Epoch == epoch).ToArray();
        if (live.Length > 0)
        {
            var sum = (Int128)BaseNumber();
            foreach (var counter in live)
            {
                sum += counter.Increments;
                sum -= counter.Decrements;
            }

            // Concurrent increments can overflow even when each node's own stayed in range: clamp, the same on every node.
            return new(FieldKind.Counter, Count: (long)Int128.Clamp(sum, long.MinValue, long.MaxValue));
        }

        return Base is { Value: { } value } ? new(FieldKind.Base, Json: value) : default;
    }

    // The base as a number for a counter: 0 unless it holds an integer.
    internal long BaseNumber()
    {
        if (Base is not { Value: { } value })
        {
            return 0;
        }

        using var json = JsonDocument.Parse(value, DocumentState.JsonOptions);
        return json.RootElement.ValueKind == JsonValueKind.Number && json.RootElement.TryGetInt64(out var number) ? number : 0;
    }

    // The elements of an array base: the implicit adds at its version.
    internal IEnumerable<string> BaseElements()
    {
        if (Base is not { Value: ['[', ..] value })
        {
            return [];
        }

        using var json = JsonDocument.Parse(value, DocumentState.JsonOptions);
        return [.. json.RootElement.EnumerateArray().Select(element => element.GetRawText())];
    }

    // The set's elements: the implicit adds of an array base without an explicit entry, and every explicit add not
    // removed. Distinct, sorted by their UTF-8 bytes.
    private string[] Elements(bool baseIsArray)
    {
        var elements = new SortedSet<string>(Utf8Ordinal.Instance);
        if (baseIsArray)
        {
            var version = Base!.Value.Version;
            elements.UnionWith(BaseElements().Where(element => !Dots.ContainsKey(new DotKey(element, version))));
        }

        elements.UnionWith(Dots.Where(dot => dot.Value.RemovedAt is null).Select(dot => dot.Key.Element));
        return [.. elements];
    }
}

internal enum FieldKind
{
    None,
    Base,
    Counter,
    Set,
}

internal readonly record struct FieldValue(FieldKind Kind, string[]? Elements = null, long Count = 0, string? Json = null);
