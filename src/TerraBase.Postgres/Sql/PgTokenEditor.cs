using System.Buffers;
using System.Runtime.CompilerServices;

namespace TerraBase.Postgres.Sql;

[Flags]
internal enum PgSyntaxFlags : uint
{
    None = 0,
    Parameter = 1 << 0,
    StringLiteral = 1 << 1,
    Period = 1 << 2,
    FunctionCall = 1 << 3,
    UnsupportedCall = 1 << 4,
    DoubleColon = 1 << 5,
    PgPrefixWord = 1 << 6,
    IsWord = 1 << 7,
    ArrayComparisonWord = 1 << 8,
    PatternOperator = 1 << 9,
    ClauseWord = 1 << 10,
    IntoWord = 1 << 11,
    CreateOrAlter = 1 << 12,
    SelectStart = 1 << 13,
    SpecialKeyword = 1 << 14,
}

internal readonly struct PooledPgTokenEditor(PgTokenEditor editor) : IDisposable
{
    public PgTokenEditor Editor => editor;

    public void Dispose() => editor.ReturnBuffers();
}

internal sealed class PgTokenEditor
{
    private const byte FlagSpaceBefore = 1 << 0;
    private const byte FlagDead = 1 << 1;
    private const byte FlagRemoved = 1 << 2;

    private struct TokenMeta
    {
        public int Start;
        public int Length;
        public int Match;
        public short EditIndex;
        public PgTokenKind Kind;
        public byte State;
    }

    private struct TokenEdit
    {
        public string? Replacement;
        public string? Prefix;
        public string? Suffix;
    }

    private readonly string _sql;
    private readonly int _count;
    private TokenMeta[] _slots;
    private TokenEdit[]? _edits;
    private int _editCount;
    private int _addedChars;
    private bool _pooled;

    private PgTokenEditor(string sql, TokenMeta[] slots, int count, PgSyntaxFlags flags, bool isBalanced, bool pooled)
    {
        _sql = sql;
        _slots = slots;
        _count = count;
        Flags = flags;
        IsBalanced = isBalanced;
        _pooled = pooled;
    }

    public int Count => _count;

    public string Source => _sql;

    public bool IsBalanced { get; }

    public PgSyntaxFlags Flags { get; }

    public static PgTokenEditor Create(string sql, ReadOnlySpan<PgToken> tokens) =>
        CreateCore(sql, tokens, pooled: false);

    public static PooledPgTokenEditor CreatePooled(string sql, ReadOnlySpan<PgToken> tokens) =>
        new(CreateCore(sql, tokens, pooled: true));

    private static PgTokenEditor CreateCore(string sql, ReadOnlySpan<PgToken> tokens, bool pooled)
    {
        if (tokens.IsEmpty)
        {
            return new PgTokenEditor(sql, [], 0, PgSyntaxFlags.None, isBalanced: true, pooled: false);
        }

        var slots = pooled ? ArrayPool<TokenMeta>.Shared.Rent(tokens.Length) : new TokenMeta[tokens.Length];
        Span<int> stackBuffer = stackalloc int[16];
        int[]? rentedStack = null;
        var openStack = stackBuffer;
        var openCount = 0;
        var balanced = true;
        var flags = PgSyntaxFlags.None;
        var space = false;
        var slotIndex = 0;
        var prevWasLimit = false;
        var sqlSpan = sql.AsSpan();
        try
        {
            for (var index = 0; index < tokens.Length; index++)
            {
                var token = tokens[index];
                if (token.Kind == PgTokenKind.Whitespace)
                {
                    space = true;
                    continue;
                }

                ref var slot = ref slots[slotIndex];
                slot.Start = token.Start;
                slot.Length = token.Length;
                slot.Match = -1;
                slot.EditIndex = -1;
                slot.Kind = token.Kind;
                slot.State = space ? FlagSpaceBefore : (byte)0;
                space = false;

                var isLimit = false;
                switch (token.Kind)
                {
                    case PgTokenKind.Word:
                        var word = sqlSpan.Slice(token.Start, token.Length);
                        flags |= ClassifyWord(word, slotIndex == 0, prevWasLimit, out isLimit);
                        break;
                    case PgTokenKind.Parameter:
                        flags |= PgSyntaxFlags.Parameter;
                        break;
                    case PgTokenKind.String or PgTokenKind.EscapeString or PgTokenKind.DollarString:
                        flags |= PgSyntaxFlags.StringLiteral;
                        break;
                    case PgTokenKind.Period:
                        flags |= PgSyntaxFlags.Period;
                        break;
                    case PgTokenKind.DoubleColon:
                        flags |= PgSyntaxFlags.DoubleColon;
                        break;
                    case PgTokenKind.Operator:
                        if (sqlSpan.Slice(token.Start, token.Length).Contains('~'))
                        {
                            flags |= PgSyntaxFlags.PatternOperator;
                        }

                        break;
                    case PgTokenKind.LeftParen:
                        if (slotIndex > 0 && slots[slotIndex - 1].Kind == PgTokenKind.Word)
                        {
                            var prevWord = sqlSpan.Slice(slots[slotIndex - 1].Start, slots[slotIndex - 1].Length);
                            flags |= ClassifyCallTarget(prevWord);
                        }

                        PushOpen(ref openStack, ref rentedStack, ref openCount, slotIndex);
                        break;
                    case PgTokenKind.LeftBracket:
                        PushOpen(ref openStack, ref rentedStack, ref openCount, slotIndex);
                        break;
                    case PgTokenKind.RightParen or PgTokenKind.RightBracket:
                        if (openCount == 0)
                        {
                            balanced = false;
                            break;
                        }

                        var opener = openStack[--openCount];
                        if ((slots[opener].Kind == PgTokenKind.LeftParen) != (token.Kind == PgTokenKind.RightParen))
                        {
                            balanced = false;
                            break;
                        }

                        slots[opener].Match = slotIndex;
                        slot.Match = opener;
                        break;
                }

                prevWasLimit = isLimit;
                slotIndex++;
            }

            if (slotIndex == 0)
            {
                if (pooled)
                {
                    ArrayPool<TokenMeta>.Shared.Return(slots);
                }

                return new PgTokenEditor(sql, [], 0, PgSyntaxFlags.None, isBalanced: true, pooled: false);
            }

            return new PgTokenEditor(sql, slots, slotIndex, flags, balanced && openCount == 0, pooled);
        }
        finally
        {
            if (rentedStack is not null)
            {
                ArrayPool<int>.Shared.Return(rentedStack);
            }
        }
    }

    internal void ReturnBuffers()
    {
        if (!_pooled)
        {
            return;
        }

        _pooled = false;
        if (_slots.Length > 0)
        {
            ArrayPool<TokenMeta>.Shared.Return(_slots);
            _slots = [];
        }

        if (_edits is { } edits)
        {
            if (_editCount > 0)
            {
                Array.Clear(edits, 0, _editCount);
            }

            ArrayPool<TokenEdit>.Shared.Return(edits);
            _edits = null;
            _editCount = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasFlag(PgSyntaxFlags flag) => (Flags & flag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PgTokenKind Kind(int index) => (uint)index < (uint)_count ? _slots[index].Kind : PgTokenKind.Whitespace;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> Raw(int index)
    {
        ref readonly var slot = ref _slots[index];
        return _sql.AsSpan(slot.Start, slot.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string Text(int index)
    {
        ref readonly var slot = ref _slots[index];
        return _sql.Substring(slot.Start, slot.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Start(int index) => _slots[index].Start;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int End(int index) => _slots[index].Start + _slots[index].Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Match(int index) => (uint)index < (uint)_count ? _slots[index].Match : -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDead(int index) => (_slots[index].State & FlagDead) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEdited(int index)
    {
        ref readonly var slot = ref _slots[index];
        if ((slot.State & FlagRemoved) != 0)
        {
            return true;
        }

        var editIndex = slot.EditIndex;
        return editIndex >= 0 && _edits is not null && _edits[editIndex].Replacement is not null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsWord(int index, string word) =>
        (uint)index < (uint)_count
        && _slots[index].Kind == PgTokenKind.Word
        && _slots[index].Length == word.Length
        && Raw(index).Equals(word, StringComparison.OrdinalIgnoreCase);

    public bool IsAnyWord(int index, params ReadOnlySpan<string> words)
    {
        if ((uint)index >= (uint)_count || _slots[index].Kind != PgTokenKind.Word)
        {
            return false;
        }

        var raw = Raw(index);
        foreach (var word in words)
        {
            if (raw.Length == word.Length && raw.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOperator(int index, string text) =>
        (uint)index < (uint)_count
        && _slots[index].Kind == PgTokenKind.Operator
        && _slots[index].Length == text.Length
        && Raw(index).SequenceEqual(text);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsName(int index) =>
        (uint)index < (uint)_count && _slots[index].Kind is PgTokenKind.Word or PgTokenKind.QuotedIdentifier;

    public bool EqualsIdentifier(int index, ReadOnlySpan<char> expectedLower)
    {
        if ((uint)index >= (uint)_count)
        {
            return false;
        }

        ref readonly var slot = ref _slots[index];
        if (slot.Kind == PgTokenKind.Word)
        {
            return slot.Length == expectedLower.Length
                && _sql.AsSpan(slot.Start, slot.Length).Equals(expectedLower, StringComparison.OrdinalIgnoreCase);
        }

        if (slot.Kind == PgTokenKind.QuotedIdentifier)
        {
            var inner = _sql.AsSpan(slot.Start + 1, slot.Length - 2);
            if (!inner.Contains('"'))
            {
                return inner.SequenceEqual(expectedLower);
            }

            return Identifier(index).AsSpan().SequenceEqual(expectedLower);
        }

        return false;
    }

    public string Identifier(int index)
    {
        ref readonly var slot = ref _slots[index];
        var raw = _sql.AsSpan(slot.Start, slot.Length);
        if (slot.Kind == PgTokenKind.QuotedIdentifier)
        {
            var inner = raw[1..^1];
            return inner.Contains('"')
                ? inner.ToString().Replace("\"\"", "\"", StringComparison.Ordinal)
                : inner.ToString();
        }

        var hasUpper = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if ((uint)(ch - 'A') <= ('Z' - 'A') || (ch >= 128 && char.IsUpper(ch)))
            {
                hasUpper = true;
                break;
            }
        }

        if (!hasUpper)
        {
            return _sql.Substring(slot.Start, slot.Length);
        }

        if (raw.Length <= 64)
        {
            Span<char> lowered = stackalloc char[raw.Length];
            raw.ToLowerInvariant(lowered);
            return new string(lowered);
        }

        return _sql.Substring(slot.Start, slot.Length).ToLowerInvariant();
    }

    public void Replace(int index, string text)
    {
        if (text.Length == 0)
        {
            Remove(index);
            return;
        }

        _slots[index].State &= unchecked((byte)~FlagRemoved);
        GetOrCreateEdit(index).Replacement = text;
        _addedChars += text.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Remove(int index)
    {
        ref var slot = ref _slots[index];
        slot.State |= FlagRemoved;
        if (slot.EditIndex >= 0 && _edits is not null)
        {
            _edits[slot.EditIndex].Replacement = null;
        }
    }

    public void RemoveRange(int from, int to)
    {
        var limit = Math.Min(to, _count - 1);
        for (var index = from; index <= limit; index++)
        {
            Remove(index);
        }
    }

    public void Kill(int from, int to)
    {
        var limit = Math.Min(to, _count - 1);
        for (var index = from; index <= limit; index++)
        {
            ref var slot = ref _slots[index];
            slot.State |= FlagDead | FlagRemoved;
            slot.EditIndex = -1;
        }
    }

    public void Prepend(int index, string text)
    {
        ref var prefix = ref GetOrCreateEdit(index).Prefix;
        prefix = prefix is null ? text : string.Concat(text, prefix);
        _addedChars += text.Length;
    }

    public void Append(int index, string text)
    {
        ref var suffix = ref GetOrCreateEdit(index).Suffix;
        suffix = suffix is null ? text : string.Concat(suffix, text);
        _addedChars += text.Length;
    }

    public string Render() => _count == 0 ? string.Empty : Render(0, _count - 1);

    public string Render(int from, int to)
    {
        var limit = Math.Min(to, _count - 1);
        if (from > limit)
        {
            return string.Empty;
        }

        var maxLength = (_slots[limit].Start + _slots[limit].Length - _slots[from].Start) + _addedChars + (limit - from);
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        char[]? rented = null;
        var destination = maxLength <= 512
            ? stackalloc char[maxLength]
            : (rented = ArrayPool<char>.Shared.Rent(maxLength)).AsSpan(0, maxLength);
        try
        {
            var sqlSpan = _sql.AsSpan();
            var edits = _edits;
            var pos = 0;
            for (var index = from; index <= limit; index++)
            {
                ref readonly var slot = ref _slots[index];
                if (index > from && (slot.State & FlagSpaceBefore) != 0 && pos > 0 && destination[pos - 1] != ' ')
                {
                    destination[pos++] = ' ';
                }

                var editIndex = slot.EditIndex;
                if (editIndex >= 0 && edits is not null)
                {
                    ref readonly var edit = ref edits[editIndex];
                    if (edit.Prefix is { } prefix)
                    {
                        prefix.AsSpan().CopyTo(destination[pos..]);
                        pos += prefix.Length;
                    }

                    if ((slot.State & FlagRemoved) == 0)
                    {
                        if (edit.Replacement is { } replacement)
                        {
                            replacement.AsSpan().CopyTo(destination[pos..]);
                            pos += replacement.Length;
                        }
                        else
                        {
                            sqlSpan.Slice(slot.Start, slot.Length).CopyTo(destination[pos..]);
                            pos += slot.Length;
                        }
                    }

                    if (edit.Suffix is { } suffix)
                    {
                        suffix.AsSpan().CopyTo(destination[pos..]);
                        pos += suffix.Length;
                    }
                }
                else if ((slot.State & FlagRemoved) == 0)
                {
                    sqlSpan.Slice(slot.Start, slot.Length).CopyTo(destination[pos..]);
                    pos += slot.Length;
                }
            }

            return new string(destination[..pos].Trim());
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    public ReadOnlySpan<char> SourceSpan(int from, int to) =>
        from > to ? [] : _sql.AsSpan(Start(from), End(to) - Start(from));

    public string RenderSource(int from, int to) => from > to ? string.Empty : _sql[Start(from)..End(to)];

    private ref TokenEdit GetOrCreateEdit(int index)
    {
        ref var slot = ref _slots[index];
        var editIndex = slot.EditIndex;
        if (editIndex >= 0 && _edits is not null)
        {
            return ref _edits[editIndex];
        }

        var edits = _edits;
        if (edits is null)
        {
            edits = _pooled ? ArrayPool<TokenEdit>.Shared.Rent(8) : new TokenEdit[8];
            _edits = edits;
        }
        else if (_editCount == edits.Length)
        {
            if (_pooled)
            {
                var grown = ArrayPool<TokenEdit>.Shared.Rent(edits.Length * 2);
                edits.AsSpan(0, _editCount).CopyTo(grown);
                Array.Clear(edits, 0, _editCount);
                ArrayPool<TokenEdit>.Shared.Return(edits);
                edits = grown;
            }
            else
            {
                Array.Resize(ref edits, edits.Length * 2);
            }

            _edits = edits;
        }

        editIndex = (short)_editCount++;
        edits[editIndex] = default;
        slot.EditIndex = editIndex;
        return ref edits[editIndex];
    }

    private static void PushOpen(ref Span<int> stack, ref int[]? rented, ref int count, int value)
    {
        if (count == stack.Length)
        {
            var grown = ArrayPool<int>.Shared.Rent(stack.Length * 2);
            stack.CopyTo(grown);
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }

            rented = grown;
            stack = grown;
        }

        stack[count++] = value;
    }

    private static PgSyntaxFlags ClassifyCallTarget(ReadOnlySpan<char> prevWord)
    {
        var len = prevWord.Length;
        var first = (char)(prevWord[0] | 0x20);
        if (len is 2 or 3)
        {
            if ((len == 2 && first is 'a' or 'i' or 'o' && (prevWord.Equals("AS", StringComparison.OrdinalIgnoreCase)
                || prevWord.Equals("IN", StringComparison.OrdinalIgnoreCase)
                || prevWord.Equals("ON", StringComparison.OrdinalIgnoreCase)
                || prevWord.Equals("OR", StringComparison.OrdinalIgnoreCase)))
                || (len == 3 && first is 'a' or 'n' or 's' && (prevWord.Equals("ANY", StringComparison.OrdinalIgnoreCase)
                    || prevWord.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                    || prevWord.Equals("AND", StringComparison.OrdinalIgnoreCase)
                    || prevWord.Equals("NOT", StringComparison.OrdinalIgnoreCase)
                    || prevWord.Equals("SET", StringComparison.OrdinalIgnoreCase))))
            {
                return PgSyntaxFlags.None;
            }
        }
        else if (len == 4 && first is 's' or 'f' or 'j'
            && (prevWord.Equals("SOME", StringComparison.OrdinalIgnoreCase)
                || prevWord.Equals("FROM", StringComparison.OrdinalIgnoreCase)
                || prevWord.Equals("JOIN", StringComparison.OrdinalIgnoreCase)))
        {
            return PgSyntaxFlags.None;
        }
        else if (len == 5 && first == 'w' && prevWord.Equals("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            return PgSyntaxFlags.None;
        }
        else if (len == 6 && first is 'v' or 's'
            && (prevWord.Equals("VALUES", StringComparison.OrdinalIgnoreCase)
                || prevWord.Equals("SELECT", StringComparison.OrdinalIgnoreCase)))
        {
            return PgSyntaxFlags.None;
        }

        var flags = PgSyntaxFlags.FunctionCall;
        if ((len >= 11 && first == 'a' && prevWord.StartsWith("array_", StringComparison.OrdinalIgnoreCase))
            || (len >= 13 && first == 'p' && prevWord.StartsWith("pg_", StringComparison.OrdinalIgnoreCase)))
        {
            flags |= PgSyntaxFlags.UnsupportedCall;
        }

        return flags;
    }

    private static PgSyntaxFlags ClassifyWord(ReadOnlySpan<char> raw, bool isFirst, bool prevWasLimit, out bool isLimit)
    {
        isLimit = false;
        var flags = PgSyntaxFlags.None;
        var len = raw.Length;
        var first = (char)(raw[0] | 0x20);
        if (len >= 3 && first == 'p' && (raw[1] | 0x20) == 'g' && raw[2] == '_')
        {
            flags |= PgSyntaxFlags.PgPrefixWord;
        }

        if (isFirst)
        {
            if (len == 6 && first == 's' && raw.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                flags |= PgSyntaxFlags.SelectStart;
            }
            else if ((len == 6 && first == 'c' && raw.Equals("CREATE", StringComparison.OrdinalIgnoreCase))
                || (len == 5 && first == 'a' && raw.Equals("ALTER", StringComparison.OrdinalIgnoreCase)))
            {
                flags |= PgSyntaxFlags.CreateOrAlter;
            }
        }

        switch (len)
        {
            case 2:
                if (first == 'i' && (raw[1] | 0x20) == 's')
                {
                    flags |= PgSyntaxFlags.IsWord;
                }

                break;
            case 3:
                if (first == 'a')
                {
                    if (raw.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                    {
                        flags |= prevWasLimit ? PgSyntaxFlags.ArrayComparisonWord | PgSyntaxFlags.ClauseWord : PgSyntaxFlags.ArrayComparisonWord;
                    }
                    else if (raw.Equals("ANY", StringComparison.OrdinalIgnoreCase))
                    {
                        flags |= PgSyntaxFlags.ArrayComparisonWord;
                    }
                }
                else if (first == 'f' && raw.Equals("FOR", StringComparison.OrdinalIgnoreCase))
                {
                    flags |= PgSyntaxFlags.ClauseWord;
                }

                break;
            case 4:
                switch (first)
                {
                    case 's' when raw.Equals("SOME", StringComparison.OrdinalIgnoreCase):
                        flags |= PgSyntaxFlags.ArrayComparisonWord;
                        break;
                    case 'i' when raw.Equals("INTO", StringComparison.OrdinalIgnoreCase):
                        flags |= PgSyntaxFlags.IntoWord;
                        break;
                    case 'o' when raw.Equals("ONLY", StringComparison.OrdinalIgnoreCase):
                    case 'd' when raw.Equals("DATE", StringComparison.OrdinalIgnoreCase):
                    case 't' when raw.Equals("TIME", StringComparison.OrdinalIgnoreCase):
                        flags |= PgSyntaxFlags.ClauseWord;
                        break;
                }

                break;
            case 5:
                switch (first)
                {
                    case 'i' when raw.Equals("ILIKE", StringComparison.OrdinalIgnoreCase):
                        flags |= PgSyntaxFlags.PatternOperator;
                        break;
                    case 'l' when raw.Equals("LIMIT", StringComparison.OrdinalIgnoreCase):
                        isLimit = true;
                        break;
                    case 'a' when raw.Equals("ARRAY", StringComparison.OrdinalIgnoreCase):
                        flags |= PgSyntaxFlags.ClauseWord;
                        break;
                }

                break;
            case 7:
                if (first == 'c' && raw.Equals("COLLATE", StringComparison.OrdinalIgnoreCase))
                {
                    flags |= PgSyntaxFlags.ClauseWord;
                }

                break;
            case 8:
                if (first == 'i' && raw.Equals("INTERVAL", StringComparison.OrdinalIgnoreCase))
                {
                    flags |= PgSyntaxFlags.ClauseWord;
                }
                else if (first == 'o' && raw.Equals("OPERATOR", StringComparison.OrdinalIgnoreCase))
                {
                    flags |= PgSyntaxFlags.PatternOperator;
                }

                break;
            case 9:
                if (first == 't' && raw.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase))
                {
                    flags |= PgSyntaxFlags.ClauseWord;
                }

                break;
            case 11:
                if (first == 't' && raw.Equals("TIMESTAMPTZ", StringComparison.OrdinalIgnoreCase))
                {
                    flags |= PgSyntaxFlags.ClauseWord;
                }

                break;
            case 12:
                if (first is 'c' or 's' && (raw.Equals("current_user", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("session_user", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("current_role", StringComparison.OrdinalIgnoreCase)))
                {
                    flags |= PgSyntaxFlags.SpecialKeyword;
                }

                break;
            case 14:
                if (first is 'c' or 'l' && (raw.Equals("current_schema", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("localtimestamp", StringComparison.OrdinalIgnoreCase)))
                {
                    flags |= PgSyntaxFlags.SpecialKeyword;
                }

                break;
            case 15:
                if (first == 'c' && raw.Equals("current_catalog", StringComparison.OrdinalIgnoreCase))
                {
                    flags |= PgSyntaxFlags.SpecialKeyword;
                }

                break;
        }

        return flags;
    }
}
