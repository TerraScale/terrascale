using System.Collections.ObjectModel;

namespace TerraBase.ZoneTree.FullTextSearch.Index;

/// <summary>
/// An in-memory inverted index mapping normalized tokens to records and token
/// positions. The index is deliberately independent of the storage provider;
/// callers can rebuild it from their authoritative records.
/// </summary>
/// <typeparam name="TRecord">The record key type.</typeparam>
public sealed class InvertedIndex<TRecord>
    where TRecord : notnull
{
    private readonly IEqualityComparer<TRecord> _recordComparer;
    private readonly Dictionary<string, Dictionary<TRecord, List<int>>> _postings;
    private readonly Dictionary<TRecord, Dictionary<string, List<int>>> _records;
    private readonly List<TRecord> _recordOrder = [];

    /// <summary>
    /// Creates an empty index.
    /// </summary>
    public InvertedIndex(IEqualityComparer<TRecord>? recordComparer = null)
    {
        _recordComparer = recordComparer ?? EqualityComparer<TRecord>.Default;
        _postings = new Dictionary<string, Dictionary<TRecord, List<int>>>(StringComparer.OrdinalIgnoreCase);
        _records = new Dictionary<TRecord, Dictionary<string, List<int>>>(_recordComparer);
    }

    /// <summary>
    /// Gets the number of records currently represented by the index.
    /// </summary>
    public int RecordCount => _records.Count;

    /// <summary>
    /// Gets the number of distinct tokens currently represented by the index.
    /// </summary>
    public int TokenCount => _postings.Count;

    /// <summary>
    /// Replaces all tokens for a record and retains its original insertion
    /// position when it is already present.
    /// </summary>
    public void Upsert(TRecord record, IEnumerable<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var previousOrder = _recordOrder.IndexOf(record);
        Remove(record);

        var positionsByToken = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var normalized = token.ToLowerInvariant();
            if (!positionsByToken.TryGetValue(normalized, out var positions))
            {
                positions = [];
                positionsByToken.Add(normalized, positions);
            }

            positions.Add(position++);
        }

        if (positionsByToken.Count == 0)
        {
            return;
        }

        _records.Add(record, positionsByToken);
        if (previousOrder < 0)
        {
            _recordOrder.Add(record);
        }
        else
        {
            _recordOrder.Insert(Math.Min(previousOrder, _recordOrder.Count), record);
        }
        foreach (var (token, positions) in positionsByToken)
        {
            if (!_postings.TryGetValue(token, out var records))
            {
                records = new Dictionary<TRecord, List<int>>(_recordComparer);
                _postings.Add(token, records);
            }

            records.Add(record, positions);
        }
    }

    /// <summary>
    /// Removes a record and all its token postings.
    /// </summary>
    /// <returns><see langword="true"/> when a record was removed.</returns>
    public bool Remove(TRecord record)
    {
        if (!_records.Remove(record, out var positionsByToken))
        {
            return false;
        }

        _recordOrder.Remove(record);
        foreach (var token in positionsByToken.Keys)
        {
            if (!_postings.TryGetValue(token, out var records))
            {
                continue;
            }

            records.Remove(record);
            if (records.Count == 0)
            {
                _postings.Remove(token);
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the records containing a token in insertion order.
    /// </summary>
    public IReadOnlyList<TRecord> GetRecords(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_postings.TryGetValue(token, out var records))
        {
            return [];
        }

        return _recordOrder.Where(records.ContainsKey).ToArray();
    }

    /// <summary>
    /// Finds records containing all supplied tokens. When order is respected,
    /// tokens must appear as one contiguous phrase.
    /// </summary>
    public IReadOnlyList<TRecord> Search(IReadOnlyList<string> tokens, bool respectTokenOrder = true)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0)
        {
            return [];
        }

        var normalizedTokens = tokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Select(token => token.ToLowerInvariant())
            .ToArray();
        if (normalizedTokens.Length == 0)
        {
            return [];
        }

        var candidateRecords = normalizedTokens
            .Select(token => _postings.TryGetValue(token, out var posting) ? posting : null)
            .Where(posting => posting is not null)
            .Cast<Dictionary<TRecord, List<int>>>()
            .OrderBy(posting => posting.Count)
            .FirstOrDefault();
        if (candidateRecords is null)
        {
            return [];
        }

        var results = new List<TRecord>();
        foreach (var record in _recordOrder)
        {
            if (!candidateRecords.ContainsKey(record))
            {
                continue;
            }

            if (normalizedTokens.All(token => _postings[token].ContainsKey(record)) &&
                (!respectTokenOrder || IsPhrase(record, normalizedTokens)))
            {
                results.Add(record);
            }
        }

        return results;
    }

    /// <summary>
    /// Gets a snapshot of a record's normalized token positions.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> GetTokenPositions(TRecord record)
    {
        if (!_records.TryGetValue(record, out var positions))
        {
            return new ReadOnlyDictionary<string, IReadOnlyList<int>>(
                new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase));
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<int>>(
            positions.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<int>)pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns all records in their insertion order.
    /// </summary>
    public IReadOnlyList<TRecord> GetRecords() => _recordOrder.ToArray();

    private bool IsPhrase(TRecord record, IReadOnlyList<string> tokens)
    {
        var firstPositions = _postings[tokens[0]][record];
        foreach (var firstPosition in firstPositions)
        {
            var matches = true;
            for (var offset = 1; offset < tokens.Count; offset++)
            {
                var positions = _postings[tokens[offset]][record];
                if (!positions.Contains(firstPosition + offset))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }
}
