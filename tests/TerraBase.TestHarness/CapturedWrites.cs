using TerraBase.Engine.Turso;

namespace TerraBase.Testing;

internal static class CapturedWrites
{
    internal sealed record CapturedCommit(long ChangeId, IReadOnlyList<string> Ids);

    internal static async Task<long> HeadAsync(TursoDatabaseStore store, CancellationToken token)
    {
        await using var read = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, token);
        return await read.LedgerAsync(command =>
        {
            using var query = command("SELECT COALESCE(MAX(change_id), 0) FROM turso_cdc WHERE change_type = 2;");
            return (long)query.ExecuteScalar()!;
        }, token);
    }

    internal static async Task<IReadOnlyList<CapturedCommit>> ReadAsync(TursoDatabaseStore store, long after, CancellationToken token)
    {
        await using var read = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, token);
        return await read.LedgerAsync<IReadOnlyList<CapturedCommit>>(command =>
        {
            using var query = command("SELECT change_id, change_type, table_name, after FROM turso_cdc WHERE change_id > $after ORDER BY change_id;");
            query.Parameters.AddWithValue("$after", after);
            using var rows = query.ExecuteReader();
            List<CapturedCommit> commits = [];
            List<string> ids = [];
            while (rows.Read())
            {
                if (rows.GetInt64(1) == 2)
                {
                    commits.Add(new(rows.GetInt64(0), [.. ids]));
                    ids.Clear();
                }
                else if (rows.GetString(2) == "items" && !rows.IsDBNull(3)
                    && SqliteRecord.Decode((byte[])rows.GetValue(3)) is [string id, ..])
                {
                    ids.Add(id);
                }
            }
            return commits;
        }, token);
    }
}
