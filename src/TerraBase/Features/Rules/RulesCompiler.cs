using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Features.Sql;
using TerraBase.Hosting;

namespace TerraBase.Features.Rules;

internal readonly union ConditionCompilation(CompiledCondition, InvalidInput);

internal readonly union ConditionPartCompilation(ConditionPart, InvalidInput);

public enum ConditionSource
{
    Uid,
    OrgId,
    OrgRole,
    IsAnonymous,
    Claim,
    ClaimList,
    RequestTime,
}

public sealed record ConditionParameter(string Name, ConditionSource Source, string? Key = null);

public readonly union ConditionPart(string, ConditionParameter);

public sealed record CompiledCondition(IReadOnlyList<ConditionPart> Parts, bool UsesNew)
{
    public bool UsesRequestTime { get; init; }
}

internal sealed record ParameterizedCondition(string Sql, ConditionParameter[] Parameters, bool HasClaimList)
{
    public bool CanUseDirectly(RulesAuth auth)
    {
        if (!HasClaimList)
        {
            return true;
        }

        for (var i = 0; i < Parameters.Length; i++)
        {
            var parameter = Parameters[i];
            if (parameter.Source == ConditionSource.ClaimList
                && !(parameter.Key is { } key && auth.Claims.TryGetValue(key, out var claim) && RulesCompiler.IsClaimList(claim)))
            {
                return false;
            }
        }

        return true;
    }
}

internal ref struct CharBuffer(Span<char> initial)
{
    private Span<char> _buffer = initial;
    private char[]? _rented;
    private int _length;

    public void Append(char value)
    {
        EnsureCapacity(1);
        _buffer[_length++] = value;
    }

    public void Append(string value) => Append(value.AsSpan());

    public void Append(scoped ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        EnsureCapacity(value.Length);
        value.CopyTo(_buffer[_length..]);
        _length += value.Length;
    }

    public readonly string BuildString() => new(_buffer[.._length]);

    public void Dispose()
    {
        if (_rented is { } rented)
        {
            _rented = null;
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private void EnsureCapacity(int additional)
    {
        if (_length + additional <= _buffer.Length)
        {
            return;
        }

        var nextSize = Math.Max(_buffer.Length * 2, _length + additional);
        var next = ArrayPool<char>.Shared.Rent(nextSize);
        _buffer[.._length].CopyTo(next);
        if (_rented is { } previous)
        {
            ArrayPool<char>.Shared.Return(previous);
        }

        _rented = next;
        _buffer = next;
    }
}

internal static class RulesCompiler
{
    public const int MaxCondition = 4096;

    private const int MaxCacheEntries = 4096;

    private const string ClaimPrefix = "auth.claims.";

    private static readonly FrozenSet<string> Operators = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "AND", "OR", "NOT", "IS", "NULL", "IN", "BETWEEN", "LIKE", "GLOB", "ESCAPE", "CASE", "WHEN", "THEN", "ELSE", "END", "COLLATE", "CAST", "AS", "TRUE", "FALSE");

    private static readonly FrozenSet<string> Types = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "INTEGER", "INT", "TEXT", "REAL", "BLOB", "NUMERIC");

    private static readonly FrozenSet<string> Collations = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "BINARY", "NOCASE", "RTRIM");

    private static readonly FrozenSet<string> Functions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "abs", "coalesce", "ifnull", "nullif", "iif", "length", "lower", "upper", "ltrim", "rtrim", "trim", "substr", "substring", "instr", "replace", "round",
        "typeof", "hex", "like", "glob");

    private static readonly ConcurrentDictionary<(string Condition, RulesOperation Operation), CachedCondition> Cache = new();

    private static int _cacheCount;

    public static ConditionCompilation Compile(string condition, IReadOnlyList<string>? columns, RulesOperation operation)
    {
        if (condition.Length <= MaxCondition && Cache.TryGetValue((condition, operation), out var cached))
        {
            if (columns is not null)
            {
                var referenced = cached.ReferencedColumns;
                for (var i = 0; i < referenced.Length; i++)
                {
                    if (!ContainsIgnoreCase(columns, referenced[i].Column))
                    {
                        return new InvalidInput(string.Concat(referenced[i].Reference, ": the table has no column ", referenced[i].Column), "unknown_column");
                    }
                }
            }

            return cached.Compiled;
        }

        if (condition.Length > MaxCondition || !SqlText.TryTokenize(condition, out var tokens) || tokens.Count == 0)
        {
            return Refused(string.Concat("a condition is a complete expression of at most ", MaxCondition.ToString(CultureInfo.InvariantCulture), " characters"));
        }

        List<ConditionPart> parts = new((tokens.Count * 2) + 2) { new ConditionPart("((") };
        List<bool> lists = [];
        List<(string Reference, string Column)>? referencedColumns = null;
        var usesNew = false;
        var usesRequestTime = false;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var previous = index > 0 ? tokens[index - 1] : default;
            var opens = index + 1 < tokens.Count && tokens[index + 1].Kind == SqlTokenKind.Open;
            if (token.Kind == SqlTokenKind.Word && index + 1 < tokens.Count && tokens[index + 1].Kind == SqlTokenKind.Dot)
            {
                var failure = Reference(tokens, ref index, columns, operation, previous.Is("IN"), HasTrue(lists), ref referencedColumns, ref usesNew, ref usesRequestTime) switch
                {
                    ConditionPart part => Append(parts, part),
                    InvalidInput refused => refused,
                };
                if (failure is not null)
                {
                    return failure;
                }

                continue;
            }

            if (previous.Is("IN") && token.Kind != SqlTokenKind.Open)
            {
                return Refused("IN takes a parenthesized list or one auth.claims list");
            }

            var piece = token.Kind switch
            {
                SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Blob or SqlTokenKind.Comma => condition[token.Start..token.End],
                SqlTokenKind.Open => "(",
                SqlTokenKind.Close => ")",
                SqlTokenKind.Symbol when token.Value[0] is '=' or '<' or '>' or '!' or '+' or '-' or '*' or '/' or '%' or '|' or '&' or '~' => token.Value,
                SqlTokenKind.Word when previous.Is("AS") => Types.Contains(token.Value) ? token.Value.ToUpperInvariant() : null,
                SqlTokenKind.Word when previous.Is("COLLATE") => Collations.Contains(token.Value) ? token.Value.ToUpperInvariant() : null,
                SqlTokenKind.Word when opens && Functions.Contains(token.Value) => token.Value.ToLowerInvariant(),
                SqlTokenKind.Word when Operators.Contains(token.Value) => token.Value.ToUpperInvariant(),
                SqlTokenKind.Word or SqlTokenKind.Quoted or SqlTokenKind.Parameter or SqlTokenKind.Semicolon or SqlTokenKind.Dot or SqlTokenKind.Symbol => null,
                _ => throw new UnreachableException(),
            };
            if (piece is null)
            {
                return Refused(token.Kind switch
                {
                    SqlTokenKind.Parameter => "conditions take no parameters",
                    SqlTokenKind.Semicolon => "a condition is one expression",
                    SqlTokenKind.Word or SqlTokenKind.Quoted when !opens => string.Concat(token.Value, " must be qualified as row.", token.Value, " or new.", token.Value),
                    SqlTokenKind.Word when opens => string.Concat("conditions cannot call ", token.Value, "; only deterministic functions without side effects are allowed"),
                    SqlTokenKind.Word or SqlTokenKind.Quoted or SqlTokenKind.Dot or SqlTokenKind.Symbol or SqlTokenKind.String or SqlTokenKind.Number
                        or SqlTokenKind.Blob or SqlTokenKind.Comma or SqlTokenKind.Open or SqlTokenKind.Close => string.Concat("conditions cannot use ", token.Value),
                    _ => throw new UnreachableException(),
                });
            }

            if (token.Kind == SqlTokenKind.Open)
            {
                lists.Add(previous.Is("IN"));
            }
            else if (token.Kind == SqlTokenKind.Close)
            {
                if (lists.Count == 0)
                {
                    return Refused("the condition has unbalanced parentheses");
                }

                lists.RemoveAt(lists.Count - 1);
            }

            parts.Add(new ConditionPart(" "));
            parts.Add(new ConditionPart(piece));
        }

        if (lists.Count != 0)
        {
            return Refused("the condition has unbalanced parentheses");
        }

        parts.Add(new ConditionPart(") IS TRUE)"));
        var compacted = Compact(parts);
        var compiled = new CompiledCondition(compacted, usesNew) { UsesRequestTime = usesRequestTime };
        var entry = new CachedCondition(compiled, referencedColumns is null ? [] : [.. referencedColumns]);
        Store((condition, operation), entry);
        return compiled;
    }

    private static ConditionPart[] Compact(List<ConditionPart> parts)
    {
        List<ConditionPart> compacted = new(parts.Count);
        StringBuilder? pending = null;
        string? pendingSingle = null;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var plain = part switch
            {
                string literal when !literal.StartsWith("row.", StringComparison.Ordinal) && !literal.StartsWith("new.", StringComparison.Ordinal) => literal,
                string or ConditionParameter => null,
            };
            if (plain is not null)
            {
                if (pending is not null)
                {
                    pending.Append(plain);
                }
                else if (pendingSingle is not null)
                {
                    pending = new StringBuilder(pendingSingle.Length + plain.Length + 16);
                    pending.Append(pendingSingle);
                    pending.Append(plain);
                    pendingSingle = null;
                }
                else
                {
                    pendingSingle = plain;
                }

                continue;
            }

            FlushPending(compacted, ref pending, ref pendingSingle);
            compacted.Add(part);
        }

        FlushPending(compacted, ref pending, ref pendingSingle);
        return [.. compacted];
    }

    private static void FlushPending(List<ConditionPart> compacted, ref StringBuilder? pending, ref string? pendingSingle)
    {
        if (pending is not null)
        {
            compacted.Add(new ConditionPart(pending.ToString()));
            pending = null;
        }
        else if (pendingSingle is not null)
        {
            compacted.Add(new ConditionPart(pendingSingle));
            pendingSingle = null;
        }
    }

    private static void Store((string Condition, RulesOperation Operation) key, CachedCondition entry)
    {
        if (Volatile.Read(ref _cacheCount) >= MaxCacheEntries)
        {
            Cache.Clear();
            Volatile.Write(ref _cacheCount, 0);
        }

        if (Cache.TryAdd(key, entry))
        {
            Interlocked.Increment(ref _cacheCount);
        }
    }

    private static InvalidInput? Append(List<ConditionPart> parts, ConditionPart part)
    {
        parts.Add(new ConditionPart(" "));
        parts.Add(part);
        return null;
    }

    public static string Render(CompiledCondition condition, RulesAuth auth, long? requestTime, string? newPrefix = null, string? rowPrefix = null)
    {
        Span<char> initial = stackalloc char[512];
        var builder = new CharBuffer(initial);
        try
        {
            AppendCondition(ref builder, condition, auth, requestTime, newPrefix, rowPrefix);
            return builder.BuildString();
        }
        finally
        {
            builder.Dispose();
        }
    }

    public static void AppendCondition(
        ref CharBuffer builder,
        CompiledCondition condition,
        RulesAuth auth,
        long? requestTime,
        string? newPrefix = null,
        string? rowPrefix = null)
    {
        if (condition.Parts is ConditionPart[] array)
        {
            for (var i = 0; i < array.Length; i++)
            {
                AppendPart(ref builder, array[i], auth, requestTime, newPrefix, rowPrefix);
            }
        }
        else
        {
            var count = condition.Parts.Count;
            for (var i = 0; i < count; i++)
            {
                AppendPart(ref builder, condition.Parts[i], auth, requestTime, newPrefix, rowPrefix);
            }
        }
    }

    public static ParameterizedCondition CompileParameterized(CompiledCondition condition, string? newPrefix = null, string? rowPrefix = null) =>
        BuildParameterized(condition, auth: null, newPrefix, rowPrefix);

    public static ParameterizedCondition RenderParameterized(CompiledCondition condition, RulesAuth auth, string? newPrefix = null, string? rowPrefix = null) =>
        BuildParameterized(condition, auth, newPrefix, rowPrefix);

    public static SqlValue BindParameter(ConditionParameter parameter, RulesAuth auth, long? requestTime) => parameter.Source switch
    {
        ConditionSource.Uid => SqlValue.FromText(auth.Uid),
        ConditionSource.OrgId => auth.OrgId is { } orgId ? SqlValue.FromText(orgId) : SqlValue.Null,
        ConditionSource.OrgRole => auth.OrgRole is { } orgRole ? SqlValue.FromText(orgRole) : SqlValue.Null,
        ConditionSource.IsAnonymous => SqlValue.FromIntegral(auth.IsAnonymous ? 1 : 0),
        ConditionSource.RequestTime => requestTime is { } milliseconds ? SqlValue.FromIntegral(milliseconds) : SqlValue.Null,
        ConditionSource.Claim => parameter.Key is { } key && auth.Claims.TryGetValue(key, out var claim) ? ClaimValue(claim) : SqlValue.Null,
        ConditionSource.ClaimList => parameter.Key is { } listKey && auth.Claims.TryGetValue(listKey, out var list) && IsClaimList(list)
            ? SqlValue.FromText(ClaimJson(list))
            : SqlValue.Null,
        _ => throw new UnreachableException(),
    };

    public static void BindParameters(ConditionParameter[] parameters, RulesAuth auth, long? requestTime, Span<SqlValue> destination)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            destination[i] = BindParameter(parameters[i], auth, requestTime);
        }
    }

    internal static bool IsClaimList(RulesClaim claim) => claim switch
    {
        IReadOnlyList<RulesScalar> => true,
        string or long or double => false,
    };

    private static ParameterizedCondition BuildParameterized(CompiledCondition condition, RulesAuth? auth, string? newPrefix, string? rowPrefix)
    {
        Span<char> initial = stackalloc char[512];
        var builder = new CharBuffer(initial);
        List<ConditionParameter>? bound = null;
        var hasClaimList = false;
        try
        {
            if (condition.Parts is ConditionPart[] array)
            {
                for (var i = 0; i < array.Length; i++)
                {
                    AppendParameterizedPart(ref builder, array[i], auth, newPrefix, rowPrefix, ref bound, ref hasClaimList);
                }
            }
            else
            {
                var count = condition.Parts.Count;
                for (var i = 0; i < count; i++)
                {
                    AppendParameterizedPart(ref builder, condition.Parts[i], auth, newPrefix, rowPrefix, ref bound, ref hasClaimList);
                }
            }

            return new ParameterizedCondition(builder.BuildString(), bound is null ? [] : [.. bound], hasClaimList);
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static void AppendParameterizedPart(
        ref CharBuffer builder,
        ConditionPart part,
        RulesAuth? auth,
        string? newPrefix,
        string? rowPrefix,
        ref List<ConditionParameter>? bound,
        ref bool hasClaimList)
    {
        _ = part switch
        {
            string literal when literal.StartsWith("new.", StringComparison.Ordinal) => AppendPrefixed(ref builder, newPrefix, literal.AsSpan(4)),
            string literal when literal.StartsWith("row.", StringComparison.Ordinal) => AppendPrefixed(ref builder, rowPrefix, literal.AsSpan(4)),
            string literal => AppendSpan(ref builder, literal),
            ConditionParameter parameter => AppendParameterizedParameter(ref builder, parameter, auth, ref bound, ref hasClaimList),
        };
    }

    private static bool AppendParameterizedParameter(
        ref CharBuffer builder,
        ConditionParameter parameter,
        RulesAuth? auth,
        ref List<ConditionParameter>? bound,
        ref bool hasClaimList)
    {
        if (parameter.Source == ConditionSource.ClaimList)
        {
            hasClaimList = true;
            if (auth is null || (parameter.Key is { } listKey && auth.Claims.TryGetValue(listKey, out var list) && IsClaimList(list)))
            {
                builder.Append("(SELECT value FROM json_each(?))");
                bound ??= [];
                bound.Add(parameter);
            }
            else
            {
                builder.Append("(SELECT NULL)");
            }

            return true;
        }

        builder.Append('?');
        bound ??= [];
        bound.Add(parameter);
        return true;
    }

    private static void AppendPart(ref CharBuffer builder, ConditionPart part, RulesAuth auth, long? requestTime, string? newPrefix, string? rowPrefix)
    {
        _ = part switch
        {
            string literal when literal.StartsWith("new.", StringComparison.Ordinal) => AppendPrefixed(ref builder, newPrefix, literal.AsSpan(4)),
            string literal when literal.StartsWith("row.", StringComparison.Ordinal) => AppendPrefixed(ref builder, rowPrefix, literal.AsSpan(4)),
            string literal => AppendSpan(ref builder, literal),
            ConditionParameter parameter => AppendParameter(ref builder, parameter, auth, requestTime),
        };
    }

    private static bool AppendPrefixed(ref CharBuffer builder, string? prefix, ReadOnlySpan<char> value)
    {
        if (prefix is not null)
        {
            builder.Append(prefix);
        }

        builder.Append(value);
        return true;
    }

    private static bool AppendSpan(ref CharBuffer builder, ReadOnlySpan<char> value)
    {
        builder.Append(value);
        return true;
    }

    private static bool AppendParameter(ref CharBuffer builder, ConditionParameter parameter, RulesAuth auth, long? requestTime)
    {
        switch (parameter.Source)
        {
            case ConditionSource.Uid:
                AppendLiteral(ref builder, auth.Uid);
                break;
            case ConditionSource.OrgId:
                AppendLiteral(ref builder, auth.OrgId);
                break;
            case ConditionSource.OrgRole:
                AppendLiteral(ref builder, auth.OrgRole);
                break;
            case ConditionSource.IsAnonymous:
                builder.Append(auth.IsAnonymous ? '1' : '0');
                break;
            case ConditionSource.RequestTime:
                if (requestTime is { } milliseconds)
                {
                    AppendInt64(ref builder, milliseconds);
                }
                else
                {
                    builder.Append("NULL");
                }

                break;
            case ConditionSource.Claim:
                if (parameter.Key is { } key && auth.Claims.TryGetValue(key, out var claim))
                {
                    AppendClaim(ref builder, claim);
                }
                else
                {
                    builder.Append("NULL");
                }

                break;
            case ConditionSource.ClaimList:
                if (parameter.Key is { } listKey && auth.Claims.TryGetValue(listKey, out var list) && IsClaimList(list))
                {
                    builder.Append("(SELECT value FROM json_each(");
                    AppendLiteral(ref builder, ClaimJson(list));
                    builder.Append("))");
                }
                else
                {
                    builder.Append("(SELECT NULL)");
                }

                break;
            default:
                throw new UnreachableException();
        }

        return true;
    }

    private static void AppendClaim(ref CharBuffer builder, RulesClaim claim)
    {
        _ = claim switch
        {
            string text => AppendLiteralValue(ref builder, text),
            long integer => AppendInt64Value(ref builder, integer),
            double real => AppendSpan(ref builder, real.ToString("R", CultureInfo.InvariantCulture)),
            IReadOnlyList<RulesScalar> => AppendLiteralValue(ref builder, ClaimJson(claim)),
        };
    }

    private static bool AppendLiteralValue(ref CharBuffer builder, string? text)
    {
        AppendLiteral(ref builder, text);
        return true;
    }

    private static bool AppendInt64Value(ref CharBuffer builder, long value)
    {
        AppendInt64(ref builder, value);
        return true;
    }

    private static void AppendInt64(ref CharBuffer builder, long value)
    {
        Span<char> scratch = stackalloc char[24];
        if (value.TryFormat(scratch, out var written, default, CultureInfo.InvariantCulture))
        {
            builder.Append(scratch[..written]);
        }
        else
        {
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendLiteral(ref CharBuffer builder, string? text)
    {
        if (text is null)
        {
            builder.Append("NULL");
            return;
        }

        builder.Append('\'');
        var span = text.AsSpan();
        var quote = span.IndexOf('\'');
        if (quote < 0)
        {
            builder.Append(span);
        }
        else
        {
            while (quote >= 0)
            {
                builder.Append(span[..quote]);
                builder.Append("''");
                span = span[(quote + 1)..];
                quote = span.IndexOf('\'');
            }

            builder.Append(span);
        }

        builder.Append('\'');
    }

    public static string Quote(string identifier) => string.Concat("\"", identifier.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");

    public static string Literal(string? text) => text is null ? "NULL" : string.Concat("'", text.Replace("'", "''", StringComparison.Ordinal), "'");

    public static bool IsSetValue(string value)
    {
        if (value is "auth.uid" or "auth.org_id" or "auth.org_role" or "auth.is_anonymous" or "request.time" or "request.ulid"
            || (value.StartsWith(ClaimPrefix, StringComparison.Ordinal) && IsClaimName(value.AsSpan(ClaimPrefix.Length))))
        {
            return true;
        }

        return SqlText.TryTokenize(value, out var tokens) && tokens switch
        {
            [{ Kind: SqlTokenKind.String or SqlTokenKind.Number }] => true,
            [{ Kind: SqlTokenKind.Blob } blob] => IsHex(blob.Value),
            [{ Kind: SqlTokenKind.Word } word] => word.Is("NULL") || word.Is("TRUE") || word.Is("FALSE"),
            [{ Kind: SqlTokenKind.Symbol, Value: "-" }, { Kind: SqlTokenKind.Number }] => true,
            _ => false,
        };
    }

    public static SqlValue SetValue(string expression, RulesAuth auth, long? requestTime, Ulid requestUlid) => expression switch
    {
        "auth.uid" => SqlValue.FromText(auth.Uid),
        "auth.org_id" => auth.OrgId is { } organization ? SqlValue.FromText(organization) : SqlValue.Null,
        "auth.org_role" => auth.OrgRole is { } role ? SqlValue.FromText(role) : SqlValue.Null,
        "auth.is_anonymous" => SqlValue.FromIntegral(auth.IsAnonymous ? 1 : 0),
        "request.time" => requestTime is { } milliseconds ? SqlValue.FromIntegral(milliseconds) : SqlValue.Null,
        "request.ulid" => SqlValue.FromText(requestUlid.ToString()),
        _ when expression.StartsWith(ClaimPrefix, StringComparison.Ordinal) =>
            auth.Claims.TryGetValue(expression[ClaimPrefix.Length..], out var claim) ? ClaimValue(claim) : SqlValue.Null,
        _ => LiteralValue(expression),
    };

    private static SqlValue ClaimValue(RulesClaim claim) => claim switch
    {
        string text => SqlValue.FromText(text),
        long integer => SqlValue.FromIntegral(integer),
        double real => SqlValue.FromReal(real),
        IReadOnlyList<RulesScalar> => SqlValue.FromText(ClaimJson(claim)),
    };

    private static SqlValue LiteralValue(string expression)
    {
        if (!SqlText.TryTokenize(expression, out var tokens))
        {
            return SqlValue.Null;
        }

        return tokens switch
        {
            [{ Kind: SqlTokenKind.String } text] => SqlValue.FromText(text.Value),
            [{ Kind: SqlTokenKind.Number } number] => Number(number.Value, negative: false),
            [{ Kind: SqlTokenKind.Symbol, Value: "-" }, { Kind: SqlTokenKind.Number } number] => Number(number.Value, negative: true),
            [{ Kind: SqlTokenKind.Blob } blob] when IsHex(blob.Value) => SqlValue.FromBlob(Convert.FromHexString(blob.Value)),
            [{ Kind: SqlTokenKind.Word } word] when word.Is("TRUE") => SqlValue.FromIntegral(1),
            [{ Kind: SqlTokenKind.Word } word] when word.Is("FALSE") => SqlValue.FromIntegral(0),
            _ => SqlValue.Null,
        };
    }

    private static bool IsHex(string text)
    {
        if (text.Length % 2 != 0)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsAsciiHexDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static SqlValue Number(string text, bool negative) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var integer) ? SqlValue.FromIntegral(negative ? -integer : integer)
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? SqlValue.FromReal(negative ? -real : real)
        : SqlValue.Null;

    private static string ClaimJson(RulesClaim claim) => claim switch
    {
        IReadOnlyList<RulesScalar> items => JoinScalars(items),
        string text => string.Concat("[", Scalar((RulesScalar)text), "]"),
        long integer => string.Concat("[", integer.ToString(CultureInfo.InvariantCulture), "]"),
        double real => string.Concat("[", real.ToString("R", CultureInfo.InvariantCulture), "]"),
    };

    private static string JoinScalars(IReadOnlyList<RulesScalar> items)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Scalar(items[i]));
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string Scalar(RulesScalar scalar) => scalar switch
    {
        string text => JsonSerializer.Serialize(text, RulesScalarJson.Default.String),
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        double real => real.ToString("R", CultureInfo.InvariantCulture),
    };

    private static ConditionPartCompilation Reference(
        List<SqlToken> tokens,
        ref int index,
        IReadOnlyList<string>? columns,
        RulesOperation operation,
        bool listPosition,
        bool inList,
        ref List<(string Reference, string Column)>? referencedColumns,
        ref bool usesNew,
        ref bool usesRequestTime)
    {
        var head = tokens[index].Value;
        List<SqlToken> path = [];
        var at = index;
        while (at + 2 < tokens.Count && tokens[at + 1].Kind == SqlTokenKind.Dot && tokens[at + 2].IsName)
        {
            path.Add(tokens[at + 2]);
            at += 2;
        }

        if (path.Count == 0 || (at + 1 < tokens.Count && tokens[at + 1].Kind is SqlTokenKind.Dot or SqlTokenKind.Open))
        {
            return Refused(string.Concat(head, ". starts no reference a condition can use"));
        }

        index = at;
        var name = path.Count == 1 ? path[0].Value : JoinPath(path);
        var reference = string.Concat(head, ".", name);
        var isClaim = head == "auth" && path is [{ Kind: SqlTokenKind.Word, Value: "claims" }, { Kind: SqlTokenKind.Word } claim] && IsClaimName(claim.Value.AsSpan());
        if ((listPosition && !isClaim) || (inList && isClaim))
        {
            return Refused(listPosition ? "IN takes a parenthesized list or one auth.claims list" : string.Concat(reference, " is a list only as the whole right side of IN"));
        }

        switch (head)
        {
            case "row" or "new" when path.Count == 1:
                if (columns is not null && !ContainsIgnoreCase(columns, path[0].Value))
                {
                    return new InvalidInput(string.Concat(reference, ": the table has no column ", path[0].Value), "unknown_column");
                }

                var allowed = head == "row"
                    ? operation is RulesOperation.Read or RulesOperation.Update or RulesOperation.Delete
                    : operation is RulesOperation.Create or RulesOperation.Update or RulesOperation.Validate;
                if (!allowed)
                {
                    return new InvalidInput(string.Concat(head, ".* is not allowed in ", Name(operation)), "reference_not_allowed");
                }

                referencedColumns ??= [];
                referencedColumns.Add((reference, path[0].Value));
                if (head == "new")
                {
                    usesNew = true;
                    return new ConditionPart(string.Concat("new.", Quote(path[0].Value)));
                }

                return new ConditionPart(string.Concat("row.", Quote(path[0].Value)));
            case "auth" when path is [{ Kind: SqlTokenKind.Word } field]:
                return field.Value switch
                {
                    "uid" => new ConditionPart(new ConditionParameter("auth_uid", ConditionSource.Uid)),
                    "org_id" => new ConditionPart(new ConditionParameter("auth_org_id", ConditionSource.OrgId)),
                    "org_role" => new ConditionPart(new ConditionParameter("auth_org_role", ConditionSource.OrgRole)),
                    "is_anonymous" => new ConditionPart(new ConditionParameter("auth_is_anonymous", ConditionSource.IsAnonymous)),
                    _ => Refused(string.Concat(reference, " is not an auth value; use auth.uid, auth.org_id, auth.org_role, auth.is_anonymous or auth.claims.<name>")),
                };
            case "auth" when isClaim && listPosition:
                return new ConditionPart(new ConditionParameter(string.Concat("auth_list_", path[1].Value), ConditionSource.ClaimList, path[1].Value));
            case "auth" when isClaim:
                return new ConditionPart(new ConditionParameter(string.Concat("auth_claim_", path[1].Value), ConditionSource.Claim, path[1].Value));
            case "request" when name == "time":
                if (operation == RulesOperation.Read)
                {
                    return new InvalidInput("request.time is not allowed in read", "reference_not_allowed");
                }

                usesRequestTime = true;
                return new ConditionPart(new ConditionParameter("request_time", ConditionSource.RequestTime));
            case "request" when name == "ulid":
                return new InvalidInput("request.ulid is allowed only as a set.create value", "reference_not_allowed");
            default:
                return Refused(string.Concat(reference, " is not a reference; use row.<column>, new.<column>, auth.<value> or request.time"));
        }
    }

    private static string JoinPath(List<SqlToken> path)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < path.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('.');
            }

            sb.Append(path[i].Value);
        }

        return sb.ToString();
    }

    private static bool ContainsIgnoreCase(IReadOnlyList<string> columns, string value)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasTrue(List<bool> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i])
            {
                return true;
            }
        }

        return false;
    }

    private static string Name(RulesOperation operation) => operation switch
    {
        RulesOperation.Read => "read",
        RulesOperation.Create => "create",
        RulesOperation.Update => "update",
        RulesOperation.Delete => "delete",
        RulesOperation.Validate => "validate",
        _ => throw new UnreachableException(),
    };

    private static bool IsClaimName(ReadOnlySpan<char> name)
    {
        if (name.Length is < 1 or > 63 || name[0] is not (>= 'a' and <= 'z'))
        {
            return false;
        }

        for (var i = 1; i < name.Length; i++)
        {
            if (name[i] is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static InvalidInput Refused(string message) => new(message, "unsafe_where");

    private sealed record CachedCondition(CompiledCondition Compiled, (string Reference, string Column)[] ReferencedColumns);
}

[JsonSerializable(typeof(string))]
internal sealed partial class RulesScalarJson : JsonSerializerContext;
