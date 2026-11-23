using System.Text;

namespace TerraBase.Postgres.Sql;

internal sealed class PgTokenEditor
{
    private readonly string _sql;
    private readonly PgToken[] _tokens;
    private readonly bool[] _spaceBefore;
    private readonly int[] _match;
    private readonly string?[] _replacement;
    private readonly string?[] _prefix;
    private readonly string?[] _suffix;
    private readonly bool[] _dead;

    private PgTokenEditor(string sql, PgToken[] tokens, bool[] spaceBefore, int[] match)
    {
        _sql = sql;
        _tokens = tokens;
        _spaceBefore = spaceBefore;
        _match = match;
        _replacement = new string?[tokens.Length];
        _prefix = new string?[tokens.Length];
        _suffix = new string?[tokens.Length];
        _dead = new bool[tokens.Length];
    }

    public int Count => _tokens.Length;

    public string Source => _sql;

    public bool IsBalanced { get; private init; }

    public static PgTokenEditor Create(string sql, ReadOnlySpan<PgToken> tokens)
    {
        List<PgToken> significant = [];
        List<bool> spaces = [];
        var space = false;
        foreach (var token in tokens)
        {
            if (token.Kind == PgTokenKind.Whitespace)
            {
                space = true;
                continue;
            }

            significant.Add(token);
            spaces.Add(space);
            space = false;
        }

        var match = new int[significant.Count];
        Array.Fill(match, -1);
        Stack<int> open = [];
        var balanced = true;
        for (var index = 0; index < significant.Count; index++)
        {
            switch (significant[index].Kind)
            {
                case PgTokenKind.LeftParen or PgTokenKind.LeftBracket:
                    open.Push(index);
                    break;
                case PgTokenKind.RightParen or PgTokenKind.RightBracket:
                    if (!open.TryPop(out var opener) || (significant[opener].Kind == PgTokenKind.LeftParen) != (significant[index].Kind == PgTokenKind.RightParen))
                    {
                        balanced = false;
                        break;
                    }

                    match[opener] = index;
                    match[index] = opener;
                    break;
            }
        }

        return new PgTokenEditor(sql, [.. significant], [.. spaces], match) { IsBalanced = balanced && open.Count == 0 };
    }

    public PgTokenKind Kind(int index) => index >= 0 && index < _tokens.Length ? _tokens[index].Kind : PgTokenKind.Whitespace;

    public ReadOnlySpan<char> Raw(int index) => _sql.AsSpan(_tokens[index].Start, _tokens[index].Length);

    public string Text(int index) => _sql.Substring(_tokens[index].Start, _tokens[index].Length);

    public int Start(int index) => _tokens[index].Start;

    public int End(int index) => _tokens[index].Start + _tokens[index].Length;

    public int Match(int index) => index >= 0 && index < _match.Length ? _match[index] : -1;

    public bool IsDead(int index) => _dead[index];

    public bool IsEdited(int index) => _replacement[index] is not null;

    public bool IsWord(int index, string word) =>
        Kind(index) == PgTokenKind.Word && Raw(index).Equals(word, StringComparison.OrdinalIgnoreCase);

    public bool IsAnyWord(int index, params ReadOnlySpan<string> words)
    {
        if (Kind(index) != PgTokenKind.Word)
        {
            return false;
        }

        var raw = Raw(index);
        foreach (var word in words)
        {
            if (raw.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsOperator(int index, string text) => Kind(index) == PgTokenKind.Operator && Raw(index).SequenceEqual(text);

    public bool IsName(int index) => Kind(index) is PgTokenKind.Word or PgTokenKind.QuotedIdentifier;

    public string Identifier(int index) => Kind(index) == PgTokenKind.QuotedIdentifier
        ? Text(index)[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
        : Text(index).ToLowerInvariant();

    public void Replace(int index, string text) => _replacement[index] = text;

    public void Remove(int index) => _replacement[index] = string.Empty;

    public void RemoveRange(int from, int to)
    {
        for (var index = from; index <= to && index < _tokens.Length; index++)
        {
            _replacement[index] = string.Empty;
        }
    }

    public void Kill(int from, int to)
    {
        for (var index = from; index <= to && index < _tokens.Length; index++)
        {
            _replacement[index] = string.Empty;
            _prefix[index] = null;
            _suffix[index] = null;
            _dead[index] = true;
        }
    }

    public void Prepend(int index, string text) => _prefix[index] = text + _prefix[index];

    public void Append(int index, string text) => _suffix[index] += text;

    public string Render() => _tokens.Length == 0 ? string.Empty : Render(0, _tokens.Length - 1);

    public string Render(int from, int to)
    {
        var builder = new StringBuilder();
        for (var index = from; index <= to && index < _tokens.Length; index++)
        {
            if (index > from && _spaceBefore[index] && builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            builder.Append(_prefix[index]);
            if (_replacement[index] is { } replacement)
            {
                builder.Append(replacement);
            }
            else
            {
                builder.Append(Raw(index));
            }

            builder.Append(_suffix[index]);
        }

        return builder.ToString().Trim();
    }

    public string RenderSource(int from, int to) => from > to ? string.Empty : _sql[Start(from)..End(to)];
}
