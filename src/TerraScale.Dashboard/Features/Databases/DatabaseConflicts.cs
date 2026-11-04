using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Features.Data;

namespace TerraScale.Dashboard.Features.Databases;

// Customer SQL is the contract: never read TerraBase's private apply tables or infer historical winners from current rows.
public sealed class DatabaseConflicts(DataService data)
{
    public async Task<DatabaseConflictList> ListAsync(AccountContext context, string databaseId, string table, string since, string until,
        int limit, string before, CancellationToken cancellationToken)
    {
        if (table.Length > 256 || limit is < 1 or > 100 || before.Length > 26)
        {
            throw new AccountAccessException("Use a table name up to 256 characters, a limit from 1 to 100, and a conflict ID for before.", 422);
        }

        var start = Instant(since, "since");
        var end = Instant(until, "until");
        if (start is not null && end is not null && start >= end)
        {
            throw new AccountAccessException("Since must be earlier than until.", 422);
        }

        var conditions = new List<string>();
        if (table.Length > 0) { conditions.Add("table_name = " + Literal(table)); }
        if (start is { } from) { conditions.Add("julianday(created_at) >= julianday(" + Literal(from.ToString("O", CultureInfo.InvariantCulture)) + ")"); }
        if (end is { } to) { conditions.Add("julianday(created_at) < julianday(" + Literal(to.ToString("O", CultureInfo.InvariantCulture)) + ")"); }
        if (before.Length > 0) { conditions.Add("id < " + Literal(before)); }
        var sql = "SELECT * FROM terrascale.conflicts" + (conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions))
            + " ORDER BY id DESC LIMIT " + (limit + 1).ToString(CultureInfo.InvariantCulture);
        var result = await data.SqlAsync(context, databaseId, sql, false, cancellationToken);
        var indexes = result.Columns.Select((column, index) => (column.Name, index)).ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
        var items = result.Rows.Take(limit).Select(row => Read(row, indexes)).ToArray();
        return new(items, result.Rows.Count > limit ? items[^1].Id : null, result.Rows.Count > limit, limit);
    }

    private static DatabaseConflict Read(IReadOnlyList<JsonElement> row, IReadOnlyDictionary<string, int> indexes)
    {
        string? Text(string name)
        {
            if (!indexes.TryGetValue(name, out var index) || index >= row.Count) { throw new InvalidDataException("Incomplete conflict response."); }
            return row[index].ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => row[index].GetString(), _ => throw new InvalidDataException("Invalid conflict response.") };
        }
        string Required(string name) => Text(name) ?? throw new InvalidDataException("Incomplete conflict response.");
        JsonElement? Json(string name) => Text(name) is { } json ? JsonSerializer.Deserialize(json, DatabaseConflictJson.Default.JsonElement) : null;
        var state = Required("loser_state");
        if (state is not ("superseded" or "reverted")) { throw new InvalidDataException("Invalid conflict state."); }
        return new(Required("id"), Required("kind"), Text("table_name"), Json("row_key"), Text("column_name"),
            new(Json("winner_value"), Text("winner_region"), Text("winner_node"), Text("winner_hlc"), Text("winner_transaction_id"), Text("winner_state")),
            new(Json("loser_value"), Required("loser_region"), Required("loser_node"), Required("loser_hlc"), Required("loser_transaction_id"), state),
            Required("message"), Required("created_at"));
    }

    private static DateTimeOffset? Instant(string value, string field)
    {
        if (value.Length == 0) { return null; }
        // The form uses UTC datetime-local; MCP callers can also pass an explicit ISO 8601 offset.
        if (value.Length > 40 || !DateTimeOffset.TryParseExact(value,
            ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        { throw new AccountAccessException("Use an ISO 8601 timestamp for " + field + ".", 422); }
        return parsed;
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

public sealed record DatabaseConflict(string Id, string Kind, string? TableName, JsonElement? RowKey, string? ColumnName,
    DatabaseConflictWrite Winner, DatabaseConflictWrite Loser, string Message, string CreatedAt);

public sealed record DatabaseConflictWrite(JsonElement? Value, string? Region, string? Node, string? Hlc, string? TransactionId, string? State)
{
    public string? TransactionState => State == "superseded" ? "confirmed" : State;
    [JsonIgnore]
    public string DisplayValue => Value?.GetRawText() ?? "Not recorded";
}

public sealed record DatabaseConflictList(IReadOnlyList<DatabaseConflict> Conflicts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextBefore, bool HasMore, int Limit);

public sealed record DatabaseConflictsPage(DatabaseConflictList? Listing, string Table, string Since, string Until, string Before, string Error = "")
{
    public string Path(string databaseId, string before = "") => DatabaseService.Path(databaseId, "conflicts") + "?table=" + Uri.EscapeDataString(Table)
        + "&since=" + Uri.EscapeDataString(Since) + "&until=" + Uri.EscapeDataString(Until) + (before.Length == 0 ? "" : "&before=" + Uri.EscapeDataString(before));
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DatabaseConflictList))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class DatabaseConflictJson : JsonSerializerContext;
