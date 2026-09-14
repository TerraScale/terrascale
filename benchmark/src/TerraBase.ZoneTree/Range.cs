namespace TerraBase.ZoneTree;

/// <summary>Describes one ordered range query.</summary>
/// <typeparam name="TKey">The indexed key type.</typeparam>
public readonly record struct KeyRange<TKey> where TKey : notnull
{
    private KeyRange(
        TKey? lowerBound,
        bool hasLowerBound,
        bool includeLowerBound,
        TKey? upperBound,
        bool hasUpperBound,
        bool includeUpperBound)
    {
        LowerBound = lowerBound;
        HasLowerBound = hasLowerBound;
        IncludeLowerBound = includeLowerBound;
        UpperBound = upperBound;
        HasUpperBound = hasUpperBound;
        IncludeUpperBound = includeUpperBound;
    }

    /// <summary>An unbounded range containing every key.</summary>
    public static KeyRange<TKey> All => new(default, false, false, default, false, false);

    /// <summary>The lower key, when <see cref="HasLowerBound"/> is true.</summary>
    public TKey? LowerBound { get; }

    /// <summary>Whether this range has a lower bound.</summary>
    public bool HasLowerBound { get; }

    /// <summary>Whether the lower key is included.</summary>
    public bool IncludeLowerBound { get; }

    /// <summary>The upper key, when <see cref="HasUpperBound"/> is true.</summary>
    public TKey? UpperBound { get; }

    /// <summary>Whether this range has an upper bound.</summary>
    public bool HasUpperBound { get; }

    /// <summary>Whether the upper key is included.</summary>
    public bool IncludeUpperBound { get; }

    /// <summary>Creates a range beginning at <paramref name="lowerBound"/>.</summary>
    public static KeyRange<TKey> From(TKey lowerBound, bool inclusive = true) =>
        new(lowerBound, true, inclusive, default, false, false);

    /// <summary>Creates a range ending at <paramref name="upperBound"/>.</summary>
    public static KeyRange<TKey> To(TKey upperBound, bool inclusive = false) =>
        new(default, false, false, upperBound, true, inclusive);

    /// <summary>Creates a range between two keys.</summary>
    public static KeyRange<TKey> Between(
        TKey lowerBound,
        TKey upperBound,
        bool includeLowerBound = true,
        bool includeUpperBound = false) =>
        new(lowerBound, true, includeLowerBound, upperBound, true, includeUpperBound);

    /// <summary>Creates a range containing one key.</summary>
    public static KeyRange<TKey> Exactly(TKey key) =>
        new(key, true, true, key, true, true);

    internal void Validate(IComparer<TKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        if (HasLowerBound && HasUpperBound)
        {
            var order = comparer.Compare(LowerBound!, UpperBound!);
            if (order > 0 || (order == 0 && (!IncludeLowerBound || !IncludeUpperBound)))
            {
                // An empty range is valid. Only reversed boundaries are invalid.
                if (order > 0)
                {
                    throw new ArgumentException("The range lower bound must not be greater than its upper bound.", nameof(comparer));
                }
            }
        }
    }

    internal bool Includes(TKey key, IComparer<TKey> comparer)
    {
        if (HasLowerBound)
        {
            var lowerComparison = comparer.Compare(key, LowerBound!);
            if (lowerComparison < 0 || (lowerComparison == 0 && !IncludeLowerBound))
            {
                return false;
            }
        }

        if (HasUpperBound)
        {
            var upperComparison = comparer.Compare(key, UpperBound!);
            if (upperComparison > 0 || (upperComparison == 0 && !IncludeUpperBound))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A point-in-time ordered key/value entry returned by a range scan.</summary>
public readonly record struct ZoneTreeEntry<TKey, TValue>(TKey Key, TValue Value, long OperationIndex)
    where TKey : notnull;
