using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Data;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed record CreateTableColumn(string Name, string Type, bool PrimaryKey, bool NotNull, bool Unique, string DefaultValue);

    public sealed record CreateTableDraft(string Name, bool IfNotExists, IReadOnlyList<CreateTableColumn> Columns);

    public sealed record CreateTableResult(CreateTableDraft Draft, string Error)
    {
        public bool Accepted => Error.Length == 0;
    }

    public static partial class CreateTableSql
    {
        public const int MaxColumns = 32;
        public static IReadOnlyList<string> ColumnTypes { get; } = ["INTEGER", "TEXT", "REAL", "BLOB", "NUMERIC"];
        public static CreateTableDraft Blank { get; } = new("", false, [new("id", "INTEGER", true, false, false, "")]);

        public static CreateTableResult Parse(IFormCollection form)
        {
            var indexes = Posted(form, "columnIndex");
            var names = Posted(form, "columnName");
            var types = Posted(form, "columnType");
            var defaults = Posted(form, "columnDefault");
            var draft = new CreateTableDraft(form["tableName"].ToString().Trim(), form["ifNotExists"].ToString() == "1", Blank.Columns);
            if (names.Length == 0 || names.Length != indexes.Length || names.Length != types.Length || names.Length != defaults.Length)
            {
                return new(draft, "The column form is incomplete.");
            }

            if (names.Length > MaxColumns)
            {
                return new(draft, "A table can have at most " + MaxColumns.ToString(CultureInfo.InvariantCulture) + " columns.");
            }

            if (indexes.Distinct(StringComparer.Ordinal).Count() != indexes.Length || indexes.Any(index => index.Length is < 1 or > 3 || !index.All(char.IsAsciiDigit)))
            {
                return new(draft, "The column form is incomplete.");
            }

            var indexSet = indexes.ToHashSet(StringComparer.Ordinal);
            var primary = Flags(form, "columnPk", indexSet);
            var required = Flags(form, "columnNotNull", indexSet);
            var unique = Flags(form, "columnUnique", indexSet);
            if (primary is null || required is null || unique is null)
            {
                return new(draft, "The column form is incomplete.");
            }

            var columns = new List<CreateTableColumn>(names.Length);
            for (var i = 0; i < names.Length; i++)
            {
                columns.Add(new(names[i], types[i], primary.Contains(indexes[i]), required.Contains(indexes[i]), unique.Contains(indexes[i]), defaults[i]));
            }

            draft = draft with { Columns = columns };
            if (draft.Name.Length == 0)
            {
                return new(draft, "Enter a table name.");
            }

            if (draft.Name.Length > 128 || HasControl(draft.Name))
            {
                return new(draft, "Enter a table name of at most 128 characters.");
            }

            if (columns.Any(column => column.Name.Length == 0))
            {
                return new(draft, "Enter a name for every column.");
            }

            if (columns.Any(column => column.Name.Length > 128 || HasControl(column.Name)))
            {
                return new(draft, "Enter column names of at most 128 characters.");
            }

            if (columns.Select(column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count)
            {
                return new(draft, "Column names must be unique.");
            }

            if (columns.Any(column => !ColumnTypes.Contains(column.Type, StringComparer.Ordinal)))
            {
                return new(draft, "Choose a column type.");
            }

            if (columns.Count(column => column.PrimaryKey) > 1)
            {
                return new(draft, "A table can have one primary key.");
            }

            if (columns.Any(column => column.DefaultValue.Length > 512 || HasControl(column.DefaultValue)))
            {
                return new(draft, "Enter a default of at most 512 characters.");
            }

            var sql = Statement(draft);
            if (Encoding.UTF8.GetByteCount(sql) > 16 * 1024)
            {
                return new(draft, "The table definition is over 16 KiB; shorten the defaults or use fewer columns.");
            }

            return new(draft, SqlValidation.Error(sql));
        }

        public static string Statement(CreateTableDraft draft)
        {
            var sql = new StringBuilder();
            sql.Append("CREATE TABLE ");
            if (draft.IfNotExists)
            {
                sql.Append("IF NOT EXISTS ");
            }

            sql.Append(QuoteIdentifier(draft.Name));
            sql.Append(" (\n");
            for (var i = 0; i < draft.Columns.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(",\n");
                }

                sql.Append("  ");
                sql.Append(ColumnSql(draft.Columns[i]));
            }

            sql.Append("\n);");
            return sql.ToString();
        }

        public static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        private static string ColumnSql(CreateTableColumn column)
        {
            var parts = new List<string> { QuoteIdentifier(column.Name), column.Type };
            if (column.PrimaryKey)
            {
                parts.Add("PRIMARY KEY");
            }

            if (column.NotNull)
            {
                parts.Add("NOT NULL");
            }

            if (column.Unique)
            {
                parts.Add("UNIQUE");
            }

            if (QuoteLiteral(column.DefaultValue) is { } literal)
            {
                parts.Add("DEFAULT " + literal);
            }

            return string.Join(' ', parts);
        }

        private static string? QuoteLiteral(string value)
        {
            if (value.Length == 0)
            {
                return null;
            }

            if (BareNumber().IsMatch(value))
            {
                return value;
            }

            if (BareWord(value) is { } word)
            {
                return word;
            }

            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        private static string? BareWord(string text) => text.ToUpperInvariant() switch
        {
            "NULL" => "NULL",
            "TRUE" => "TRUE",
            "FALSE" => "FALSE",
            "CURRENT_TIMESTAMP" => "CURRENT_TIMESTAMP",
            "CURRENT_TIME" => "CURRENT_TIME",
            "CURRENT_DATE" => "CURRENT_DATE",
            _ => null,
        };

        private static string[] Posted(IFormCollection form, string key)
        {
            var values = form[key];
            var result = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                result[i] = (values[i] ?? "").Trim();
            }

            return result;
        }

        private static HashSet<string>? Flags(IFormCollection form, string key, HashSet<string> indexes)
        {
            var flags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in form[key])
            {
                if (value is not { Length: > 0 } flag || !indexes.Contains(flag) || !flags.Add(flag))
                {
                    return null;
                }
            }

            return flags;
        }

        private static bool HasControl(string text) => text.Any(char.IsControl);

        [GeneratedRegex(@"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
        private static partial Regex BareNumber();
    }
}
