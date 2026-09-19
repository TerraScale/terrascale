using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard2;

/// <summary>
/// Port of compileAccessRule/evaluateAccessRule from
/// src/features/access-rules/access-rules-data.ts. Compiles a safe CEL subset
/// (lexer + recursive-descent parser with line/column diagnostics) and
/// evaluates it against a claims-only context. Arbitrary code can never enter
/// the rule. Parser internals (token, expression tree) are private to this
/// file; callers only see diagnostics and index analysis.
/// </summary>
public static partial class AccessRuleCompiler
{
    public static AccessRuleCompileResult Compile(string? source, ImmutableArray<AccessRuleIndex> indexes = default)
    {
        var text = source ?? "";
        var lexer = new Lexer(text);
        var tokens = lexer.ReadAll();
        if (lexer.Diagnostics.Count > 0)
            return InvalidCompilation(text, [.. lexer.Diagnostics], indexes);

        var parser = new Parser(tokens);
        var expression = parser.Parse();
        if (expression is null || parser.Diagnostics.Count > 0)
            return InvalidCompilation(text, [.. parser.Diagnostics], indexes);

        string[] referenced = [.. CollectDocumentFields(expression, []).Distinct().Order()];
        return new AccessRuleCompileResult(true, text, [], AnalyzeIndexes(referenced, indexes));
    }

    public static AccessRuleEvaluation Evaluate(string? source, AccessRuleEvaluationContext context) =>
        Compile(source) is { Ok: false, Diagnostics: var diagnostics }
            ? new AccessRuleEvaluation(false, "deny", diagnostics.IsEmpty ? null : diagnostics[0])
            : EvaluateExpression(source ?? "", SanitizeContext(context));

    // --- Index analysis ------------------------------------------------------

    private static AccessRuleIndexAnalysis AnalyzeIndexes(string[] referencedFields, ImmutableArray<AccessRuleIndex> indexes)
    {
        var available = indexes.IsDefault
            ? new HashSet<string>()
            : [.. indexes.SelectMany(i => i.Fields.Select(NormalizeField))];
        var required = referencedFields.Where(f => !available.Contains(f)).ToArray();
        return new AccessRuleIndexAnalysis(
            required,
            [.. required.Select(f => $"access_rule_{f.Replace('.', '_')}")],
            referencedFields);
    }

    private static AccessRuleCompileResult InvalidCompilation(
        string source, ImmutableArray<AccessRuleDiagnostic> diagnostics, ImmutableArray<AccessRuleIndex> indexes)
    {
        string[] referenced = diagnostics.IsEmpty
            ? []
            : [.. ExtractDocumentFields().Matches(source).Select(m => NormalizeField($"document.{m.Groups[1].Value}")).Distinct().Order()];
        return new AccessRuleCompileResult(
            false,
            source,
            diagnostics.IsEmpty
                ? [new(AccessRuleDiagnosticCode.InvalidExpression, "The rule must be a boolean expression.", 1, 1)]
                : diagnostics,
            AnalyzeIndexes(referenced, indexes));
    }

    private static string NormalizeField(string path) =>
        path.StartsWith("document.", StringComparison.Ordinal) ? path["document.".Length..] : path;

    [GeneratedRegex(@"\bdocument\.([A-Za-z_][A-Za-z0-9_.]*)")]
    private static partial Regex ExtractDocumentFields();

    // --- Evaluation ----------------------------------------------------------

    private static AccessRuleEvaluation EvaluateExpression(string source, AccessRuleEvaluationContext context)
    {
        // Recompiled here (cheap): the expression tree never leaves the compiler.
        var parser = new Parser(new Lexer(source).ReadAll());
        var expression = parser.Parse();
        try
        {
            var value = ValueOf(expression!, context);
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new AccessRuleEvaluation(false, "deny", new(
                    AccessRuleDiagnosticCode.NonBooleanResult, "Access rules must evaluate to true or false.", 1, 1));
            return new AccessRuleEvaluation(
                value.ValueKind == JsonValueKind.True,
                value.ValueKind == JsonValueKind.True ? "allow" : "deny");
        }
        catch (Exception)
        {
            return new AccessRuleEvaluation(false, "deny", new(
                AccessRuleDiagnosticCode.InvalidExpression, "The rule could not be evaluated for this context.", 1, 1));
        }
    }

    private static JsonElement ValueOf(Expression expression, AccessRuleEvaluationContext context)
    {
        switch (expression.Kind)
        {
            case "literal":
                return expression.Literal;
            case "path":
                return ReadPath(context, expression.Path);
            case "list":
                return JsonSerializer.SerializeToElement(expression.Values.Select(v => (object?)ValueOf(v, context)).ToArray());
            case "has":
            {
                var value = ValueOf(expression.Value!, context);
                return (value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)).AsJson();
            }
            case "unary":
                return (!Truthy(ValueOf(expression.Value!, context))).AsJson();
            case "binary":
            {
                if (expression.Operator == "&&")
                    return (Truthy(ValueOf(expression.Left!, context)) && Truthy(ValueOf(expression.Right!, context))).AsJson();
                if (expression.Operator == "||")
                    return (Truthy(ValueOf(expression.Left!, context)) || Truthy(ValueOf(expression.Right!, context))).AsJson();
                var left = ValueOf(expression.Left!, context);
                var right = ValueOf(expression.Right!, context);
                if (expression.Operator == "==")
                    return Equal(left, right).AsJson();
                if (expression.Operator == "!=")
                    return (!Equal(left, right)).AsJson();
                if (expression.Operator == "in")
                    return (right.ValueKind == JsonValueKind.Array
                        && right.EnumerateArray().Any(item => Equal(left, item))).AsJson();
                return default;
            }
            default:
                return default;
        }
    }

    private static JsonElement ReadPath(AccessRuleEvaluationContext context, string path)
    {
        var segments = path.Split('.');
        var current = segments[0] switch
        {
            "principal" => context.Principal,
            "document" => context.Document,
            "auth" => context.Auth,
            _ => default,
        };
        foreach (var segment in segments[1..])
            current = current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var property)
                ? property
                : default;
        return current;
    }

    /// <summary>JSON.stringify equality: undefined equals only undefined,
    /// null equals only null, everything else compares serialized text.</summary>
    private static bool Equal(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Undefined || right.ValueKind == JsonValueKind.Undefined)
            return left.ValueKind == JsonValueKind.Undefined && right.ValueKind == JsonValueKind.Undefined;
        return JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    }

    private static bool Truthy(JsonElement value) => value.ValueKind == JsonValueKind.True;

    private static AccessRuleEvaluationContext SanitizeContext(AccessRuleEvaluationContext context) => new(
        StripSecrets(context.Principal),
        StripSecrets(context.Document),
        context.Auth);

    private static JsonElement StripSecrets(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return value;
        var safe = value.EnumerateObject().Where(p => !SecretKey().IsMatch(p.Name))
            .ToDictionary(p => p.Name, p => p.Value);
        return JsonSerializer.SerializeToElement(safe);
    }

    [GeneratedRegex("token|secret|password|private.?key|authorization", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKey();

    private static JsonElement AsJson(this bool value) =>
        JsonSerializer.SerializeToElement(value);

    private static IEnumerable<string> CollectDocumentFields(Expression expression, List<string> result)
    {
        if (expression.Kind == "path" && expression.Path.StartsWith("document.", StringComparison.Ordinal))
            result.Add(NormalizeField(expression.Path));
        if (expression.Kind == "list")
            foreach (var value in expression.Values)
                CollectDocumentFields(value, result);
        if (expression.Kind is "unary" or "has")
            CollectDocumentFields(expression.Value!, result);
        if (expression.Kind == "binary")
        {
            CollectDocumentFields(expression.Left!, result);
            CollectDocumentFields(expression.Right!, result);
        }
        return result;
    }

    // --- Expression tree (private to the compiler) ---------------------------

    private sealed record Expression(
        string Kind,
        JsonElement Literal = default,
        string Path = "",
        ImmutableArray<Expression> Values = default,
        Expression? Value = null,
        string Operator = "",
        Expression? Left = null,
        Expression? Right = null);

    private sealed record Token(string Kind, string Value, int Line, int Column);

    // --- Lexer (port of class Lexer) ------------------------------------------

    private sealed class Lexer(string source)
    {
        private readonly string _source = source;
        private int _index, _line = 1, _column = 1;

        public List<AccessRuleDiagnostic> Diagnostics { get; } = [];

        public List<Token> ReadAll()
        {
            var tokens = new List<Token>();
            while (_index < _source.Length)
            {
                var ch = _source[_index];
                if (char.IsWhiteSpace(ch))
                {
                    Advance();
                    continue;
                }
                var (line, column) = (_line, _column);
                if (char.IsAsciiLetter(ch) || ch == '_')
                {
                    var start = _index;
                    while (_index < _source.Length && (char.IsAsciiLetterOrDigit(_source[_index]) || _source[_index] == '.' || _source[_index] == '_'))
                        Advance();
                    var value = _source[start.._index];
                    tokens.Add(new(
                        value is "true" or "false" ? "boolean" : value == "null" ? "null" : value == "in" ? "operator" : "identifier",
                        value, line, column));
                    continue;
                }
                if (char.IsAsciiDigit(ch))
                {
                    var start = _index;
                    while (_index < _source.Length && (char.IsAsciiDigit(_source[_index]) || _source[_index] == '.'))
                        Advance();
                    tokens.Add(new("number", _source[start.._index], line, column));
                    continue;
                }
                if (ch is '"' or '\'')
                {
                    Advance();
                    var value = "";
                    var closed = false;
                    while (_index < _source.Length)
                    {
                        var next = _source[_index];
                        if (next == ch)
                        {
                            Advance();
                            closed = true;
                            break;
                        }
                        if (next == '\\')
                        {
                            Advance();
                            if (_index < _source.Length)
                            {
                                value += _source[_index];
                                Advance();
                            }
                        }
                        else
                        {
                            value += next;
                            Advance();
                        }
                    }
                    if (!closed)
                        Diagnostics.Add(new(AccessRuleDiagnosticCode.UnterminatedString, "String literal is not closed.", line, column));
                    else
                        tokens.Add(new("string", value, line, column));
                    continue;
                }
                var pair = _index + 1 < _source.Length ? _source.Substring(_index, 2) : "";
                if (pair is "&&" or "||" or "==" or "!=")
                {
                    Advance();
                    Advance();
                    tokens.Add(new("operator", pair, line, column));
                    continue;
                }
                if (ch is '!' or '(' or ')' or '[' or ']' or ',')
                {
                    Advance();
                    tokens.Add(new("operator", ch.ToString(), line, column));
                    continue;
                }
                Diagnostics.Add(new(
                    AccessRuleDiagnosticCode.InvalidCharacter,
                    $"Character {JsonStringify(ch)} is not allowed in CEL rules.",
                    line, column));
                Advance();
            }
            tokens.Add(new("eof", "", _line, _column));
            return tokens;
        }

        private void Advance()
        {
            var ch = _source[_index];
            _index++;
            if (ch == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
        }

        private static string JsonStringify(char ch) => ch switch
        {
            '"' => "\\\"",
            '\\' => "\\\\",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ when char.IsControl(ch) => $"\\u{(int)ch:x4}",
            _ => ch.ToString(),
        };
    }

    // --- Parser (port of class Parser) ----------------------------------------

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;

        public List<AccessRuleDiagnostic> Diagnostics { get; } = [];

        public Expression? Parse()
        {
            if (Peek().Kind == "eof")
            {
                Diagnostics.Add(new(AccessRuleDiagnosticCode.MissingExpression, "Enter a CEL access rule.", 1, 1));
                return null;
            }
            var result = ParseOr();
            if (Peek().Kind != "eof")
                Fail(AccessRuleDiagnosticCode.UnexpectedToken, $"Unexpected token {JsonStringify(Peek().Value)}.");
            return result;
        }

        private Expression? ParseOr() => Fold("||", ParseAnd);

        private Expression? ParseAnd() => Fold("&&", ParseComparison);

        private Expression? ParseComparison()
        {
            var left = ParseUnary();
            if (left is null)
                return null;
            var token = Peek();
            if (token.Value is "==" or "!=" or "in")
            {
                _index++;
                var right = ParseUnary();
                if (right is null)
                    return null;
                left = new("binary", Operator: token.Value, Left: left, Right: right);
            }
            return left;
        }

        private Expression? ParseUnary() =>
            Peek().Value == "!"
                ? (ParseUnary() is { } value ? new("unary", Value: value) : null)
                : ParsePrimary();

        private Expression? ParsePrimary()
        {
            var token = Peek();
            switch (token.Kind)
            {
                case "string":
                    _index++;
                    return new("literal", Literal: JsonSerializer.SerializeToElement(token.Value));
                case "number":
                    _index++;
                    if (!double.TryParse(token.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
                    {
                        Fail(AccessRuleDiagnosticCode.InvalidExpression, "Number literal is invalid.");
                        return null;
                    }
                    return new("literal", Literal: JsonSerializer.SerializeToElement(number));
                case "boolean":
                    _index++;
                    return new("literal", Literal: (token.Value == "true").AsJson());
                case "null":
                    _index++;
                    return new("literal", Literal: JsonSerializer.SerializeToElement((object?)null));
                case "identifier":
                    _index++;
                    if (Peek().Value == "(")
                    {
                        _index++;
                        var value = ParseOr();
                        if (Peek().Value != ")")
                        {
                            Fail(AccessRuleDiagnosticCode.UnexpectedToken, "Function call is missing a closing parenthesis.");
                            return null;
                        }
                        _index++;
                        if (token.Value != "has")
                        {
                            Fail(AccessRuleDiagnosticCode.UnknownFunction, $"Function {token.Value} is not allowed.");
                            return null;
                        }
                        return value is null ? null : new("has", Value: value);
                    }
                    return new("path", Path: token.Value);
            }
            if (token.Value == "(")
            {
                _index++;
                var result = ParseOr();
                if (Peek().Value != ")")
                {
                    Fail(AccessRuleDiagnosticCode.MissingClosingParen, "Expression is missing a closing parenthesis.");
                    return null;
                }
                _index++;
                return result;
            }
            if (token.Value == "[")
            {
                _index++;
                var values = new List<Expression>();
                while (Peek().Value != "]" && Peek().Kind != "eof")
                {
                    if (ParseOr() is not { } value)
                        return null;
                    values.Add(value);
                    if (Peek().Value == ",")
                        _index++;
                    else
                        break;
                }
                if (Peek().Value != "]")
                {
                    Fail(AccessRuleDiagnosticCode.UnexpectedToken, "List literal is missing a closing bracket.");
                    return null;
                }
                _index++;
                return new("list", Values: [.. values]);
            }
            Fail(AccessRuleDiagnosticCode.MissingExpression, "Expected an expression.");
            return null;
        }

        private Expression? Fold(string operatorName, Func<Expression?> parse)
        {
            var left = parse();
            while (left is not null && Peek().Value == operatorName)
            {
                _index++;
                if (parse() is not { } right)
                    return null;
                left = new("binary", Operator: operatorName, Left: left, Right: right);
            }
            return left;
        }

        private Token Peek() => tokens[Math.Min(_index, tokens.Count - 1)];

        private void Fail(AccessRuleDiagnosticCode code, string message)
        {
            var token = Peek();
            Diagnostics.Add(new(code, message, token.Line, token.Column));
        }

        private static string JsonStringify(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")}\"";
    }
}
