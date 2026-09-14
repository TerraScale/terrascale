namespace TerraBase.ZoneTree.FullTextSearch.Tokenizer;

/// <summary>
/// Splits text into word slices without allocating token strings.
/// </summary>
public interface IWordTokenizer
{
    /// <summary>
    /// Gets the token slices in a string.
    /// </summary>
    IReadOnlyList<Slice> GetSlices(ReadOnlySpan<char> text);

    /// <summary>
    /// Enumerates token slices in a character buffer.
    /// </summary>
    IEnumerable<Slice> EnumerateSlices(ReadOnlyMemory<char> text);
}
