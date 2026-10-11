using System.Collections.Immutable;

namespace TerraBase.Engine.Lattices;

/// <summary>
/// An add-wins set: each element keeps the dots of the adds that put it there, so a remove takes out only the adds it
/// observed and a concurrent add survives (<c>Causal&lt;DotMap&lt;T, DotSet&gt;&gt;</c>, delta-state CRDTs, figures 12 and 15).
/// </summary>
/// <typeparam name="T">The element.</typeparam>
public sealed record AwSet<T> : ILattice<AwSet<T>> where T : notnull
{
    /// <summary>The empty set, the bottom.</summary>
    public AwSet() : this([], new CausalContext())
    {
    }

    private AwSet(ImmutableDictionary<T, ImmutableHashSet<Dot>> entries, CausalContext context)
    {
        Entries = entries;
        Context = context;
    }

    static AwSet<T> ILattice<AwSet<T>>.Bottom => new();

    /// <summary>Each element with the dots of the adds that keep it; never an empty dot set.</summary>
    public ImmutableDictionary<T, ImmutableHashSet<Dot>> Entries { get; }

    /// <summary>Every add and remove this state has seen.</summary>
    public CausalContext Context { get; }

    /// <summary>Whether <paramref name="item"/> is in the set.</summary>
    public bool Contains(T item) => Entries.ContainsKey(item);

    /// <summary>The delta of <paramref name="replica"/> adding <paramref name="item"/>. This state must be <paramref name="replica"/>'s own.</summary>
    public AwSet<T> AddDelta(ReplicaId replica, T item)
    {
        var dot = new Dot(replica, Context.Max(replica) + 1);
        return new(ImmutableDictionary<T, ImmutableHashSet<Dot>>.Empty.Add(item, [dot]), CausalContext.Of(Dots(item)).Add(dot));
    }

    /// <summary>The delta that removes <paramref name="item"/> as this state sees it.</summary>
    public AwSet<T> RemoveDelta(T item) => new([], CausalContext.Of(Dots(item)));

    /// <inheritdoc />
    public AwSet<T> Join(AwSet<T> other)
    {
        var entries = ImmutableDictionary.CreateBuilder<T, ImmutableHashSet<Dot>>();
        foreach (var item in Entries.Keys.Union(other.Entries.Keys))
        {
            var mine = Dots(item);
            var theirs = other.Dots(item);
            var dots = mine.Intersect(theirs)
                .Union(mine.Where(dot => !other.Context.Contains(dot)))
                .Union(theirs.Where(dot => !Context.Contains(dot)));
            if (!dots.IsEmpty)
            {
                entries[item] = dots;
            }
        }

        return new(entries.ToImmutable(), Context.Join(other.Context));
    }

    /// <inheritdoc />
    public bool Equals(AwSet<T>? other) =>
        other is not null
        && Context.Equals(other.Context)
        && Entries.Count == other.Entries.Count
        && Entries.All(entry => other.Entries.TryGetValue(entry.Key, out var dots) && dots.SetEquals(entry.Value));

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Entries.Count, Context);

    private ImmutableHashSet<Dot> Dots(T item) => Entries.GetValueOrDefault(item, []);
}
