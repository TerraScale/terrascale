namespace TerraScale.TerraBase.Engine.Sqlite;

/// <summary>
/// Every statement the engine runs against <c>documents</c>. Their query plans are recorded in
/// <c>docs/sqlite-query-plans.md</c> and asserted by the Fast suite, so a plan that falls back to a
/// full scan fails the build.
/// </summary>
internal static class SqliteStatements
{
    public const string Get = "SELECT version, body FROM documents WHERE collection = $collection AND id = $id;";

    public const string Put = """
        INSERT INTO documents (collection, id, version, body) VALUES ($collection, $id, 1, $body)
        ON CONFLICT (collection, id) DO UPDATE SET version = version + 1, body = excluded.body
        RETURNING version;
        """;

    public const string Delete = "DELETE FROM documents WHERE collection = $collection AND id = $id;";

    // Two statements, not `$after IS NULL OR id > $after`: the OR keeps SQLite from seeking to the
    // cursor, so every page would walk the collection from its first id.
    public const string ListFirst = """
        SELECT id, version, body, octet_length(body) FROM documents
        WHERE collection = $collection
        ORDER BY id LIMIT $limit;
        """;

    public const string ListAfter = """
        SELECT id, version, body, octet_length(body) FROM documents
        WHERE collection = $collection AND id > $after
        ORDER BY id LIMIT $limit;
        """;

    /// <summary>Statement name and text, for query-plan checks.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(Get)] = Get,
        [nameof(Put)] = Put,
        [nameof(Delete)] = Delete,
        [nameof(ListFirst)] = ListFirst,
        [nameof(ListAfter)] = ListAfter,
    };
}
