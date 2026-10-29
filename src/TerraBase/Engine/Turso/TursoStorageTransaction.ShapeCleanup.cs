using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    private const string CleanupColumns = "object_key, state, attempt";

    internal ValueTask<ShapeCleanupAdmission> ReserveShapeCleanupAsync(ShapeCleanupObject item, long now, CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var fence = Command("SELECT 1 FROM documents WHERE collection = $collection AND id = $database;", DatabaseErasure.Collection);
            fence.Parameters.AddWithValue("$database", item.Database.DatabaseId);
            if (fence.ExecuteScalar() is not null)
            {
                return ShapeCleanupAdmission.Refused;
            }

            bool? live = null;
            var existing = Command("""
                SELECT state = 0 AND deadline > $now AND organization_id = $organization AND project_id = $project
                    AND database_id = $database AND incarnation = $incarnation
                FROM _ts_shape_cleanup WHERE object_key = $key;
                """);
            Bind(existing, item);
            existing.Parameters.AddWithValue("$now", now);
            if (existing.ExecuteScalar() is long current)
            {
                live = current == 1;
            }

            if (live is { } found)
            {
                if (found)
                {
                    var renew = Command("UPDATE _ts_shape_cleanup SET upload_until = max(upload_until, $until) WHERE object_key = $key;");
                    renew.Parameters.AddWithValue("$key", item.Key);
                    renew.Parameters.AddWithValue("$until", now + (long)ShapeCleanupLimits.Upload.TotalMilliseconds);
                    _ = renew.ExecuteNonQuery();
                }

                return found ? ShapeCleanupAdmission.Live : ShapeCleanupAdmission.Refused;
            }

            if (item.Deadline <= now)
            {
                return ShapeCleanupAdmission.Refused;
            }

            var usage = CleanupUsage();
            var known = Command("SELECT 1 FROM _ts_shape_cleanup_handles WHERE handle = $handle;");
            known.Parameters.AddWithValue("$handle", item.Key[10..29]);
            var newHandle = known.ExecuteScalar() is null;
            var bytes = ShapeCleanupLimits.RowCost(item);
            if (usage.Objects + 1 > ShapeCleanupLimits.Objects || usage.Bytes + bytes > ShapeCleanupLimits.Bytes
                || newHandle && usage.Handles + 1 > ShapeCleanupLimits.Handles)
            {
                return ShapeCleanupAdmission.Saturated;
            }

            var insert = Command("""
                INSERT INTO _ts_shape_cleanup (object_key, organization_id, project_id, database_id, incarnation, handle, floor_offset,
                    epoch, key_id, deadline, due, state, reason, published, attempt, next_attempt, parked, upload_until, lease, lease_until, bytes)
                VALUES ($key, $organization, $project, $database, $incarnation, $handle, $floor, $epoch, $key_id, $deadline, $deadline,
                    0, 0, 0, 0, 0, 0, $until, '', 0, $bytes);
                """);
            Bind(insert, item);
            insert.Parameters.AddWithValue("$handle", item.Key[10..29]);
            insert.Parameters.AddWithValue("$floor", item.FloorOffset);
            insert.Parameters.AddWithValue("$epoch", item.Epoch);
            insert.Parameters.AddWithValue("$key_id", item.KeyId);
            insert.Parameters.AddWithValue("$deadline", item.Deadline);
            insert.Parameters.AddWithValue("$until", now + (long)ShapeCleanupLimits.Upload.TotalMilliseconds);
            insert.Parameters.AddWithValue("$bytes", bytes);
            _ = insert.ExecuteNonQuery();
            CountHandle(item.Key[10..29], 1, newHandle);
            ChangeUsage(1, bytes, newHandle ? 1 : 0);
            return ShapeCleanupAdmission.Live;
        }, cancellationToken);

    internal ValueTask<bool> PublishShapeCleanupAsync(string key, long epoch, string keyId, long now, CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var publish = Command("""
                UPDATE _ts_shape_cleanup SET published = 1, epoch = $epoch, key_id = $key_id, upload_until = 0
                WHERE object_key = $key AND state = 0 AND deadline > $now;
                """);
            publish.Parameters.AddWithValue("$key", key);
            publish.Parameters.AddWithValue("$epoch", epoch);
            publish.Parameters.AddWithValue("$key_id", keyId);
            publish.Parameters.AddWithValue("$now", now);
            return publish.ExecuteNonQuery() == 1;
        }, cancellationToken);

    internal ValueTask<int> ForgetShapeCleanupAsync(string key, TenantContext database, CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var row = Command("""
                SELECT handle, bytes FROM _ts_shape_cleanup WHERE object_key = $key AND state = 0 AND published = 0
                    AND organization_id = $organization AND project_id = $project AND database_id = $database;
                """);
            row.Parameters.AddWithValue("$key", key);
            BindDatabase(row, database);
            string? handle = null;
            long bytes = 0;
            using (var reader = row.ExecuteReader())
            {
                if (reader.Read())
                {
                    handle = reader.GetString(0);
                    bytes = reader.GetInt64(1);
                }
            }

            return handle is null ? 0 : RemoveCleanupRow(key, handle, bytes);
        }, cancellationToken);

    internal ValueTask<(IReadOnlySet<string> Live, bool Saturated)> LiveShapeCleanupAsync(IReadOnlyList<string> keys, long now, CancellationToken cancellationToken) =>
        CleanupAsync<(IReadOnlySet<string>, bool)>(() =>
        {
            HashSet<string> live = [with(StringComparer.Ordinal)];
            var command = Command("SELECT 1 FROM _ts_shape_cleanup WHERE object_key = $key AND state = 0 AND published = 1 AND deadline > $now;");
            foreach (var key in keys)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$now", now);
                if (command.ExecuteScalar() is not null)
                {
                    live.Add(key);
                }
            }

            return (live, CleanupUsage().Saturated);
        }, cancellationToken);

    internal ValueTask<bool> ProvesShapeCleanupAsync(TenantContext database, string handle, long floor, long epoch, string keyId, long now,
        CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var command = Command("""
                SELECT 1 FROM _ts_shape_cleanup WHERE handle = $handle AND state = 0 AND floor_offset >= $floor AND published = 1
                    AND deadline > $now AND epoch = $epoch AND key_id = $key_id
                    AND organization_id = $organization AND project_id = $project AND database_id = $database LIMIT 1;
                """);
            command.Parameters.AddWithValue("$handle", handle);
            command.Parameters.AddWithValue("$floor", floor);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$epoch", epoch);
            command.Parameters.AddWithValue("$key_id", keyId);
            BindDatabase(command, database);
            return command.ExecuteScalar() is not null;
        }, cancellationToken);

    internal ValueTask<int> ActivateShapeObjectAsync(string key, ShapeCleanupReason reason, long now, CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var command = Activation("object_key = $key", reason, now);
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteNonQuery();
        }, cancellationToken);

    internal ValueTask<int> ActivateShapeHandleAsync(TenantContext database, string handle, long floor, ShapeCleanupReason reason, long now,
        CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var command = Activation("handle = $handle AND (floor_offset < $floor OR deadline <= $now) AND organization_id = $organization AND project_id = $project AND database_id = $database",
                reason, now);
            command.Parameters.AddWithValue("$handle", handle);
            command.Parameters.AddWithValue("$floor", floor);
            BindDatabase(command, database);
            return command.ExecuteNonQuery();
        }, cancellationToken);

    internal ValueTask<int> ActivateShapeDatabaseAsync(TenantContext database, long now, CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var command = Activation("database_id = $database AND organization_id = $organization AND project_id = $project", ShapeCleanupReason.Database, now);
            BindDatabase(command, database);
            return command.ExecuteNonQuery();
        }, cancellationToken);

    internal ValueTask<List<ShapeCleanupItem>> LeaseShapeCleanupAsync(string lease, long now, int limit, CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var candidates = Command($"""
                SELECT {CleanupColumns} FROM _ts_shape_cleanup WHERE state IN (0, 1, 2) AND due <= $now AND parked = 0
                    AND next_attempt <= $now AND lease_until <= $now AND upload_until <= $now
                ORDER BY state, due LIMIT $limit;
                """);
            candidates.Parameters.AddWithValue("$now", now);
            candidates.Parameters.AddWithValue("$limit", (long)Math.Min(limit, ShapeCleanupLimits.Transaction));
            List<ShapeCleanupItem> items = [];
            long bytes = 0;
            using (var reader = candidates.ExecuteReader())
            {
                while (reader.Read() && (bytes += ShapeCleanupItem.Bytes) <= ShapeCleanupLimits.WindowBytes)
                {
                    var key = reader.GetString(0);
                    if (!ShapeCleanupLimits.Canonical(key))
                    {
                        throw new InvalidDataException("a shape cleanup inventory key is not canonical");
                    }

                    items.Add(new(key, (ShapeCleanupStage)Math.Max(1, reader.GetInt64(1)), checked((int)reader.GetInt64(2))));
                }
            }

            var claim = Command("""
                UPDATE _ts_shape_cleanup SET state = max(state, 1), lease = $lease, lease_until = $until WHERE object_key = $key;
                """);
            foreach (var item in items)
            {
                claim.Parameters.Clear();
                claim.Parameters.AddWithValue("$key", item.Key);
                claim.Parameters.AddWithValue("$lease", lease);
                claim.Parameters.AddWithValue("$until", now + (long)ShapeCleanupLimits.Lease.TotalMilliseconds);
                _ = claim.ExecuteNonQuery();
            }

            return items;
        }, cancellationToken);

    internal ValueTask<int> SaveShapeCleanupAsync(string lease, IReadOnlyList<ShapeCleanupProgress> progress, long now, CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var save = Command("""
                UPDATE _ts_shape_cleanup SET state = $state, attempt = $attempt, next_attempt = $next, parked = $parked,
                    due = CASE WHEN $state = 3 THEN deadline ELSE due END,
                    lease = CASE WHEN $held = 1 THEN lease ELSE '' END, lease_until = CASE WHEN $held = 1 THEN lease_until ELSE 0 END
                WHERE object_key = $key AND lease = $lease AND lease_until > $now;
                """);
            var saved = 0;
            foreach (var item in progress)
            {
                save.Parameters.Clear();
                save.Parameters.AddWithValue("$key", item.Key);
                save.Parameters.AddWithValue("$state", (long)item.Stage);
                save.Parameters.AddWithValue("$attempt", (long)item.Attempt);
                save.Parameters.AddWithValue("$next", item.NextAttempt);
                save.Parameters.AddWithValue("$parked", (long)item.Parked);
                save.Parameters.AddWithValue("$held", item.Held ? 1L : 0L);
                save.Parameters.AddWithValue("$lease", lease);
                save.Parameters.AddWithValue("$now", now);
                saved += save.ExecuteNonQuery();
            }

            return saved;
        }, cancellationToken);

    internal ValueTask<int> CollectShapeCleanupAsync(long now, CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var expired = Command($"SELECT object_key, handle, bytes FROM _ts_shape_cleanup WHERE state = 3 AND due <= $now LIMIT {ShapeCleanupLimits.Transaction};");
            expired.Parameters.AddWithValue("$now", now);
            List<(string Key, string Handle, long Bytes)> rows = [];
            using (var reader = expired.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
                }
            }

            return rows.Sum(row => RemoveCleanupRow(row.Key, row.Handle, row.Bytes));
        }, cancellationToken);

    internal ValueTask<int> RecoverShapeCleanupAsync(CancellationToken cancellationToken) =>
        CleanupAsync(() => Command($"""
            UPDATE _ts_shape_cleanup SET lease = '', lease_until = 0, parked = 0, attempt = 0, next_attempt = 0
            WHERE rowid IN (SELECT rowid FROM _ts_shape_cleanup WHERE lease_until != 0 OR parked != 0 LIMIT {ShapeCleanupLimits.Transaction});
            """).ExecuteNonQuery(), cancellationToken);

    internal ValueTask<ShapeCleanupStatus> ShapeCleanupStatusAsync(long now, CancellationToken cancellationToken) =>
        CleanupAsync(() =>
        {
            var usage = CleanupUsage();
            var pending = Command("SELECT count(*), coalesce(sum(parked != 0), 0), coalesce(min(due), 0) FROM _ts_shape_cleanup WHERE state IN (0, 1, 2) AND due <= $now;");
            pending.Parameters.AddWithValue("$now", now);
            using var reader = pending.ExecuteReader();
            _ = reader.Read();
            return usage with { Pending = reader.GetInt64(0), Parked = reader.GetInt64(1), OldestDue = reader.GetInt64(2) };
        }, cancellationToken);

    private ValueTask<T> CleanupAsync<T>(Func<T> call, CancellationToken cancellationToken) =>
        store.IsSystem && store.Captures
            ? RunAsync(call, cancellationToken, mutation: write)
            : throw new InvalidOperationException("shape cleanup belongs to the internal SQL catalog");

    private SqliteCommand Activation(string scope, ShapeCleanupReason reason, long now)
    {
        var command = Command($"""
            UPDATE _ts_shape_cleanup SET state = 1, reason = $reason, due = min(due, $now)
            WHERE rowid IN (SELECT rowid FROM _ts_shape_cleanup WHERE state = 0 AND {scope} LIMIT {ShapeCleanupLimits.Transaction});
            """);
        command.Parameters.AddWithValue("$reason", (long)reason);
        command.Parameters.AddWithValue("$now", now);
        return command;
    }

    private ShapeCleanupStatus CleanupUsage()
    {
        using var reader = Command("SELECT objects, bytes, handles FROM _ts_shape_cleanup_usage WHERE id = 1;").ExecuteReader();
        return reader.Read()
            ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), 0, 0, 0)
            : throw new InvalidDataException("the shape cleanup usage row is missing");
    }

    private int RemoveCleanupRow(string key, string handle, long bytes)
    {
        var delete = Command("DELETE FROM _ts_shape_cleanup WHERE object_key = $key;");
        delete.Parameters.AddWithValue("$key", key);
        if (delete.ExecuteNonQuery() != 1)
        {
            return 0;
        }

        var remaining = CountHandle(handle, -1, created: false);
        ChangeUsage(-1, -bytes, remaining == 0 ? -1 : 0);
        return 1;
    }

    private long CountHandle(string handle, long delta, bool created)
    {
        if (created)
        {
            var insert = Command("INSERT INTO _ts_shape_cleanup_handles (handle, objects) VALUES ($handle, $delta);");
            insert.Parameters.AddWithValue("$handle", handle);
            insert.Parameters.AddWithValue("$delta", delta);
            _ = insert.ExecuteNonQuery();
            return delta;
        }

        var update = Command("UPDATE _ts_shape_cleanup_handles SET objects = objects + $delta WHERE handle = $handle;");
        update.Parameters.AddWithValue("$handle", handle);
        update.Parameters.AddWithValue("$delta", delta);
        _ = update.ExecuteNonQuery();
        var read = Command("SELECT objects FROM _ts_shape_cleanup_handles WHERE handle = $handle;");
        read.Parameters.AddWithValue("$handle", handle);
        var objects = read.ExecuteScalar() as long? ?? throw new InvalidDataException("a shape cleanup handle count is missing");
        if (objects == 0)
        {
            var drop = Command("DELETE FROM _ts_shape_cleanup_handles WHERE handle = $handle;");
            drop.Parameters.AddWithValue("$handle", handle);
            _ = drop.ExecuteNonQuery();
        }

        return objects;
    }

    private void ChangeUsage(long objects, long bytes, long handles)
    {
        var update = Command("UPDATE _ts_shape_cleanup_usage SET objects = objects + $objects, bytes = bytes + $bytes, handles = handles + $handles WHERE id = 1;");
        update.Parameters.AddWithValue("$objects", objects);
        update.Parameters.AddWithValue("$bytes", bytes);
        update.Parameters.AddWithValue("$handles", handles);
        _ = update.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand command, ShapeCleanupObject item)
    {
        command.Parameters.AddWithValue("$key", item.Key);
        command.Parameters.AddWithValue("$incarnation", item.Incarnation.ToString());
        BindDatabase(command, item.Database);
    }

    private static void BindDatabase(SqliteCommand command, TenantContext database)
    {
        command.Parameters.AddWithValue("$organization", database.OrganizationId);
        command.Parameters.AddWithValue("$project", database.ProjectId);
        command.Parameters.AddWithValue("$database", database.DatabaseId);
    }
}
