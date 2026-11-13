using TerraBase.Engine;
using TerraBase.Features.FileStorage;
using TerraBase.Features.Management;

namespace TerraBase.Features.Replicas;

internal sealed record RegionalCell(string? Text, long? Integer);
internal sealed record RegionalTable(string Name, IReadOnlyList<RegionalCell[]> Rows);
internal sealed record RegionalRecord(string Kind, string Id, byte[] Json);
internal sealed record RegionalCatalogSnapshot(IReadOnlyList<RegionalTable> Tables, IReadOnlyList<RegionalRecord> Records);

internal static class RegionalCatalog
{
    private const int MaxRows = 10_000;
    private const long MaxBytes = 4 * 1024 * 1024;
    private static readonly string[] Tables =
    [
        "catalog_organizations", "catalog_projects", "catalog_databases", "catalog_database_replicas",
        "catalog_retired_ids", "catalog_pending_cleanup",
    ];
    private static readonly Dictionary<string, object?> NoNamed = [];

    private static bool Shared(string kind) => kind is "catalog" or "api_keys" or "api_key_hashes"
        or "project_closures" or "deleted_databases" or "database_expiry" or "retired_databases" or "database_purges" or "home_moves" or FileErasure.Purges or FileErasure.ProjectPurges
        || kind.StartsWith("database_purge_receipts/", StringComparison.Ordinal)
        || kind.StartsWith("project_closure_receipts/", StringComparison.Ordinal)
        || kind.StartsWith("deleted_databases/", StringComparison.Ordinal)
        || kind.StartsWith("file-storage/", StringComparison.Ordinal)
        || kind.StartsWith("backup_destination_history/", StringComparison.Ordinal)
        || kind.StartsWith("project_closure_purges/", StringComparison.Ordinal)
        || kind.StartsWith("projects/", StringComparison.Ordinal) && (kind.EndsWith("/api_keys", StringComparison.Ordinal)
            || kind.EndsWith("/backup_destination", StringComparison.Ordinal) || kind.EndsWith("/file-buckets", StringComparison.Ordinal))
        || kind.StartsWith("databases/", StringComparison.Ordinal) && (kind.EndsWith("/functions", StringComparison.Ordinal)
            || kind.EndsWith("/backup_policy", StringComparison.Ordinal));

    internal static async Task<RegionalCatalogSnapshot> ExportAsync(IStorageEngine engine, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var tables = new List<RegionalTable>();
        long bytes = 0;
        var count = 0;
        foreach (var name in Tables)
        {
            var result = await transaction.ExecuteStatementAsync(Sql($"SELECT * FROM {name} LIMIT {MaxRows + 1}"), cancellationToken).ConfigureAwait(false);
            if (result.Truncated || result.Rows.Count > MaxRows)
            {
                throw new IOException("regional catalog exceeds the handoff bound");
            }
            var rows = new List<RegionalCell[]>();
            foreach (var row in result.Rows)
            {
                bytes += row.Sum(value => value is string text ? 6L * text.Length + 64 : 64);
                if (bytes > MaxBytes || ++count > MaxRows)
                {
                    throw new IOException("regional catalog exceeds the handoff bound");
                }
                rows.Add([.. row.Select(value => value switch
                {
                    null => new RegionalCell(null, null),
                    string text => new RegionalCell(text, null),
                    long number => new RegionalCell(null, number),
                    _ => throw new InvalidDataException("unsupported regional catalog column"),
                })]);
            }
            tables.Add(new RegionalTable(name, rows));
        }

        var records = new List<RegionalRecord>();
        string? afterKind = null;
        do
        {
            var page = await transaction.ListKindsAsync(afterKind, 100, cancellationToken).ConfigureAwait(false);
            foreach (var kind in page.Kinds.Where(kind => Shared(kind.Name)))
            {
                string? after = null;
                RecordPage entries;
                do
                {
                    entries = await transaction.ListAsync(kind.Name, after, 100, MaxBytes, cancellationToken).ConfigureAwait(false);
                    foreach (var record in entries.Records)
                    {
                        bytes += record.Json.Length * 2L + 6L * (record.Id.Length + kind.Name.Length) + 128;
                        if (bytes > MaxBytes || ++count > MaxRows)
                        {
                            throw new IOException("regional catalog exceeds the handoff bound");
                        }
                        records.Add(new RegionalRecord(kind.Name, record.Id, record.Json.ToArray()));
                    }
                    after = entries.Records.Count > 0 ? entries.Records[^1].Id : null;
                }
                while (entries.HasMore && after is not null);
            }
            afterKind = page.HasMore && page.Kinds.Count > 0 ? page.Kinds[^1].Name : null;
        }
        while (afterKind is not null);
        if (bytes > MaxBytes)
        {
            throw new IOException("regional catalog exceeds the handoff bound");
        }
        return new RegionalCatalogSnapshot(tables, records);
    }

    internal static async Task ImportAsync(IStorageEngine engine, RegionalCatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Tables.Count != Tables.Length || !snapshot.Tables.Select(table => table.Name).SequenceEqual(Tables)
            || snapshot.Records.Any(record => !Shared(record.Kind)))
        {
            throw new InvalidDataException("invalid regional catalog snapshot");
        }
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in snapshot.Tables)
        {
            _ = await transaction.ExecuteStatementAsync(Sql($"DELETE FROM {table.Name}"), cancellationToken).ConfigureAwait(false);
            foreach (var row in table.Rows)
            {
                var args = row.Select(cell => cell.Text is { } text ? (object)text : cell.Integer is { } integer ? integer : null).ToArray();
                _ = await transaction.ExecuteStatementAsync(Sql($"INSERT INTO {table.Name} VALUES ({string.Join(',', row.Select(_ => "?"))})", args), cancellationToken).ConfigureAwait(false);
            }
        }
        string? after = null;
        do
        {
            var page = await transaction.ListKindsAsync(after, 100, cancellationToken).ConfigureAwait(false);
            foreach (var kind in page.Kinds.Where(kind => Shared(kind.Name)))
            {
                _ = await transaction.DropKindAsync(kind.Name, cancellationToken).ConfigureAwait(false);
            }
            after = page.HasMore && page.Kinds.Count > 0 ? page.Kinds[^1].Name : null;
        }
        while (after is not null);
        foreach (var record in snapshot.Records)
        {
            _ = await transaction.PutAsync(record.Kind, record.Id, record.Json, cancellationToken).ConfigureAwait(false);
        }
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqlStatement Sql(string text, params object?[] args) => new(text, args, NoNamed, MaxRows + 1, MaxBytes, TimeSpan.FromSeconds(10));
}
