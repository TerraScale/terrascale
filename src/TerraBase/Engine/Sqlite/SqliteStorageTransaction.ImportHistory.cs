namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    private bool _importHistory;

    // Receiver-side projections can outgrow the incoming group; stage their history on disk, not in a managed list.
    private void BeginImportHistory()
    {
        if (_importHistory)
        {
            return;
        }

        Command("""
            CREATE TEMP TABLE imported_history (
                collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT
            );
            CREATE INDEX temp.imported_history_keys ON imported_history (collection, id);
            """).ExecuteNonQuery();
        _importHistory = true;
    }

    private void ImportHistory(DocumentChange change)
    {
        BeginImportHistory();
        var command = BodyCommand("INSERT INTO temp.imported_history (collection, id, version, body) VALUES ($collection, $id, $version, $body);",
            change.Collection, change.Id);
        command.Parameters.AddWithValue("$version", change.Version);
        command.Parameters.AddWithValue("$body", change.Json is { } json ? (object)StrictUtf8.GetString(json.Span) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void CommitImportHistory(long offset)
    {
        if (!_importHistory)
        {
            return;
        }

        // A source group can project arbitrarily many receiver bodies. Keep atomicity and expire old shape cursors
        // when that projection cannot fit one bounded log chunk; the current state remains available as a snapshot.
        var bytes = Command("SELECT coalesce(sum(coalesce(octet_length(body), 0) + 6 * octet_length(id) + 128), 0) FROM temp.imported_history;")
            .ExecuteScalar();
        if (bytes is long total && total > MaxWrittenBytes)
        {
            var floor = Command("UPDATE shape_history SET floor = max(floor, $offset) WHERE id = 1;");
            floor.Parameters.AddWithValue("$offset", offset);
            floor.ExecuteNonQuery();
        }

        // Snapshots at the new floor still need every projected state, even when the log cursor expired.
        var command = Command("""
            INSERT INTO change_log (collection, generation, id, version, document_version, body)
            SELECT h.collection, c.generation, h.id, $offset, h.version, h.body
            FROM temp.imported_history h JOIN collections c ON c.name = h.collection ORDER BY h.rowid;
            """);
        command.Parameters.AddWithValue("$offset", offset);
        command.ExecuteNonQuery();
        Command("DROP TABLE temp.imported_history;").ExecuteNonQuery();
    }

    private void DropImportHistory(string collection)
    {
        if (_importHistory)
        {
            Command("DELETE FROM temp.imported_history WHERE collection = $collection;", collection).ExecuteNonQuery();
        }
    }
}
