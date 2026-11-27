using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Data;

namespace TerraScale.Dashboard.Features.Databases;

public sealed record TableRowBatch(IReadOnlyList<string> Statements, string DisplaySql, int ExpectedRows);
public sealed record TableRowMutated(DatabaseSqlResult Result, string Sql, long RowsAffected, int ExpectedRows, int MatchedRows);
public sealed record TableRowTableMissing(string Table);
public sealed record TableRowPayloadInvalid(string Message);
public sealed record TableRowColumnUnknown(string Table, string Column);
public sealed record TableRowKeyRequired(string Message);
public sealed record TableRowSqlRefused(int Status, string Message);

public readonly union TableRowPlan(TableRowBatch, TableRowPayloadInvalid, TableRowColumnUnknown, TableRowKeyRequired);

public readonly union TableRowMutationResult(TableRowMutated, TableRowTableMissing, TableRowPayloadInvalid, TableRowColumnUnknown, TableRowKeyRequired, TableRowSqlRefused);

public static class TableRowMutations
{
    public const int MaxEdits = 50;
    public const int PayloadBytes = 48 * 1024;
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 8 };

    public static async Task<TableRowMutationResult> ApplyAsync(IDatabaseBackend backend, DatabaseScope scope, DatabaseRegion home, string databaseId,
        DatabaseSqlCatalog catalog, string tableName, string operation, string payload, CancellationToken cancellationToken)
    {
        if (catalog.Tables.FirstOrDefault(table => table.Name == tableName) is not { } table)
        {
            return new TableRowTableMissing(tableName);
        }

        var plan = Plan(table, operation, payload);
        return plan switch
        {
            TableRowBatch batch => await ExecuteAsync(backend, scope, home, databaseId, batch, cancellationToken),
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

        using var document = FormRequests.TryParseJson(payload, Options);
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

    private static async Task<TableRowMutationResult> ExecuteAsync(IDatabaseBackend backend, DatabaseScope scope, DatabaseRegion home, string databaseId,
        TableRowBatch batch, CancellationToken cancellationToken)
    {
        foreach (var statement in batch.Statements)
        {
            if (SqlValidation.Diagnostics(statement).FirstOrDefault(item => item.Severity == "error") is { } problem)
            {
                return new TableRowSqlRefused(StatusCodes.Status422UnprocessableEntity, problem.Message);
            }
        }

        return await backend.TrySqlBatchAsync(scope, home, databaseId, batch.Statements, true, cancellationToken) switch
        {
            IReadOnlyList<DatabaseSqlResult> results => Summarize(batch, results),
            DatabaseBackendFailure failure => new TableRowSqlRefused(failure.Status, failure.Message),
        };
    }

    private static TableRowMutated Summarize(TableRowBatch batch, IReadOnlyList<DatabaseSqlResult> results)
    {
        var total = results.Aggregate(0L, (sum, item) => checked(sum + item.RowsAffected));
        var matched = results.Count(item => item.RowsAffected == 1);
        var lastInsert = results.Count == 1 ? results[0].LastInsertRowId : null;
        return new TableRowMutated(new DatabaseSqlResult([], [], total, lastInsert), batch.DisplaySql, total, batch.ExpectedRows, matched);
    }

    private static TableRowPlan Update(DatabaseSqlTable table, JsonElement root)
    {
        if (!root.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array || edits.GetArrayLength() is < 1 or > MaxEdits)
        {
            return new TableRowPayloadInvalid("Send 1 to " + MaxEdits + " edits.");
        }

        var rows = new OrderedDictionary<string, (OrderedDictionary<string, string> Sets, OrderedDictionary<string, string> Guards)>(StringComparer.Ordinal);
        var editsKey = false;
        foreach (var edit in edits.EnumerateArray())
        {
            if (edit.ValueKind != JsonValueKind.Object || !edit.TryGetProperty("keys", out var keys) ||
                !edit.TryGetProperty("column", out var named) || named.ValueKind != JsonValueKind.String || named.GetString() is not { } name ||
                !edit.TryGetProperty("value", out var value) || !edit.TryGetProperty("expected", out var expected))
            {
                return new TableRowPayloadInvalid("Each edit names its row keys, a column, an expected value and a new value.");
            }

            if (Match(table, keys, out var where) is { } refused)
            {
                return refused;
            }

            if (Column(table, name) is not { } column)
            {
                return new TableRowColumnUnknown(table.Name, name);
            }

            if (column.PrimaryKey > 0)
            {
                editsKey = true;
            }

            if (Literal(value, true) is not { } literal)
            {
                return new TableRowPayloadInvalid("The value for " + column.Name + " must be text, a number or null.");
            }

            if (ValuePredicate(column.Name, expected, true) is not { } guard)
            {
                return new TableRowPayloadInvalid("The expected value for " + column.Name + " must be text, a number or null.");
            }

            if (!rows.TryGetValue(where, out var entry))
            {
                entry = (new(StringComparer.Ordinal), new(StringComparer.Ordinal));
                rows.Add(where, entry);
            }

            entry.Guards.TryAdd(column.Name, guard);
            entry.Sets[column.Name] = literal;
        }

        if (editsKey && rows.Count > 1)
        {
            return new TableRowPayloadInvalid("Edit primary key values one row at a time.");
        }

        var target = DatabaseTables.Identifier(table.Name);
        string[] statements = [.. rows.Select(row => "UPDATE " + target + "\nSET " +
            string.Join(", ", row.Value.Sets.Select(set => DatabaseTables.Identifier(set.Key) + " = " + set.Value)) +
            "\nWHERE " + row.Key + " AND " + string.Join(" AND ", row.Value.Guards.Values) + ";")];
        return new TableRowBatch(statements, string.Join("\n", statements), statements.Length);
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
                DatabaseTables.NumericType(column.Decltype) && property.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(property.Value.GetString())))
            {
                continue;
            }

            if (Literal(property.Value, true) is not { } literal || !assigned.TryAdd(column.Name, literal))
            {
                return new TableRowPayloadInvalid("Give " + column.Name + " one text, number or null value.");
            }
        }

        var target = DatabaseTables.Identifier(table.Name);
        var statement = assigned.Count == 0
            ? "INSERT INTO " + target + " DEFAULT VALUES;"
            : "INSERT INTO " + target + " (" + string.Join(", ", assigned.Keys.Select(DatabaseTables.Identifier)) + ")\nVALUES (" + string.Join(", ", assigned.Values) + ");";
        return new TableRowBatch([statement], statement, 1);
    }

    private static TableRowPlan Delete(DatabaseSqlTable table, JsonElement root)
    {
        if (!root.TryGetProperty("keys", out var keys))
        {
            return new TableRowPayloadInvalid("Name the row to delete with a keys object.");
        }

        if (Match(table, keys, out var where) is { } refused)
        {
            return refused;
        }

        var statement = "DELETE FROM " + DatabaseTables.Identifier(table.Name) + "\nWHERE " + where + ";";
        return new TableRowBatch([statement], statement, 1);
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

        var predicates = new Dictionary<string, string>(StringComparer.Ordinal);
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

            if (ValuePredicate(column.Name, property.Value, false) is not { } predicate || !predicates.TryAdd(column.Name, predicate))
            {
                return new TableRowPayloadInvalid("Key " + column.Name + " needs one text or number value.");
            }
        }

        if (primary.FirstOrDefault(column => !predicates.ContainsKey(column.Name)) is { } missing)
        {
            return new TableRowKeyRequired("Name the row's " + missing.Name + " value.");
        }

        where = string.Join(" AND ", primary.Select(column => predicates[column.Name]));
        return null;
    }

    private static string? ValuePredicate(string columnName, JsonElement value, bool allowNull)
    {
        var ident = DatabaseTables.Identifier(columnName);
        return value.ValueKind switch
        {
            JsonValueKind.String when Literal(value, false) is { } literal => "typeof(" + ident + ") = 'text' AND " + ident + " COLLATE BINARY = " + literal,
            JsonValueKind.Number => "typeof(" + ident + ") IN ('integer', 'real') AND " + ident + " = " + value.GetRawText(),
            JsonValueKind.Null when allowNull => ident + " IS NULL",
            _ => null,
        };
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
