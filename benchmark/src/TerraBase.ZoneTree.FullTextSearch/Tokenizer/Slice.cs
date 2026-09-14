namespace TerraBase.ZoneTree.FullTextSearch.Tokenizer;

/// <summary>
/// Identifies a token within a source string.
/// </summary>
public readonly record struct Slice(int Offset, int Length)
{
    /// <summary>
    /// Returns the text represented by this slice.
    /// </summary>
    public string Extract(ReadOnlySpan<char> source) => source.Slice(Offset, Length).ToString();
}
