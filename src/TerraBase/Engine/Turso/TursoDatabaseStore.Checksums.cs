using TerraBase.Engine.Revisions;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    internal async Task<RevisionChecksums> ChecksumAsync(DatabaseRevision expected, ChecksumCursor? cursor, CancellationToken cancellationToken)
    {
        if (Ledger is { } ledger && !IsSystem)
        {
            await ledger.SettleAsync(this, cancellationToken).ConfigureAwait(false);
        }

        ThrowIfRefused(maintenance: false);
        var reader = await RentAsync(cancellationToken).ConfigureAwait(false);
        RevisionChecksums result;
        try
        {
            var revision = await WithWriterAsync(writer =>
            {
                RefuseFaultedCopy();
                Revisions.Catch(writer.Connection);
                TursoRevisionLedger.BeginSnapshot(reader.Connection);
                try
                {
                    return Revisions.SnapshotRevision(reader.Connection);
                }
                catch
                {
                    TursoRevisionLedger.EndSnapshot(reader.Connection);
                    throw;
                }
            }, "a replica checksum", cancellationToken).ConfigureAwait(false);
            try
            {
                result = revision == expected
                    ? await RoundAsync(reader.Connection, revision, cursor, cancellationToken).ConfigureAwait(false)
                    : new RevisionChecksums(revision, null, null);
            }
            finally
            {
                await io.RunAsync(() => TursoRevisionLedger.EndSnapshot(reader.Connection), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            await io.RunAsync(() => pool.Discard(reader), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await ReturnAsync(reader).ConfigureAwait(false);
        return result;
    }

    private async Task<RevisionChecksums> RoundAsync(SqliteConnection reader, DatabaseRevision revision, ChecksumCursor? cursor, CancellationToken cancellationToken)
    {
        var ranges = new List<RangeChecksum>();
        var (tables, schema) = await io.RunAsync(() =>
        {
            using var hasher = new RangeChecksums.Hasher();
            foreach (var row in Rows(reader, RangeChecksums.SchemaSql))
            {
                hasher.Add(row);
            }

            return (Rows(reader, RangeChecksums.TablesSql(cursor?.Table ?? "")).Select(row => (string)row[0]!).ToList(), hasher.Finish(RangeChecksums.Schema, 0, 0));
        }, cancellationToken).ConfigureAwait(false);
        ranges.Add(schema);
        long bytes = 0;
        foreach (var table in tables)
        {
            var after = cursor is { } start && start.Table == table ? start.After : long.MinValue;
            while (after != long.MaxValue)
            {
                if (ranges.Count > RangeChecksums.MaxRanges || bytes >= RangeChecksums.RoundBytes)
                {
                    return new RevisionChecksums(revision, ranges, new ChecksumCursor(table, after));
                }

                var from = after;
                var range = await io.RunAsync(() => Range(reader, table, from, cancellationToken), cancellationToken).ConfigureAwait(false);
                ranges.Add(range);
                bytes += range.Bytes;
                after = range.Last;
            }
        }

        return new RevisionChecksums(revision, ranges, null);
    }

    private static RangeChecksum Range(SqliteConnection reader, string table, long after, CancellationToken cancellationToken)
    {
        using var hasher = new RangeChecksums.Hasher();
        using var command = reader.CreateCommand();
        command.CommandText = RangeChecksums.RowsSql(table, after, long.MaxValue, RangeChecksums.RangeRows);
        using var rows = command.ExecuteReader();
        var values = new object?[rows.FieldCount];
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = rows.IsDBNull(index) ? null : rows.GetValue(index);
            }

            hasher.Add(values);
            if (hasher.Bytes >= RangeChecksums.RangeBytes)
            {
                break;
            }
        }

        var last = hasher.Rows == RangeChecksums.RangeRows || hasher.Bytes >= RangeChecksums.RangeBytes ? hasher.LastRowid : long.MaxValue;
        return hasher.Finish(table, after, last);
    }

    private static List<object?[]> Rows(SqliteConnection reader, string sql)
    {
        using var command = reader.CreateCommand();
        command.CommandText = sql;
        using var rows = command.ExecuteReader();
        var result = new List<object?[]>();
        while (rows.Read())
        {
            var values = new object?[rows.FieldCount];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = rows.IsDBNull(index) ? null : rows.GetValue(index);
            }

            result.Add(values);
        }

        return result;
    }
}
