using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Data;

namespace TerraScale.Dashboard.Features.Databases;

public sealed record TableRowStatement(string Sql);
public sealed record TableRowMutated(DatabaseSqlResult Result, string Sql);
public sealed record TableRowTableMissing(string Table);
public sealed record TableRowPayloadInvalid(string Message);
public sealed record TableRowColumnUnknown(string Table, string Column);
public sealed record TableRowKeyRequired(string Message);
public sealed record TableRowSqlRefused(int Status, string Message);

public readonly union TableRowPlan(TableRowStatement, TableRowPayloadInvalid, TableRowColumnUnknown, TableRowKeyRequired);

public readonly union TableRowMutationResult(TableRowMutated, TableRowTableMissing, TableRowPayloadInvalid, TableRowColumnUnknown, TableRowKeyRequired, TableRowSqlRefused);

public static class TableRowMutations
{
    public const int MaxEdits = 50;
    private const int PayloadBytes = 48 * 1024;
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 8 };

    public static async Task<TableRowMutationResult> ApplyAsync(DataService data, AccountContext context, string databaseId, DatabaseSqlCatalog catalog,
        string tableName, string operation, string payload, CancellationToken cancellationToken)
    {
        if (catalog.Tables.FirstOrDefault(table => table.Name == tableName) is not { } table)
        {
            return new TableRowTableMissing(tableName);
        }

        var plan = Plan(table, operation, payload);
        return plan switch
        {
            TableRowStatement statement => await ExecuteAsync(data, context, databaseId, statement.Sql, cancellationToken),
            TableRowPayloadInvalid invalid => invalid,
            TableRowColumnUnknown unknown => unknown,
            TableRowKeyRequired key => key,
        };
    }

    public static TableRowPlan Plan(DatabaseSqlTable table, string operation, string payload)
    {
        if (Encoding.UTF8.GetByteCount(payload) > PayloadBytes)
        {
            return new TableRowPayloadInvalid("The change is too large. Send at most " + MaxEdits + " edits at a time.");
        }

        using var document = Parse(payload);
        if (document is not { RootElement.ValueKind: JsonValueKind.Object })
        {
            return new TableRowPayloadInvalid("The change payload must be a JSON object.");
        }

        return operation switch
        {
            "update" => Update(table, document.RootElement),
            "insert" => Insert(table, document.RootElement),
            "delete" => Delete(table, document.RootElement),
            _ => new TableRowPayloadInvalid("Choose update, insert or delete."),
        };
    }

    private static async Task<TableRowMutationResult> ExecuteAsync(DataService data, AccountContext context, string databaseId, string sql, CancellationToken cancellationToken)
    {
        if (SqlValidation.Diagnostics(sql).FirstOrDefault(item => item.Severity == "error") is { } problem)
        {
            return new TableRowSqlRefused(StatusCodes.Status422UnprocessableEntity, problem.Message);
        }

        try
        {
            return new TableRowMutated(await data.SqlAsync(context, databaseId, sql, true, cancellationToken), sql);
        }
        catch (Exception error) when (DatabaseResults.KnownFailure(error))
        {
            return new TableRowSqlRefused(DatabaseResults.Status(error), DatabaseResults.Message(error));
        }
    }

    private static JsonDocument? Parse(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TableRowPlan Update(DatabaseSqlTable table, JsonElement root)
    {
        if (!root.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array || edits.GetArrayLength() is < 1 or > MaxEdits)
        {
            return new TableRowPayloadInvalid("Send 1 to " + MaxEdits + " edits.");
        }

        var rows = new OrderedDictionary<string, OrderedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var edit in edits.EnumerateArray())
        {
            if (edit.ValueKind != JsonValueKind.Object || !edit.TryGetProperty("keys", out var keys) ||
                !edit.TryGetProperty("column", out var named) || named.ValueKind != JsonValueKind.String || named.GetString() is not { } name ||
                !edit.TryGetProperty("value", out var value))
            {
                return new TableRowPayloadInvalid("Each edit names its row keys, a column and a value.");
            }

            if (Match(table, keys, out var where) is { } refused)
            {
                return refused;
            }

            if (Column(table, name) is not { } column)
            {
                return new TableRowColumnUnknown(table.Name, name);
            }

            if (Literal(value, true) is not { } literal)
            {
                return new TableRowPayloadInvalid("The value for " + column.Name + " must be text, a number or null.");
            }

            if (!rows.TryGetValue(where, out var sets))
            {
                sets = new(StringComparer.Ordinal);
                rows.Add(where, sets);
            }

            sets[column.Name] = literal;
        }

        var target = DatabaseTables.Identifier(table.Name);
        if (rows.Count == 1)
        {
            var (where, sets) = rows.First();
            return new TableRowStatement("UPDATE " + target + "\nSET " + string.Join(", ", sets.Select(set => DatabaseTables.Identifier(set.Key) + " = " + set.Value)) +
                "\nWHERE " + where + ";");
        }

        string[] columns = [.. rows.Values.SelectMany(sets => sets.Keys).Distinct(StringComparer.Ordinal)];
        var assignments = columns.Select(column => DatabaseTables.Identifier(column) + " = CASE" +
            string.Concat(rows.Where(row => row.Value.ContainsKey(column)).Select(row => " WHEN " + row.Key + " THEN " + row.Value[column])) +
            " ELSE " + DatabaseTables.Identifier(column) + " END");
        return new TableRowStatement("UPDATE " + target + "\nSET " + string.Join(",\n    ", assignments) +
            "\nWHERE " + string.Join("\n   OR ", rows.Keys.Select(where => "(" + where + ")")) + ";");
    }

    private static TableRowPlan Insert(DatabaseSqlTable table, JsonElement root)
    {
        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
        {
            return new TableRowPayloadInvalid("Send the new row as a values object.");
        }

        var assigned = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in values.EnumerateObject())
        {
            if (Column(table, property.Name) is not { } column)
            {
                return new TableRowColumnUnknown(table.Name, property.Name);
            }

            if (column.PrimaryKey > 0 && (property.Value.ValueKind == JsonValueKind.Null ||
                property.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(property.Value.GetString())))
            {
                continue;
            }

            if (Literal(property.Value, true) is not { } literal || !assigned.TryAdd(column.Name, literal))
            {
                return new TableRowPayloadInvalid("Give " + column.Name + " one text, number or null value.");
            }
        }

        var target = DatabaseTables.Identifier(table.Name);
        return new TableRowStatement(assigned.Count == 0
            ? "INSERT INTO " + target + " DEFAULT VALUES;"
            : "INSERT INTO " + target + " (" + string.Join(", ", assigned.Keys.Select(DatabaseTables.Identifier)) + ")\nVALUES (" + string.Join(", ", assigned.Values) + ");");
    }

    private static TableRowPlan Delete(DatabaseSqlTable table, JsonElement root)
    {
        if (!root.TryGetProperty("keys", out var keys))
        {
            return new TableRowPayloadInvalid("Name the row to delete with a keys object.");
        }

        return Match(table, keys, out var where) is { } refused
            ? refused
            : new TableRowStatement("DELETE FROM " + DatabaseTables.Identifier(table.Name) + "\nWHERE " + where + ";");
    }

    private static TableRowPlan? Match(DatabaseSqlTable table, JsonElement keys, out string where)
    {
        where = "";
        DatabaseTableColumn[] primary = [.. table.Schema.Where(column => column.PrimaryKey > 0).OrderBy(column => column.PrimaryKey)];
        if (primary.Length == 0)
        {
            return new TableRowKeyRequired(table.Name + " has no primary key, so its rows can only be changed in the SQL editor.");
        }

        if (keys.ValueKind != JsonValueKind.Object)
        {
            return new TableRowPayloadInvalid("Name the row with a keys object.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in keys.EnumerateObject())
        {
            if (Column(table, property.Name) is not { } column)
            {
                return new TableRowColumnUnknown(table.Name, property.Name);
            }

            if (column.PrimaryKey == 0)
            {
                return new TableRowKeyRequired(column.Name + " is not part of the primary key of " + table.Name + ".");
            }

            if (Literal(property.Value, false) is not { } literal || !values.TryAdd(column.Name, literal))
            {
                return new TableRowPayloadInvalid("Key " + column.Name + " needs one text or number value.");
            }
        }

        if (primary.FirstOrDefault(column => !values.ContainsKey(column.Name)) is { } missing)
        {
            return new TableRowKeyRequired("Name the row's " + missing.Name + " value.");
        }

        where = string.Join(" AND ", primary.Select(column => DatabaseTables.Identifier(column.Name) + " = " + values[column.Name]));
        return null;
    }

    private static DatabaseTableColumn? Column(DatabaseSqlTable table, string name)
    {
        return table.Schema.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Literal(JsonElement value, bool allowNull)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { } text && !text.Contains('\0', StringComparison.Ordinal)
                ? "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'" : null,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Null => allowNull ? "NULL" : null,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined => null,
            _ => throw new UnreachableException(),
        };
    }
}
