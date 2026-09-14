namespace TerraBase.ZoneTree.FullTextSearch.Tokenizer;

/// <summary>
/// Tokenizes Unicode words, optionally retaining digits and filtering stop words.
/// </summary>
public sealed class WordTokenizer : IWordTokenizer
{
    private static readonly string[] DefaultStopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "if", "in", "into",
        "is", "it", "no", "not", "of", "on", "or", "such", "that", "the", "their", "then",
        "there", "these", "they", "this", "to", "was", "will", "with"
    ];

    private readonly HashSet<string> _stopWords;

    /// <summary>
    /// Gets the minimum number of characters required for a token.
    /// </summary>
    public int MinimumTokenLength { get; }

    /// <summary>
    /// Gets the compatibility spelling used by the original ZoneTree API.
    /// </summary>
    public int MimimumTokenLength => MinimumTokenLength;

    /// <summary>
    /// Gets whether numeric characters remain part of a token.
    /// </summary>
    public bool IncludeDigits { get; }

    /// <summary>
    /// Gets whether configured stop words are filtered.
    /// </summary>
    public bool UseStopWords { get; }

    /// <summary>
    /// Creates a word tokenizer. Tokens shorter than three characters are
    /// ignored by default, matching the ZoneTree search defaults.
    /// </summary>
    /// <param name="mimimumTokenLength">Minimum token length. The misspelling is retained for source compatibility.</param>
    /// <param name="includeDigits">Whether digits can occur in tokens.</param>
    /// <param name="useStopWords">Whether stop words should be filtered.</param>
    /// <param name="customStopWords">Optional replacement stop-word list.</param>
    public WordTokenizer(
        int mimimumTokenLength = 3,
        bool includeDigits = false,
        bool useStopWords = false,
        IEnumerable<string>? customStopWords = null)
    {
        if (mimimumTokenLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mimimumTokenLength), "Token length must be positive.");
        }

        MinimumTokenLength = mimimumTokenLength;
        IncludeDigits = includeDigits;
        UseStopWords = useStopWords;
        _stopWords = new HashSet<string>(
            customStopWords ?? DefaultStopWords,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds stop words to the active filter.
    /// </summary>
    public void AddStopWords(IEnumerable<string> stopWords)
    {
        ArgumentNullException.ThrowIfNull(stopWords);
        foreach (var stopWord in stopWords)
        {
            if (!string.IsNullOrWhiteSpace(stopWord))
            {
                _stopWords.Add(stopWord);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Slice> GetSlices(ReadOnlySpan<char> text)
    {
        var slices = new List<Slice>();
        AddSlices(text, slices);
        return slices;
    }

    /// <inheritdoc />
    public IEnumerable<Slice> EnumerateSlices(ReadOnlyMemory<char> text)
    {
        var slices = GetSlices(text.Span);
        return slices;
    }

    /// <summary>
    /// Tokenizes text and normalizes tokens for case-insensitive indexing.
    /// </summary>
    public IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var slices = GetSlices(text.AsSpan());
        var tokens = new List<string>(slices.Count);
        foreach (var slice in slices)
        {
            tokens.Add(text.AsSpan(slice.Offset, slice.Length).ToString().ToLowerInvariant());
        }

        return tokens;
    }

    private void AddSlices(ReadOnlySpan<char> text, ICollection<Slice> destination)
    {
        var start = -1;
        for (var index = 0; index < text.Length; index++)
        {
            var isWordCharacter = char.IsLetter(text[index]) || (IncludeDigits && char.IsDigit(text[index]));
            if (isWordCharacter)
            {
                start = start < 0 ? index : start;
                continue;
            }

            AddSlice(text, start, index - start, destination);
            start = -1;
        }

        AddSlice(text, start, text.Length - start, destination);
    }

    private void AddSlice(
        ReadOnlySpan<char> text,
        int start,
        int length,
        ICollection<Slice> destination)
    {
        if (start < 0 || length < MinimumTokenLength)
        {
            return;
        }

        var value = text.Slice(start, length);
        if (UseStopWords && _stopWords.Contains(value.ToString()))
        {
            return;
        }

        destination.Add(new Slice(start, length));
    }
}
