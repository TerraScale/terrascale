using System.Text.Json;
using TerraBase;

internal static class SqliteStorageTests
{
    internal static async Task Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"terrabase-sqlite-{Guid.NewGuid():N}.db");
        var key = new DocumentKey(new TenantId("sqlite-tenant"), new CollectionId("users"), "user-1");
        try
        {
            await using (var provider = new SqliteStorageProvider(path))
            {
                using var body = JsonDocument.Parse("{\"name\":\"Ada\"}");
                var created = await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
                Assert(created.Outcome == WriteOutcome.Created && created.Document?.Version == 1, "SQLite creates version one");
                var conflict = await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Version(99));
                Assert(conflict.Outcome == WriteOutcome.Conflict, "SQLite enforces version preconditions");
                var read = await provider.ReadAsync(key);
                Assert(read?.Body.GetProperty("name").GetString() == "Ada", "SQLite reads the committed blob");
                var deleted = await provider.DeleteAsync(key, DocumentPrecondition.Version(1));
                Assert(deleted.Outcome == WriteOutcome.Deleted, "SQLite deletes transactionally");
                var recreated = await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
                Assert(recreated.Document?.Version == 2, "SQLite preserves version watermarks after delete");
            }

            await using (var reopened = new SqliteStorageProvider(path))
            {
                var read = await reopened.ReadAsync(key);
                Assert(read?.Version == 2, "SQLite data survives close and reopen");
                var documents = new List<Document>();
                await foreach (var document in reopened.ScanAsync(key.Tenant, key.Collection))
                {
                    documents.Add(document);
                }

                Assert(documents.Count == 1 && documents[0].Key == key, "SQLite scans deterministically");
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
