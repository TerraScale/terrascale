using System.Text;

namespace TerraBase.Features.Import;

/// <summary>
/// Reads a <c>pg_dump</c> plain-text dump without running any of it (docs/terrabase/import.md sections 1 and 4). The text
/// is split into statements by a scanner that knows quotes, comments and dollar quotes; only table, enum, constraint and
/// <c>COPY</c> statements are interpreted. Statements known to hold no data are skipped (and the notable ones listed);
/// anything else that could hold data blocks the import.
/// </summary>
internal sealed class PostgresDump : IImportSource
{
    /// <summary>A statement TerraBase interprets is at most this long; longer ones are only classified by their start.</summary>
    public const int MaxStatement = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    private readonly string _path;
    private readonly Dictionary<string, SourceTable> _tables = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _enums = [with(StringComparer.Ordinal)];

    private PostgresDump(string path)
    {
        _path = path;
    }

    public SourceSchema Schema { get; } = new();

    /// <summary>Reads the whole dump once for its schema and row counts.</summary>
    public static PostgresDump Open(string path, CancellationToken cancellationToken)
    {
        CheckSignature(path);
        var dump = new PostgresDump(path);
        dump.ReadSchema(cancellationToken);
        return dump;
    }

    /// <summary>Whether <paramref name="typeKey"/> names an enum declared in the dump.</summary>
    public bool IsEnum(string typeKey) => _enums.Contains(typeKey);

    public IEnumerable<SourceRow> Rows(ImportPosition? from, CancellationToken cancellationToken)
    {
        using var lines = new ByteLines(_path, from?.Offset ?? 0);
        var scanner = new StatementScanner();
        Copy? copy = from?.CopyTable is { } name && _tables.TryGetValue(name, out var resumed)
            ? new Copy(resumed, Columns(resumed, from.CopyColumns!), Discard: false)
            : null;
        while (lines.TryRead(out var line))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (copy is not null)
            {
                if (line.SequenceEqual("\\."u8))
                {
                    copy = null;
                    continue;
                }

                if (copy.Discard || copy.Columns is null)
                {
                    continue;
                }

                var values = Fields(line, copy.Columns.Count, lines.Position);
                yield return new SourceRow(copy.Table, values, copy.Columns, null,
                    new ImportPosition(lines.Position, copy.Table.Display, [.. copy.Columns.Select(column => column.Name)], 0, 0));
                continue;
            }

            foreach (var statement in scanner.Feed(Decode(line, lines.LineStart == 0)))
            {
                if (statement.Text.StartsWith('\\'))
                {
                    continue;
                }

                var tokens = SqlTokens.Read(statement.Text, sqlite: false);
                if (tokens is [{ } first, ..] && first.Is("COPY"))
                {
                    copy = CopyHeader(tokens, record: false);
                }
            }
        }
    }

    public void Dispose()
    {
    }

    private static void CheckSignature(string path)
    {
        Span<byte> head = stackalloc byte[512];
        int read;
        using (var file = File.OpenRead(path))
        {
            read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        head = head[..read];
        string? refused = head switch
        {
            [(byte)'P', (byte)'G', (byte)'D', (byte)'M', (byte)'P', ..] =>
                "this is a pg_dump custom-format archive; convert it with pg_restore -f dump.sql archive.dump",
            [0x1f, 0x8b, ..] or [0x28, 0xb5, 0x2f, 0xfd, ..] or [0x04, 0x22, 0x4d, 0x18, ..] or [(byte)'B', (byte)'Z', (byte)'h', ..]
                or [0xfd, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0, ..] => "the dump is compressed; upload the plain SQL file",
            _ when head.StartsWith("SQLite format 3\0"u8) => "this is a SQLite database; create the import with format sqlite",
            _ when head.Length >= 262 && head[257..262].SequenceEqual("ustar"u8) => "this is a tar archive; dump with --format=plain",
            _ when head.Contains((byte)0) => "the dump is not text",
            _ => null,
        };
        if (refused is not null)
        {
            throw new SourceFormatException("unsupported_source_format", refused);
        }
    }

    private static string Decode(ReadOnlySpan<byte> line, bool first)
    {
        try
        {
            return Utf8.GetString(first && line is [0xef, 0xbb, 0xbf, ..] ? line[3..] : line);
        }
        catch (DecoderFallbackException)
        {
            throw new SourceFormatException("unsupported_source_format", "the dump is not valid UTF-8; dump with --encoding=UTF8");
        }
    }

    private void ReadSchema(CancellationToken cancellationToken)
    {
        using var lines = new ByteLines(_path, 0);
        var scanner = new StatementScanner();
        Copy? copy = null;
        while (lines.TryRead(out var line))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (copy is not null)
            {
                if (line.SequenceEqual("\\."u8))
                {
                    copy = null;
                }
                else if (!copy.Discard)
                {
                    copy.Table.Rows++;
                }

                continue;
            }

            foreach (var statement in scanner.Feed(Decode(line, lines.LineStart == 0)))
            {
                if (Classify(statement) is { } started)
                {
                    copy = started;
                }
            }
        }

        if (copy is not null)
        {
            throw new SourceFormatException("malformed_source", "the dump ends inside COPY data (no \\. line)");
        }

        if (scanner.Pending)
        {
            Schema.Block("malformed_source", "the dump ends inside a statement");
        }

        foreach (var table in Schema.Tables)
        {
            InheritColumns(table, 0);
        }
    }

    // Columns of INHERITS and PARTITION OF parents that the child does not declare itself come first, as in PostgreSQL.
    private void InheritColumns(SourceTable table, int depth)
    {
        if (depth > 8)
        {
            return;
        }

        var inherited = new List<SourceColumn>();
        foreach (var parent in table.Parents)
        {
            if (_tables.TryGetValue(Key(parent), out var source))
            {
                InheritColumns(source, depth + 1);
                inherited.AddRange(source.Columns.Where(column => table.Column(column.Name, ignoreCase: false) is null
                    && !inherited.Any(other => other.Name == column.Name)));
            }
        }

        table.Columns.InsertRange(0, inherited);
        table.Parents.Clear();
    }

    private Copy? Classify(Statement statement)
    {
        if (statement.Text.StartsWith('\\'))
        {
            Meta(statement.Text);
            return null;
        }

        var tokens = SqlTokens.Read(statement.Truncated ? statement.Text[..Math.Min(statement.Text.Length, 4096)] : statement.Text, sqlite: false);
        if (tokens.Count == 0)
        {
            return null;
        }

        var first = tokens[0];
        if (statement.Truncated && (first.Is("COPY") || first.Is("ALTER") || (first.Is("CREATE") && Kind(tokens, 1).Kind is "table" or "type")))
        {
            Schema.Block("malformed_source", $"a statement over 1 MiB cannot be read: {Excerpt(statement.Text)}");
            return first.Is("COPY") ? new Copy(null!, null, Discard: true) : null;
        }

        if (first.Is("SET"))
        {
            CheckSetting(tokens);
            return null;
        }

        if (first.Is("COPY"))
        {
            return CopyHeader(tokens, record: true);
        }

        if (first.Is("CREATE"))
        {
            Create(tokens, statement.Text);
        }
        else if (first.Is("ALTER"))
        {
            Alter(tokens);
        }
        else if (first.Is("SELECT"))
        {
            Select(tokens, statement.Text);
        }
        else if (first.Is("INSERT"))
        {
            var i = 2;
            var target = tokens.Count > 1 && tokens[1].Is("INTO") ? SqlTokens.QualifiedName(tokens, ref i) : null;
            const string message = "INSERT statements are not imported; dump with COPY (pg_dump's default, without --inserts)";
            if (target is not null && _tables.TryGetValue(Key(target), out var table))
            {
                if (!table.Issues.Any(issue => issue.Code == "unsupported_statement"))
                {
                    table.Issues.Add(new("blocking", "unsupported_statement", null, null, message));
                }
            }
            else if (!Schema.Issues.Any(issue => issue.Code == "unsupported_statement" && issue.Message == message))
            {
                Schema.Block("unsupported_statement", message);
            }
        }
        else if (!(first.Is("COMMENT") || first.Is("SECURITY") || first.Is("GRANT") || first.Is("REVOKE") || first.Is("REFRESH")
            || first.Is("DROP") || first.Is("RESET") || first.Is("BEGIN") || first.Is("COMMIT") || first.Is("START") || first.Is("END")
            || first.Is("ANALYZE") || first.Is("VACUUM")))
        {
            Schema.Block("unsupported_statement", $"a statement that may hold data is not imported: {Excerpt(statement.Text)}");
        }

        return null;
    }

    private void Meta(string line)
    {
        var command = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        switch (command)
        {
            case "\\restrict" or "\\unrestrict":
                return;
            case "\\connect" or "\\c":
                throw new SourceFormatException("unsupported_source_format", "pg_dumpall output holds several databases; dump one database with pg_dump");
            default:
                Schema.Block("unsupported_statement", $"psql meta-command {command} is not supported");
                return;
        }
    }

    private static void CheckSetting(List<SqlToken> tokens)
    {
        if (tokens.Count < 4 || !(tokens[2].IsSymbol("=") || tokens[2].Is("TO")))
        {
            return;
        }

        var value = tokens[3].Text;
        if (tokens[1].Is("client_encoding") && !string.Equals(value.Replace("-", "", StringComparison.Ordinal), "UTF8", StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceFormatException("unsupported_source_format", "the dump is not UTF-8; dump with --encoding=UTF8");
        }

        if (tokens[1].Is("standard_conforming_strings") && !value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceFormatException("unsupported_source_format", "the dump turns standard_conforming_strings off; dump with a current pg_dump");
        }
    }

    private void Select(List<SqlToken> tokens, string text)
    {
        var i = 1;
        var function = SqlTokens.QualifiedName(tokens, ref i) is { } name ? name[^1].ToLowerInvariant() : "";
        if (function is "set_config" or "setval")
        {
            return;
        }

        Schema.Block("unsupported_statement", function.StartsWith("lo", StringComparison.Ordinal)
            ? "large objects are not imported; dump with --no-large-objects"
            : $"a statement that may hold data is not imported: {Excerpt(text)}");
    }

    // The kind of object a CREATE makes, and where its name starts.
    private static (string Kind, int Name) Kind(List<SqlToken> tokens, int i)
    {
        string[] modifiers = ["OR", "REPLACE", "GLOBAL", "LOCAL", "TEMP", "TEMPORARY", "UNLOGGED", "UNIQUE", "TRUSTED", "PROCEDURAL", "DEFAULT", "CONSTRAINT", "RECURSIVE"];
        while (i < tokens.Count && modifiers.Any(tokens[i].Is))
        {
            i++;
        }

        string[][] kinds =
        [
            ["MATERIALIZED", "VIEW"], ["FOREIGN", "TABLE"], ["FOREIGN", "DATA", "WRAPPER"], ["EVENT", "TRIGGER"], ["TEXT", "SEARCH"],
            ["USER", "MAPPING"], ["ACCESS", "METHOD"], ["OPERATOR", "CLASS"], ["OPERATOR", "FAMILY"],
            ["TABLE"], ["TYPE"], ["DOMAIN"], ["SCHEMA"], ["SEQUENCE"], ["INDEX"], ["FUNCTION"], ["PROCEDURE"], ["AGGREGATE"], ["OPERATOR"],
            ["CAST"], ["COLLATION"], ["CONVERSION"], ["EXTENSION"], ["TRIGGER"], ["RULE"], ["POLICY"], ["PUBLICATION"], ["SUBSCRIPTION"],
            ["STATISTICS"], ["VIEW"], ["SERVER"], ["LANGUAGE"], ["TRANSFORM"], ["TABLESPACE"], ["ROLE"],
        ];
        foreach (var kind in kinds)
        {
            if (i + kind.Length <= tokens.Count && kind.Select((word, k) => tokens[i + k].Is(word)).All(match => match))
            {
                return (string.Join('_', kind).ToLowerInvariant(), i + kind.Length);
            }
        }

        return ("", i);
    }

    private void Create(List<SqlToken> tokens, string text)
    {
        var (kind, i) = Kind(tokens, 1);
        if (i + 2 < tokens.Count && tokens[i].Is("IF") && tokens[i + 1].Is("NOT") && tokens[i + 2].Is("EXISTS"))
        {
            i += 3;
        }

        if (kind == "index" && i < tokens.Count && tokens[i].Is("CONCURRENTLY"))
        {
            i++;
        }

        var start = i;
        var name = SqlTokens.QualifiedName(tokens, ref i);
        switch (kind)
        {
            case "table" when name is not null:
                CreateTable(tokens, name, i);
                return;
            case "type" when name is not null && i + 1 < tokens.Count && tokens[i].Is("AS") && tokens[i + 1].Is("ENUM"):
                if (_enums.Count >= 100_000)
                {
                    throw new SourceFormatException("too_many_tables", "the dump declares more than 100000 enum types");
                }

                _enums.Add(Key(name));
                return;
            case "foreign_table" or "materialized_view":
                Schema.Skip(kind, Display(name));
                Schema.Warn($"{kind.Replace('_', ' ')} {Display(name)} is not imported: its rows are not part of the dump's table data");
                return;
            case "":
                Schema.Block("unsupported_statement", $"a statement that may hold data is not imported: {Excerpt(text)}");
                return;
            default:
                if (kind is not ("role" or "tablespace"))
                {
                    Schema.Skip(kind, name is null ? SqlTokens.Text(tokens.Skip(start).Take(3)) : Display(name));
                }

                return;
        }
    }

    private void CreateTable(List<SqlToken> tokens, List<string> name, int i)
    {
        var table = new SourceTable { Schema = name.Count > 1 ? name[^2] : "public", Name = name[^1] };
        if (Schema.Tables.Count >= ImportPlanner.MaxTables)
        {
            throw new SourceFormatException("too_many_tables", "an import holds at most 1000 tables");
        }

        if (_tables.ContainsKey(table.Display))
        {
            table.Issues.Add(new("blocking", "malformed_source", null, null, "the dump defines this table twice"));
            return;
        }

        _tables[table.Display] = table;
        Schema.Tables.Add(table);
        if (i + 1 < tokens.Count && tokens[i].Is("PARTITION") && tokens[i + 1].Is("OF"))
        {
            i += 2;
            if (SqlTokens.QualifiedName(tokens, ref i) is { } parent)
            {
                table.Parents.Add(parent);
            }
        }
        else if (i < tokens.Count && tokens[i].Is("OF"))
        {
            table.Issues.Add(new("blocking", "unsupported_statement", null, null, "typed tables (CREATE TABLE … OF type) are not supported"));
            return;
        }

        if (i >= tokens.Count || !tokens[i].IsSymbol("("))
        {
            if (table.Parents.Count == 0)
            {
                table.Issues.Add(new("blocking", "malformed_source", null, null, "the table definition cannot be read"));
            }

            return;
        }

        var close = SqlTokens.Closing(tokens, i);
        if (close < 0)
        {
            table.Issues.Add(new("blocking", "malformed_source", null, null, "the table definition cannot be read"));
            return;
        }

        foreach (var element in SqlTokens.SplitTopLevel(tokens, i + 1, close))
        {
            Element(table, element, sqlite: false);
        }

        foreach (var check in table.Checks)
        {
            Schema.Skip("check_constraint", table.Display + "." + ImportNames.Quote(check));
        }

        for (var j = close + 1; j < tokens.Count; j++)
        {
            if (tokens[j].Is("INHERITS") && SqlTokens.NameList(tokens, j + 1, out _) is not null)
            {
                var k = j + 2;
                while (k < tokens.Count && SqlTokens.QualifiedName(tokens, ref k) is { } parent)
                {
                    table.Parents.Add(parent);
                    if (k < tokens.Count && tokens[k].IsSymbol(","))
                    {
                        k++;
                    }
                }
            }
            else if (tokens[j].Is("PARTITION") && j + 1 < tokens.Count && tokens[j + 1].Is("BY"))
            {
                table.Partitioned = true;
            }
        }

        // pg_dump defines parents first, so their columns are known for the child's COPY list.
        InheritColumns(table, 0);
    }

    /// <summary>Reads one element of a table definition: a column or a table constraint.</summary>
    internal static void Element(SourceTable table, List<SqlToken> element, bool sqlite)
    {
        if (element.Count == 0)
        {
            return;
        }

        var i = 0;
        string? constraintName = null;
        if (element[0].Is("CONSTRAINT") && element.Count > 1)
        {
            constraintName = element[1].Text;
            i = 2;
        }

        // EXCLUDE is not reserved in PostgreSQL: a column may be named exclude, but the constraint continues with USING or "(".
        if (i < element.Count && (element[i].Is("PRIMARY") || element[i].Is("UNIQUE") || element[i].Is("FOREIGN") || element[i].Is("CHECK")
            || (element[i].Is("EXCLUDE") && i + 1 < element.Count && (element[i + 1].Is("USING") || element[i + 1].IsSymbol("(")))))
        {
            TableConstraint(table, element, i, constraintName);
            return;
        }

        if (table.Columns.Count >= TablePlan.MaxColumns)
        {
            if (!table.Issues.Any(issue => issue.Code == "too_many_columns"))
            {
                table.Issues.Add(new("blocking", "too_many_columns", null, null, "a table has at most 1600 columns"));
            }

            return;
        }

        if (!element[0].IsName)
        {
            table.Issues.Add(new("blocking", "malformed_source", null, null, $"a column definition cannot be read: {SqlTokens.Text(element.Take(8))}"));
            return;
        }

        var stops = new[] { "CONSTRAINT", "NOT", "NULL", "DEFAULT", "PRIMARY", "UNIQUE", "CHECK", "REFERENCES", "COLLATE", "GENERATED", "COMPRESSION", "STORAGE", "AS" };
        var end = 1;
        var depth = 0;
        while (end < element.Count)
        {
            var token = element[end];
            depth += token.IsSymbol("(") ? 1 : token.IsSymbol(")") ? -1 : 0;
            if (depth == 0 && stops.Any(token.Is))
            {
                break;
            }

            end++;
        }

        var column = new SourceColumn { Name = element[0].Text, Type = TypeText(element.Skip(1).Take(end - 1)) };
        table.Columns.Add(column);
        for (var j = end; j < element.Count; j++)
        {
            var token = element[j];
            if (token.Is("NOT") && j + 1 < element.Count && element[j + 1].Is("NULL"))
            {
                column.NotNull = true;
            }
            else if (token.Is("PRIMARY") && j + 1 < element.Count && element[j + 1].Is("KEY"))
            {
                table.PrimaryKey = [column.Name];
                column.NotNull |= !sqlite;
                table.Constraints.Add(new("primary_key", null, [column.Name], null, false));
            }
            else if (token.Is("UNIQUE"))
            {
                table.Constraints.Add(new("unique", null, [column.Name], null, false));
            }
            else if (token.Is("REFERENCES"))
            {
                var k = j + 1;
                if (SqlTokens.QualifiedName(element, ref k) is { } target)
                {
                    var columns = SqlTokens.NameList(element, k, out _);
                    table.Constraints.Add(new("foreign_key", null, [column.Name], References(target, columns), false));
                }
            }
            else if (token.Is("DEFAULT") && j + 1 < element.Count)
            {
                (column.Default, column.ExpressionDefault) = Literal(element, j + 1);
            }
            else if ((token.Is("GENERATED") && j + 2 < element.Count && element[j + 1].Is("ALWAYS") && element[j + 2].Is("AS")
                    && !(j + 3 < element.Count && element[j + 3].Is("IDENTITY")))
                || (token.Is("AS") && j + 1 < element.Count && element[j + 1].IsSymbol("(")))
            {
                column.Generated = true;
                column.Stored = element.Skip(j).Any(word => word.Is("STORED"));
            }
        }
    }

    private static void TableConstraint(SourceTable table, List<SqlToken> element, int i, string? name)
    {
        if (element[i].Is("CHECK") || element[i].Is("EXCLUDE"))
        {
            table.Checks.Add(name ?? "check");
        }
        else if (element[i].Is("PRIMARY") && SqlTokens.NameList(element, i + 2, out _) is { } key)
        {
            table.PrimaryKey = key;
            table.Constraints.Add(new("primary_key", name, key, null, false));
        }
        else if (element[i].Is("UNIQUE"))
        {
            var open = element.FindIndex(i, token => token.IsSymbol("("));
            if (open > 0 && SqlTokens.NameList(element, open, out _) is { } columns)
            {
                table.Constraints.Add(new("unique", name, columns, null, false));
            }
        }
        else if (element[i].Is("FOREIGN") && SqlTokens.NameList(element, i + 2, out var close) is { } columns)
        {
            var k = close + 1;
            if (k < element.Count && element[k].Is("REFERENCES"))
            {
                k++;
                if (SqlTokens.QualifiedName(element, ref k) is { } target)
                {
                    table.Constraints.Add(new("foreign_key", name, columns, References(target, SqlTokens.NameList(element, k, out _)), false));
                }
            }
        }
    }

    private static string References(List<string> target, List<string>? columns) =>
        string.Join('.', target.Select(ImportNames.Quote)) + (columns is null ? "" : "(" + string.Join(", ", columns.Select(ImportNames.Quote)) + ")");

    // A literal DEFAULT: a number, string, NULL, TRUE/FALSE or blob, optionally signed. Anything else is an expression.
    private static (SourceValue Value, bool Expression) Literal(List<SqlToken> tokens, int i)
    {
        var negative = false;
        if (i < tokens.Count && (tokens[i].IsSymbol("-") || tokens[i].IsSymbol("+")))
        {
            negative = tokens[i].IsSymbol("-");
            i++;
        }

        if (i < tokens.Count && tokens[i].IsSymbol("(") && i + 2 < tokens.Count && tokens[i + 2].IsSymbol(")"))
        {
            i++;
        }

        if (i >= tokens.Count)
        {
            return (SourceValue.Null, true);
        }

        var token = tokens[i];
        return token.Kind switch
        {
            SqlTokenKind.Number when token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(token.Text.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var hex) =>
                (new SourceValue(SourceKind.Integer, Integer: negative ? -hex : hex), false),
            SqlTokenKind.Number when long.TryParse(token.Text, System.Globalization.CultureInfo.InvariantCulture, out var integer) =>
                (new SourceValue(SourceKind.Integer, Integer: negative ? -integer : integer), false),
            SqlTokenKind.Number when double.TryParse(token.Text, System.Globalization.CultureInfo.InvariantCulture, out var real) =>
                (new SourceValue(SourceKind.Real, Real: negative ? -real : real), false),
            SqlTokenKind.String when !negative => (new SourceValue(SourceKind.Text, Text: token.Text), false),
            SqlTokenKind.Word when token.Is("NULL") => (SourceValue.Null, false),
            SqlTokenKind.Word when token.Is("TRUE") => (new SourceValue(SourceKind.Integer, Integer: 1), false),
            SqlTokenKind.Word when token.Is("FALSE") => (new SourceValue(SourceKind.Integer, Integer: 0), false),
            SqlTokenKind.Word when (token.Text is ['x' or 'X']) && i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.String
                && tokens[i + 1].Text.Length % 2 == 0 && tokens[i + 1].Text.All(char.IsAsciiHexDigit) =>
                (new SourceValue(SourceKind.Blob, Blob: Convert.FromHexString(tokens[i + 1].Text)), false),
            _ => (SourceValue.Null, true),
        };
    }

    /// <summary>A declared type as lowercase, space-separated text, quoted identifiers kept.</summary>
    internal static string TypeText(IEnumerable<SqlToken> tokens) =>
        SqlTokens.Text(tokens.Select(token => token.Kind == SqlTokenKind.Word ? token with { Text = token.Text.ToLowerInvariant() } : token));

    private void Alter(List<SqlToken> tokens)
    {
        if (tokens.Count < 3 || !tokens[1].Is("TABLE"))
        {
            return;
        }

        var i = 2;
        if (i + 1 < tokens.Count && tokens[i].Is("IF") && tokens[i + 1].Is("EXISTS"))
        {
            i += 2;
        }

        if (i < tokens.Count && tokens[i].Is("ONLY"))
        {
            i++;
        }

        if (SqlTokens.QualifiedName(tokens, ref i) is not { } name || !_tables.TryGetValue(Key(name), out var table))
        {
            return;
        }

        if (i + 1 < tokens.Count && tokens[i].Is("ADD") && tokens[i + 1].Is("CONSTRAINT"))
        {
            var element = tokens.GetRange(i + 1, tokens.Count - i - 1);
            if (element.Count > 2 && (element[2].Is("CHECK") || element[2].Is("EXCLUDE")))
            {
                Schema.Skip(element[2].Is("CHECK") ? "check_constraint" : "exclusion_constraint", table.Display + "." + ImportNames.Quote(element[1].Text));
                return;
            }

            TableConstraint(table, element, 2, element.Count > 1 ? element[1].Text : null);
        }
    }

    private Copy CopyHeader(List<SqlToken> tokens, bool record)
    {
        var i = 1;
        var name = SqlTokens.QualifiedName(tokens, ref i);
        List<string>? columns = null;
        if (i < tokens.Count && tokens[i].IsSymbol("("))
        {
            columns = SqlTokens.NameList(tokens, i, out var close);
            i = close + 1;
        }

        var plain = name is not null && i + 1 < tokens.Count && tokens[i].Is("FROM") && tokens[i + 1].Is("stdin")
            && (i + 2 == tokens.Count || (i + 3 == tokens.Count && tokens[i + 2].IsSymbol(";")));
        if (name is null || !_tables.TryGetValue(Key(name), out var table))
        {
            if (record)
            {
                Schema.Block("unsupported_statement", $"COPY into {(name is null ? "an unreadable table" : Display(name))}, which the dump does not define");
            }

            return new Copy(null!, null, Discard: true);
        }

        if (!plain)
        {
            if (record)
            {
                table.Issues.Add(new("blocking", "unsupported_statement", null, null, "only COPY … FROM stdin in text format is supported"));
            }

            return new Copy(table, null, Discard: true);
        }

        var names = columns ?? [.. table.Columns.Where(column => !column.Generated).Select(column => column.Name)];
        var resolved = Columns(table, names);
        if (resolved is null && record)
        {
            table.Issues.Add(new("blocking", "malformed_source", null, null, "COPY names a column the table does not declare"));
        }

        return new Copy(table, resolved, Discard: resolved is null);
    }

    private static List<SourceColumn>? Columns(SourceTable table, IReadOnlyList<string> names)
    {
        var columns = new List<SourceColumn>(names.Count);
        foreach (var name in names)
        {
            if (table.Column(name, ignoreCase: false) is not { } column)
            {
                return null;
            }

            columns.Add(column);
        }

        return columns;
    }

    /// <summary>Splits a <c>COPY</c> text-format line into its values: tab-separated, <c>\N</c> for NULL, backslash escapes.</summary>
    private static SourceValue[] Fields(ReadOnlySpan<byte> line, int count, long position)
    {
        var values = new SourceValue[count];
        var buffer = new ArrayBufferWriterLite();
        for (var field = 0; field < count; field++)
        {
            var tab = line.IndexOf((byte)'\t');
            var raw = tab < 0 ? line : line[..tab];
            if ((tab < 0) != (field == count - 1))
            {
                throw new SourceFormatException("malformed_source", $"a COPY line before byte {position} does not hold {count} values");
            }

            values[field] = raw.SequenceEqual("\\N"u8) ? SourceValue.Null : new SourceValue(SourceKind.Text, Text: Unescape(raw, buffer, position));
            line = tab < 0 ? default : line[(tab + 1)..];
        }

        return values;
    }

    private static string Unescape(ReadOnlySpan<byte> raw, ArrayBufferWriterLite buffer, long position)
    {
        if (raw.IndexOf((byte)'\\') < 0)
        {
            return DecodeValue(raw, position);
        }

        buffer.Clear();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\' || i + 1 == raw.Length)
            {
                buffer.Add(raw[i]);
                continue;
            }

            var c = raw[++i];
            switch (c)
            {
                case (byte)'b': buffer.Add(8); break;
                case (byte)'f': buffer.Add(12); break;
                case (byte)'n': buffer.Add(10); break;
                case (byte)'r': buffer.Add(13); break;
                case (byte)'t': buffer.Add(9); break;
                case (byte)'v': buffer.Add(11); break;
                case >= (byte)'0' and <= (byte)'7':
                    {
                        var value = c - '0';
                        for (var k = 0; k < 2 && i + 1 < raw.Length && raw[i + 1] is >= (byte)'0' and <= (byte)'7'; k++)
                        {
                            value = (value * 8) + (raw[++i] - '0');
                        }

                        buffer.Add((byte)value);
                        break;
                    }

                case (byte)'x' when i + 1 < raw.Length && Uri.IsHexDigit((char)raw[i + 1]):
                    {
                        var value = Convert.ToInt32(((char)raw[++i]).ToString(), 16);
                        if (i + 1 < raw.Length && Uri.IsHexDigit((char)raw[i + 1]))
                        {
                            value = (value * 16) + Convert.ToInt32(((char)raw[++i]).ToString(), 16);
                        }

                        buffer.Add((byte)value);
                        break;
                    }

                default:
                    buffer.Add(c);
                    break;
            }
        }

        return DecodeValue(buffer.Span, position);
    }

    private static string DecodeValue(ReadOnlySpan<byte> value, long position)
    {
        try
        {
            return Utf8.GetString(value);
        }
        catch (DecoderFallbackException)
        {
            throw new SourceFormatException("malformed_source", $"a COPY value before byte {position} is not valid UTF-8");
        }
    }

    /// <summary>A type or table name as one key: identifiers joined by dots, unquoted words lowercased.</summary>
    internal static string Key(IReadOnlyList<string> name) =>
        name.Count == 1 ? ImportNames.Table("public", name[0]) : ImportNames.Table(name[^2], name[^1]);

    private static string Display(List<string>? name) => name is null ? "" : string.Join('.', name.Select(ImportNames.Quote));

    private static string Excerpt(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 80 ? flat : flat[..80] + "…";
    }

    private sealed record Copy(SourceTable Table, IReadOnlyList<SourceColumn>? Columns, bool Discard);

    /// <summary>A growable byte buffer for unescaping one value.</summary>
    private sealed class ArrayBufferWriterLite
    {
        private byte[] _bytes = new byte[256];
        private int _length;

        public ReadOnlySpan<byte> Span => _bytes.AsSpan(0, _length);

        public void Clear() => _length = 0;

        public void Add(byte value)
        {
            if (_length == _bytes.Length)
            {
                Array.Resize(ref _bytes, _bytes.Length * 2);
            }

            _bytes[_length++] = value;
        }
    }

    /// <summary>A complete statement, or a psql meta-command line (starting with <c>\</c>).</summary>
    private readonly record struct Statement(string Text, bool Truncated);

    /// <summary>
    /// Splits dump text into statements at <c>;</c> outside strings, quoted identifiers, dollar quotes and comments. Text
    /// beyond <see cref="MaxStatement"/> is scanned but not kept.
    /// </summary>
    private sealed class StatementScanner
    {
        private readonly StringBuilder _text = new();
        private Mode _mode;
        private string _tag = "";
        private int _depth;
        private bool _truncated;

        private enum Mode
        {
            Normal,
            Single,
            Escape,
            Double,
            Dollar,
            Block,
        }

        /// <summary>Whether text other than whitespace is waiting for its statement's end.</summary>
        public bool Pending { get; private set; }

        public IEnumerable<Statement> Feed(string line)
        {
            if (_mode == Mode.Normal && line.StartsWith('\\') && !Pending)
            {
                _text.Clear();
                yield return new Statement(line, false);
                yield break;
            }

            var start = 0;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                switch (_mode)
                {
                    case Mode.Normal when c == '-' && i + 1 < line.Length && line[i + 1] == '-':
                        Append(line, start, i);
                        start = line.Length;
                        i = line.Length;
                        break;
                    case Mode.Normal when c == '/' && i + 1 < line.Length && line[i + 1] == '*':
                        _mode = Mode.Block;
                        _depth = 1;
                        i++;
                        break;
                    case Mode.Normal when c == '\'':
                        _mode = i > 0 && line[i - 1] is 'E' or 'e' && (i < 2 || !(char.IsLetterOrDigit(line[i - 2]) || line[i - 2] == '_')) ? Mode.Escape : Mode.Single;
                        break;
                    case Mode.Normal when c == '"':
                        _mode = Mode.Double;
                        break;
                    case Mode.Normal when c == '$' && (i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] == '_')) && SqlTokens.DollarTag(line, i) is { } tag:
                        _mode = Mode.Dollar;
                        _tag = tag;
                        i += tag.Length - 1;
                        break;
                    case Mode.Normal when c == ';':
                        Append(line, start, i + 1);
                        start = i + 1;
                        yield return Complete();
                        break;
                    case Mode.Single when c == '\'':
                    case Mode.Double when c == '"':
                        if (i + 1 < line.Length && line[i + 1] == c)
                        {
                            i++;
                        }
                        else
                        {
                            _mode = Mode.Normal;
                        }

                        break;
                    case Mode.Escape when c == '\\':
                        i++;
                        break;
                    case Mode.Escape when c == '\'':
                        if (i + 1 < line.Length && line[i + 1] == '\'')
                        {
                            i++;
                        }
                        else
                        {
                            _mode = Mode.Normal;
                        }

                        break;
                    case Mode.Dollar when c == '$' && string.CompareOrdinal(line, i, _tag, 0, _tag.Length) == 0:
                        _mode = Mode.Normal;
                        i += _tag.Length - 1;
                        break;
                    case Mode.Block when c == '/' && i + 1 < line.Length && line[i + 1] == '*':
                        _depth++;
                        i++;
                        break;
                    case Mode.Block when c == '*' && i + 1 < line.Length && line[i + 1] == '/':
                        i++;
                        if (--_depth == 0)
                        {
                            _mode = Mode.Normal;
                        }

                        break;
                }
            }

            Append(line, start, line.Length);
            Append("\n", 0, 1);
        }

        private void Append(string line, int start, int end)
        {
            if (end <= start)
            {
                return;
            }

            Pending |= line.AsSpan(start, end - start).ContainsAnyExcept(" \t\r\n");
            var room = MaxStatement - _text.Length;
            if (end - start > room)
            {
                _truncated = true;
                end = start + Math.Max(room, 0);
            }

            _text.Append(line, start, end - start);
        }

        private Statement Complete()
        {
            var statement = new Statement(_text.ToString().Trim(), _truncated);
            _text.Clear();
            _truncated = false;
            Pending = false;
            return statement;
        }
    }
}
