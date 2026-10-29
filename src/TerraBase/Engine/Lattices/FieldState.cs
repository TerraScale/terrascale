using System.Collections.Immutable;
using System.Text.Json;

namespace TerraBase.Engine.Lattices;

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

    public BaseEntry? Base { get; }

    public ImmutableSortedDictionary<ReplicaId, CounterEntry> Counters { get; }

    public ImmutableSortedDictionary<DotKey, DotEntry> Dots { get; }

    public bool Equals(FieldState? other) =>
    other is not null && Nullable.Equals(Base, other.Base) && Counters.SequenceEqual(other.Counters) && Dots.SequenceEqual(other.Dots);

    public override bool Equals(object? obj) => Equals(obj as FieldState);

    public override int GetHashCode() => HashCode.Combine(Base, Counters.Count, Dots.Count);

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

    internal FieldValue Value(EntryVersion? clear)
    {
        var epoch = Epoch(Base, clear);
        var baseIsArray = Base is { Value: ['[', ..] };
        if (!Dots.IsEmpty && (baseIsArray || Dots.Values.Any(dot => dot.RemovedAt is null)))
        {
            return new(FieldKind.Set, Elements(baseIsArray));
        }

        Int128? total = null;
        if (!Counters.IsEmpty)
        {
            foreach (var (_, counter) in Counters)
            {
                if (counter.Epoch == epoch)
                {
                    total = (total ?? BaseNumber()) + counter.Increments - counter.Decrements;
                }
            }
        }

        if (total is { } sum)
        {

            return new(FieldKind.Counter, Count: (long)Int128.Clamp(sum, long.MinValue, long.MaxValue));
        }

        return Base is { Value: { } value } ? new(FieldKind.Base, Json: value) : default;
    }

    internal long BaseNumber()
    {
        if (Base is not { Value: { } value })
        {
            return 0;
        }

        using var json = JsonDocument.Parse(value, DocumentState.JsonOptions);
        return json.RootElement.ValueKind == JsonValueKind.Number && json.RootElement.TryGetInt64(out var number) ? number : 0;
    }

    internal IEnumerable<string> BaseElements()
    {
        if (Base is not { Value: ['[', ..] value })
        {
            return [];
        }

        using var json = JsonDocument.Parse(value, DocumentState.JsonOptions);
        return [.. json.RootElement.EnumerateArray().Select(element => element.GetRawText())];
    }

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
