using System.Text.Json;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Tests;

public static class StoreRows
{
    public static async Task<IReadOnlyList<StoredRow>> RowsAsync(this TerraBaseSqlDatabase database, string table, int limit = 10000) =>
        [.. (await database.QueryAsync($"SELECT id, body, version FROM {Table(table)} ORDER BY id LIMIT ?", [limit])).Select(Read)];

    public static async Task<StoredRow?> RowAsync(this TerraBaseSqlDatabase database, string table, string id, CancellationToken cancellationToken = default)
    {
        var rows = await database.QueryAsync($"SELECT id, body, version FROM {Table(table)} WHERE id = ?", [id], cancellationToken);
        return rows.Count == 0 ? null : Read(rows[0]);
    }

    public static async Task<string> OverwriteAsync(this TerraBaseSqlDatabase database, string table, string id, JsonElement record, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        var version = AuthStore.NewId();
        var scope = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("instanceId", out var instance) && instance.ValueKind == JsonValueKind.String ? instance.GetString() : null;
        var result = expectedVersion is null
            ? await database.ExecuteAsync($"INSERT INTO {Table(table)} (id, instance_id, version, body) VALUES (?, ?, ?, ?) ON CONFLICT (id) DO NOTHING",
                [id, scope, version, record.GetRawText()], cancellationToken)
            : await database.ExecuteAsync($"UPDATE {Table(table)} SET instance_id = ?, version = ?, body = ? WHERE id = ? AND version = ?",
                [scope, version, record.GetRawText(), id, expectedVersion], cancellationToken);
        return result.RowsAffected == 1 ? version : throw new StorePreconditionFailedException(0);
    }

    public static async Task<bool> RemoveRowAsync(this TerraBaseSqlDatabase database, string table, string id, CancellationToken cancellationToken = default) =>
        (await database.ExecuteAsync($"DELETE FROM {Table(table)} WHERE id = ?", [id], cancellationToken)).RowsAffected == 1;

    public static async Task CreateTableAsync(this TerraBaseSqlDatabase database, string table, CancellationToken cancellationToken = default) =>
        await database.ExecuteAsync($"CREATE TABLE {Table(table)} (id TEXT PRIMARY KEY NOT NULL, instance_id TEXT, version TEXT NOT NULL CHECK (length(version) = 26), body TEXT NOT NULL CHECK (json_valid(body)))",
            cancellationToken: cancellationToken);

    public static async Task DropTableAsync(this TerraBaseSqlDatabase database, string table, CancellationToken cancellationToken = default) =>
        await database.ExecuteAsync($"DROP TABLE {Table(table)}", cancellationToken: cancellationToken);

    public static async Task<IReadOnlyList<string>> TableNamesAsync(this TerraBaseSqlDatabase database, CancellationToken cancellationToken = default) =>
        [.. (await database.QueryAsync("SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name", cancellationToken: cancellationToken)).Select(row => row.GetString(0))];

    public static string Table(string table) =>
        table.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')
            ? "\"" + table + "\""
            : throw new ArgumentException("Tables are lowercase snake_case.", nameof(table));

    private static StoredRow Read(TerraBaseSqlRecord row)
    {
        using var body = JsonDocument.Parse(row.GetString("body"));
        return new StoredRow(row.GetString("id"), body.RootElement.Clone(), row.GetString("version"));
    }
}
