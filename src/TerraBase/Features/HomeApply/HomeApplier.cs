using System.Globalization;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Clock;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

using TerraBase.Features.Sql;

namespace TerraBase.Features.HomeApply;

internal sealed partial class HomeApplier(IStorageEngine engine, IHlcClock clock, IHomeWriteLease leases, TerraBaseOptions options, TimeProvider time, ILogger<HomeApplier> logger)
    : IHomeApply
{
    internal const int MaxChanges = 1024;
    internal const int KeptConflicts = 10_000;
    private static readonly TimeSpan KeptConflictAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan MaxAhead = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KeptApplied = TimeSpan.FromDays(7);

    private static readonly string[] Tables =
    [
        "CREATE TABLE IF NOT EXISTS _ts_rows (tbl TEXT NOT NULL, pk TEXT NOT NULL, stamps TEXT NOT NULL, deleted TEXT, PRIMARY KEY (tbl, pk))",
        "CREATE TABLE IF NOT EXISTS _ts_applied (id TEXT PRIMARY KEY, region TEXT NOT NULL, state TEXT NOT NULL, result TEXT NOT NULL, applied_at INTEGER NOT NULL)",
        ConflictTable.Ddl,
    ];

    public async ValueTask<HomeApplyResult?> ApplyAsync(TenantContext database, RegionTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        var id = transaction.Id!;
        var region = transaction.Region!;
        var stamp = transaction.Hlc!.Value;
        var skewed = stamp.PhysicalMilliseconds - time.GetUtcNow().ToUnixTimeMilliseconds() > MaxAhead.TotalMilliseconds;
        if (!skewed)
        {
            _ = await clock.ObserveAsync(stamp, cancellationToken).ConfigureAwait(false);
        }

        Rejection rejection;
        var applying = await engine.BeginTransactionAsync(database, write: true, cancellationToken).ConfigureAwait(false);
        if (applying is null)
        {
            return null;
        }

        await using (applying.ConfigureAwait(false))
        {
            await EnsureTablesAsync(applying, cancellationToken).ConfigureAwait(false);
            if (await RecordedAsync(applying, id, cancellationToken).ConfigureAwait(false) is { } recorded)
            {
                return recorded;
            }

            if (skewed)
            {
                rejection = new Rejection("clock", null, null, "the transaction's clock is ahead of the home's by more than 60 seconds");
            }
            else
            {
                var before = await applying.MeasureSqlStorageAsync(cancellationToken).ConfigureAwait(false);
                await applying.SuspendForeignKeysAsync(cancellationToken).ConfigureAwait(false);
                var run = new ApplyRun(applying, new Writer(stamp, region, id), cancellationToken);
                try
                {
                    foreach (var change in transaction.Changes!)
                    {
                        await run.ApplyAsync(change).ConfigureAwait(false);
                    }

                    await run.CheckForeignKeysAsync().ConfigureAwait(false);
                    var result = new HomeApplyResult(id, WriteStates.Confirmed, [.. run.Superseded.Select(conflict => conflict.Public)], []);
                    await RecordAsync(applying, transaction, result, run.Superseded, cancellationToken).ConfigureAwait(false);
                    var growth = await applying.MeasureSqlStorageAsync(cancellationToken).ConfigureAwait(false) - before;
                    if (growth != 0)
                    {
                        await applying.AddSqlStorageAsync(growth, cancellationToken).ConfigureAwait(false);
                    }

                    _ = await applying.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return result;
                }
                catch (RejectedException rejected)
                {
                    rejection = rejected.Rejection;
                }
                catch (SqlStatementException error) when (Rejects(error) is { } kind)
                {
                    rejection = new Rejection(kind, run.Current?.Table, run.Current?.Key, error.Message);
                }
            }
        }

        return await RevertAsync(database, transaction, rejection, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<HomeApplyResult?> StateAsync(TenantContext database, string transactionId, CancellationToken cancellationToken)
    {
        var reading = await engine.BeginTransactionAsync(database, write: false, cancellationToken).ConfigureAwait(false);
        if (reading is null)
        {
            return null;
        }

        await using (reading.ConfigureAwait(false))
        {
            var exists = await reading.RunAsync("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = '_ts_applied'", cancellationToken).ConfigureAwait(false);
            return exists.Rows.Count > 0 && await RecordedAsync(reading, transactionId, cancellationToken).ConfigureAwait(false) is { } recorded
                ? recorded
                : new HomeApplyResult(transactionId, WriteStates.Provisional, [], []);
        }
    }

    public async ValueTask PrepareAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (!await leases.PrepareAsync(database, cancellationToken).ConfigureAwait(false))
        {
            throw new StorageBusyException();
        }

        var preparing = await engine.BeginTransactionAsync(database, write: true, cancellationToken).ConfigureAwait(false);
        if (preparing is null)
        {
            return;
        }

        await using (preparing.ConfigureAwait(false))
        {
            if (await EnsureTablesAsync(preparing, cancellationToken).ConfigureAwait(false))
            {
                _ = await preparing.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask StampHomeWritesAsync(IStorageTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var stamped = await transaction.RunAsync("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = '_ts_rows'", cancellationToken).ConfigureAwait(false);
        if (stamped.Rows.Count == 0)
        {
            return;
        }

        var changes = await transaction.ReadCapturedChangesAsync(cancellationToken).ConfigureAwait(false);
        if (changes.Count == 0)
        {
            return;
        }

        var stamp = new Writer(await clock.NextAsync(cancellationToken).ConfigureAwait(false), options.Region, EntityIds.New(time).ToString());
        var shapes = new Dictionary<string, TableShape?>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (!shapes.TryGetValue(change.Table, out var shape))
            {
                shapes[change.Table] = shape = await ApplySql.ShapeAsync(transaction, change.Table, cancellationToken).ConfigureAwait(false);
            }

            if (shape is not { Keys.Count: > 0 })
            {
                continue;
            }

            var before = change.Before is { } old ? KeyOf(shape, change.Columns, old) : null;
            var after = change.After is { } image ? KeyOf(shape, change.Columns, image) : null;
            if (before is not null && (after is null || before != after))
            {
                var gone = await ApplySql.ReadStampsAsync(transaction, shape.Name, before, cancellationToken).ConfigureAwait(false);
                gone.Columns.Clear();
                gone.Deleted = stamp;
                await ApplySql.WriteStampsAsync(transaction, shape.Name, before, gone, cancellationToken).ConfigureAwait(false);
            }

            if (after is null)
            {
                continue;
            }

            var stamps = await ApplySql.ReadStampsAsync(transaction, shape.Name, after, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < change.Columns.Count; i++)
            {
                if (change.Type != CapturedRowChange.Update || before != after || change.Updated is not { } updated || (i < updated.Length && updated[i]))
                {
                    stamps.Columns[change.Columns[i]] = stamp;
                }
            }

            stamps.Deleted = null;
            await ApplySql.WriteStampsAsync(transaction, shape.Name, after, stamps, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? KeyOf(TableShape shape, IReadOnlyList<string> columns, object?[] image)
    {
        var values = new object?[shape.Keys.Count];
        for (var k = 0; k < values.Length; k++)
        {
            var at = -1;
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].Equals(shape.Keys[k], StringComparison.OrdinalIgnoreCase))
                {
                    at = i;
                    break;
                }
            }

            if (at < 0 || at >= image.Length)
            {
                return null;
            }

            values[k] = image[at];
        }

        return ApplySql.KeyText(values);
    }

    private async ValueTask<HomeApplyResult> RevertAsync(TenantContext database, RegionTransaction transaction, Rejection rejection, CancellationToken cancellationToken)
    {
        var recording = await engine.BeginTransactionAsync(database, write: true, cancellationToken).ConfigureAwait(false)
            ?? throw new StorageBusyException();
        await using (recording.ConfigureAwait(false))
        {
            await EnsureTablesAsync(recording, cancellationToken).ConfigureAwait(false);
            if (await RecordedAsync(recording, transaction.Id!, cancellationToken).ConfigureAwait(false) is { } recorded)
            {
                return recorded;
            }

            var rows = await HomeRowsAsync(recording, transaction, cancellationToken).ConfigureAwait(false);
            var winner = rows.FirstOrDefault(row => row.Row.Table.Equals(rejection.Table, StringComparison.OrdinalIgnoreCase) && row.Row.Key.GetRawText() == rejection.Key);
            var conflict = new ConflictRecord(rejection.Kind, WriteStates.Reverted, rejection.Table, rejection.Key, null, winner.Newest,
                winner.Row?.Values is { } image ? ApplySql.Shown(image.GetRawText()) : null, ApplySql.Shown(ChangesJson(transaction)), rejection.Message);
            var result = new HomeApplyResult(transaction.Id!, WriteStates.Reverted, [conflict.Public], [.. rows.Select(row => row.Row)]);
            await RecordAsync(recording, transaction, result, [conflict], cancellationToken).ConfigureAwait(false);
            _ = await recording.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.ZLogWarning($"Reverted region transaction {transaction.Id:@Transaction} from {transaction.Region:@Region}: {rejection.Kind:@Kind}");
            return result;
        }
    }

    private static async ValueTask<List<(HomeApplyRow Row, Writer? Newest)>> HomeRowsAsync(IStorageTransaction transaction, RegionTransaction region, CancellationToken cancellationToken)
    {
        var rows = new List<(HomeApplyRow Row, Writer? Newest)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shapes = new Dictionary<string, TableShape?>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in region.Changes!)
        {
            if (!shapes.TryGetValue(change.Table!, out var shape))
            {
                shapes[change.Table!] = shape = await ApplySql.ShapeAsync(transaction, change.Table!, cancellationToken).ConfigureAwait(false);
            }

            var source = change.Op == "insert" ? change.Values : change.Key;
            if (shape is not { Keys.Count: > 0 } || source is null || ApplyRun.KeyValues(shape, source) is not { } key)
            {
                continue;
            }

            var keyText = ApplySql.KeyText(key);
            if (!seen.Add(shape.Name + "\n" + keyText))
            {
                continue;
            }

            var image = await ApplySql.ReadRowAsync(transaction, shape, key, cancellationToken).ConfigureAwait(false);
            var stamps = await ApplySql.ReadStampsAsync(transaction, shape.Name, keyText, cancellationToken).ConfigureAwait(false);
            rows.Add((new HomeApplyRow(shape.Name, JsonElement.Parse(ApplySql.Object(shape.Keys, key)),
                image is null ? null : JsonElement.Parse(ApplySql.Object(shape.Columns, image))), image is null ? stamps.Deleted : stamps.Newest));
        }

        return rows;
    }

    private static string ChangesJson(RegionTransaction transaction) => ApplySql.Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var change in transaction.Changes!)
        {
            writer.WriteStartObject();
            writer.WriteString("op", change.Op);
            writer.WriteString("table", change.Table);
            foreach (var (name, values) in (ReadOnlySpan<(string, IReadOnlyDictionary<string, JsonElement>?)>)[("key", change.Key), ("values", change.Values)])
            {
                if (values is null)
                {
                    continue;
                }

                writer.WriteStartObject(name);
                foreach (var (column, value) in values)
                {
                    writer.WritePropertyName(column);
                    value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    });

    private static async ValueTask<bool> EnsureTablesAsync(IStorageTransaction transaction, CancellationToken cancellationToken)
    {
        var present = await transaction.RunAsync("SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('_ts_rows', '_ts_applied', '{ConflictTable.Name}')", cancellationToken).ConfigureAwait(false);
        if (present.Rows is [[long count]] && count == Tables.Length)
        {
            return false;
        }

        foreach (var table in Tables)
        {
            _ = await transaction.RunAsync(table, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static async ValueTask<HomeApplyResult?> RecordedAsync(IStorageTransaction transaction, string id, CancellationToken cancellationToken)
    {
        var found = await transaction.RunAsync("SELECT result FROM _ts_applied WHERE id = ?", [id], cancellationToken).ConfigureAwait(false);
        return found.Rows is [[string result]] ? JsonSerializer.Deserialize(result, HomeApplyJsonContext.Default.HomeApplyResult) : null;
    }

    private async ValueTask RecordAsync(
        IStorageTransaction transaction, RegionTransaction region, HomeApplyResult result, List<ConflictRecord> conflicts, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        _ = await transaction.RunAsync("INSERT INTO _ts_applied (id, region, state, result, applied_at) VALUES (?, ?, ?, ?, ?)", [region.Id, region.Region, result.State, JsonSerializer.Serialize(result, HomeApplyJsonContext.Default.HomeApplyResult), now.ToUnixTimeMilliseconds()], cancellationToken)
            .ConfigureAwait(false);
        _ = await transaction.RunAsync("DELETE FROM _ts_applied WHERE id IN (SELECT id FROM _ts_applied WHERE id < ? LIMIT 16)", [FloorId(now - KeptApplied)], cancellationToken).ConfigureAwait(false);
        foreach (var conflict in conflicts)
        {
            var winner = conflict.Winner;
            _ = await transaction.RunAsync(
                ConflictTable.Insert,
                [EntityIds.New(time).ToString(), conflict.Kind, conflict.Table, conflict.Key, conflict.Column, conflict.WinnerValue, conflict.LoserValue,
                    winner?.Region, winner?.Stamp.NodeId, winner is null ? null : StampText.Format(winner.Stamp), winner?.Transaction,
                    winner is null ? null : WriteStates.Confirmed, region.Region, region.Hlc!.Value.NodeId, StampText.Format(region.Hlc.Value), region.Id,
                    conflict.LoserState, conflict.Message, now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)],
                cancellationToken).ConfigureAwait(false);
        }

        if (conflicts.Count > 0)
        {
            _ = await transaction.RunAsync("DELETE FROM _ts_conflicts WHERE id IN (SELECT id FROM _ts_conflicts WHERE id < ? LIMIT 64)",
                [FloorId(now - KeptConflictAge)], cancellationToken).ConfigureAwait(false);
            _ = await transaction.RunAsync("DELETE FROM _ts_conflicts WHERE id IN (SELECT id FROM _ts_conflicts ORDER BY id DESC LIMIT -1 OFFSET ?)",
                [(long)KeptConflicts], cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FloorId(DateTimeOffset at)
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var milliseconds = at.ToUnixTimeMilliseconds();
        Span<char> id = stackalloc char[26];
        for (var i = 9; i >= 0; i--)
        {
            id[i] = Alphabet[(int)(milliseconds & 31)];
            milliseconds >>= 5;
        }

        id[10..].Fill('0');
        return new string(id);
    }

    private static string? Rejects(SqlStatementException error) => error.Failure switch
    {
        Unique => "unique",
        PrimaryKey => "primary_key",
        NotNull => "not_null",
        Check => "check",
        ForeignKeyFailure => "foreign_key",
        Constraint or TooBig => "constraint",
        NoSuchTable or NoSuchColumn or Syntax => "schema",
        Error or AlreadyExists or Busy or Interrupted or ReadOnly or StaleRow => null,
    };
}
