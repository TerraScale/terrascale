using System.Collections.Immutable;

namespace TerraBase.Engine.Lattices;

/// <summary>
/// A multi-value register: each value is tagged with the <see cref="Dot"/> of its write, and the
/// <see cref="Context"/> remembers every write seen, so a write replaces exactly the values it observed and concurrent
/// writes survive side by side (<c>Causal&lt;DotFun&gt;</c>, delta-state CRDTs, figures 12 and 14). A document is one.
/// </summary>
/// <typeparam name="T">The value. Values with the same dot are the same value.</typeparam>
public sealed record MvRegister<T> : ILattice<MvRegister<T>>
{
    /// <summary>The empty register, the bottom.</summary>
    public MvRegister() : this([], new CausalContext())
    {
    }

    /// <summary>
    /// An empty register that has already seen <paramref name="floor"/>: a new key starts from its database's floor of
    /// collected dots (tombstone GC), and a write that failed leaves its dot behind, so no dot is ever minted twice.
    /// </summary>
    public MvRegister(CausalContext floor) : this([], floor ?? throw new ArgumentNullException(nameof(floor)))
    {
    }

    private MvRegister(ImmutableDictionary<Dot, T> values, CausalContext context)
    {
        Values = values;
        Context = context;
    }

    static MvRegister<T> ILattice<MvRegister<T>>.Bottom => new();

    // Rebuilds a decoded register: every value's write is a real event (counters start at 1) of the context that holds it.
    internal static MvRegister<T> FromParts(ImmutableDictionary<Dot, T> values, CausalContext context)
    {
        if (values.Keys.Any(dot => dot.Counter == 0 || !context.Contains(dot)))
        {
            throw new InvalidDataException("a register value lies outside its causal context");
        }

        return new(values, context);
    }

    /// <summary>The current values by dot: one after a sequential history, several after concurrent writes.</summary>
    public ImmutableDictionary<Dot, T> Values { get; }

    /// <summary>Every write this state has seen, including those whose values were since replaced.</summary>
    public CausalContext Context { get; }

    /// <summary>The delta of <paramref name="replica"/> writing <paramref name="value"/> over every value this state knows.</summary>
    public MvRegister<T> WriteDelta(ReplicaId replica, T value) => WriteDelta(replica, value, Context);

    /// <summary>
    /// The delta of <paramref name="replica"/> writing <paramref name="value"/> over the writes in
    /// <paramref name="observed"/> only; values written concurrently with it stay. This state must be
    /// <paramref name="replica"/>'s own, so the new dot is fresh.
    /// </summary>
    public MvRegister<T> WriteDelta(ReplicaId replica, T value, CausalContext observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        // Checked: a counter that wrapped to zero would read as already seen, and its write would vanish on any join.
        var dot = new Dot(replica, checked(Math.Max(Context.Max(replica), observed.Max(replica)) + 1));
        return new(ImmutableDictionary<Dot, T>.Empty.Add(dot, value), observed.Add(dot));
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A dot carries two different values: a replica minted it twice.</exception>
    public MvRegister<T> Join(MvRegister<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // A value survives when both sides hold it, or when the other side never saw its write.
        var values = ImmutableDictionary.CreateBuilder<Dot, T>();
        foreach (var (dot, value) in Values)
        {
            if (other.Values.TryGetValue(dot, out var theirs))
            {
                // Merging on would keep one value and silently drop the other everywhere (Riak kv679): refuse.
                if (!EqualityComparer<T>.Default.Equals(value, theirs))
                {
                    throw new InvalidOperationException(
                        $"dot ({dot.Replica.Value}, {dot.Counter}) carries two different values: a replica minted it twice");
                }

                values[dot] = value;
            }
            else if (!other.Context.Contains(dot))
            {
                values[dot] = value;
            }
        }

        foreach (var (dot, value) in other.Values)
        {
            if (!Values.ContainsKey(dot) && !Context.Contains(dot))
            {
                values[dot] = value;
            }
        }

        return new(values.ToImmutable(), Context.Join(other.Context));
    }

    /// <inheritdoc />
    public bool Equals(MvRegister<T>? other) =>
        other is not null
        && Context.Equals(other.Context)
        && Values.Count == other.Values.Count
        && Values.All(entry => other.Values.TryGetValue(entry.Key, out var value) && EqualityComparer<T>.Default.Equals(entry.Value, value));

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Values.Count, Context);
}
