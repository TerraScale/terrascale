using System.Text.Json;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Tests;

public static class StoreRows
{
    public static async Task<IReadOnlyList<StoredDocument>> RowsAsync(this TerraBaseDatabase database, string collection, int limit = 10000) =>
        [.. (await database.QuerySqlAsync($"SELECT id, body, version FROM {Table(collection)} ORDER BY id LIMIT ?", [limit])).Select(Read)];

    public static async Task<StoredDocument?> RowAsync(this TerraBaseDatabase database, string collection, string id, CancellationToken cancellationToken = default)
    {
        var rows = await database.QuerySqlAsync($"SELECT id, body, version FROM {Table(collection)} WHERE id = ?", [id], cancellationToken);
        return rows.Count == 0 ? null : Read(rows[0]);
    }

    public static async Task<string> OverwriteAsync(this TerraBaseDatabase database, string collection, string id, JsonElement document, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        var version = AuthStore.NewId();
        var scope = document.ValueKind == JsonValueKind.Object && document.TryGetProperty("instanceId", out var instance) && instance.ValueKind == JsonValueKind.String ? instance.GetString() : null;
        var result = expectedVersion is null
            ? await database.ExecuteSqlAsync($"INSERT INTO {Table(collection)} (id, instance_id, version, body) VALUES (?, ?, ?, ?) ON CONFLICT (id) DO NOTHING",
                [id, scope, version, document.GetRawText()], cancellationToken)
            : await database.ExecuteSqlAsync($"UPDATE {Table(collection)} SET instance_id = ?, version = ?, body = ? WHERE id = ? AND version = ?",
                [scope, version, document.GetRawText(), id, expectedVersion], cancellationToken);
        return result.RowsAffected == 1 ? version : throw new StorePreconditionFailedException(0);
    }

    public static async Task<bool> RemoveRowAsync(this TerraBaseDatabase database, string collection, string id, CancellationToken cancellationToken = default) =>
        (await database.ExecuteSqlAsync($"DELETE FROM {Table(collection)} WHERE id = ?", [id], cancellationToken)).RowsAffected == 1;

    public static async Task CreateTableAsync(this TerraBaseDatabase database, string collection, CancellationToken cancellationToken = default) =>
        await database.ExecuteSqlAsync($"CREATE TABLE {Table(collection)} (id TEXT PRIMARY KEY NOT NULL, instance_id TEXT, version TEXT NOT NULL CHECK (length(version) = 26), body TEXT NOT NULL CHECK (json_valid(body)))",
            cancellationToken: cancellationToken);

    public static async Task DropTableAsync(this TerraBaseDatabase database, string collection, CancellationToken cancellationToken = default) =>
        await database.ExecuteSqlAsync($"DROP TABLE {Table(collection)}", cancellationToken: cancellationToken);

    public static async Task<IReadOnlyList<string>> TableNamesAsync(this TerraBaseDatabase database, CancellationToken cancellationToken = default) =>
        [.. (await database.QuerySqlAsync("SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name", cancellationToken: cancellationToken)).Select(row => row.GetString(0))];

    public static string Table(string collection) =>
        collection.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')
            ? "\"" + collection + "\""
            : throw new ArgumentException("Tables are lowercase snake_case.", nameof(collection));

    private static StoredDocument Read(TerraBaseSqlRecord row)
    {
        using var body = JsonDocument.Parse(row.GetString("body"));
        return new StoredDocument(row.GetString("id"), body.RootElement.Clone(), row.GetString("version"));
    }
}
