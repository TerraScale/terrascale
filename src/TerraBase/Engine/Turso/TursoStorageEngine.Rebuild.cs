using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Turso.Data.Sqlite;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Engine.Turso;

/// <summary>
/// The storage half of rebuilding a faulted copy into a new incarnation (docs/terrabase/decisions/0013a, R7): the swap
/// that drains the open copy and moves its file and log aside unwritten, the fresh file in its place, and the verified
/// read of the side copy's states for the salvage. <see cref="ReplicaRebuild"/> decides the order.
/// </summary>
internal sealed partial class TursoStorageEngine : IRebuildStorage
{
    // How long a swap waits for the open copy's writer before it refuses and leaves the copy in place.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    public string SidePath(TenantContext database, Ulid incarnation)
    {
        var (key, path) = Locate(database);
        return Path.Combine(Path.GetDirectoryName(path)!, $".{key.Database}.{incarnation}.rebuild");
    }

    public bool SideExists(string sidePath) => File.Exists(sidePath);

    /// <summary>Whether the database's file is in place, without opening it.</summary>
    internal bool FileExists(TenantContext database) => File.Exists(Locate(database).Path);

    /// <summary>Whether the kernel caches no page of the database's file or its sidecars (<see cref="PageCache"/>), unopened.</summary>
    internal bool Evicted(TenantContext database)
    {
        var path = Locate(database).Path;
        return File.Exists(path) && Sidecars.Prepend(string.Empty).Select(sidecar => path + sidecar).Where(File.Exists).All(PageCache.Evicted);
    }

    public async ValueTask<Ulid?> IncarnationAsync(TenantContext database, CancellationToken cancellationToken) =>
        await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is TursoDatabaseStore store ? store.Incarnation : null;

    public async ValueTask<Ulid?> SwapAsync(TenantContext database, Ulid faultedIncarnation, string sidePath, CancellationToken cancellationToken)
    {
        // Opened first, so the check below reads the file's own identity.
        if (await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore
            || await Ledger.GetAsync(database, cancellationToken).ConfigureAwait(false) is { Phase: RebuildPhase.Pending, NewIncarnation: not null })
        {
            // A refused rebuild (a second uncertain copy kept) moves nothing.
            return null;
        }

        var (key, path) = Locate(database);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ThrowIfPurgeFencedAsync(key.Database, cancellationToken).ConfigureAwait(false);
            RejectDatabaseLinks(sidePath);
            if (!_stores.TryGetValue(key, out var store) || store.Incarnation != faultedIncarnation || _initializing.ContainsKey(key)
                || _pendingIntents.Contains(key) || File.Exists(sidePath))
            {
                return null;
            }

            if (!await DrainAsync(key, store, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            try
            {
                await Io.RunAsync(() =>
                {
                    MoveAside(path, sidePath);
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Moved back: the old file is in place again, and opens as the faulted incarnation it is.
                return null;
            }

            await PublishAsync(key.Database, path, temporary => TursoDatabaseStore.InitializeAsync(key.Database, temporary, Io, _pragmas, Encryption, cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            ExitCreation();
        }

        return await IncarnationAsync(database, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Ulid?> PublishFreshAsync(TenantContext database, string sidePath, CancellationToken cancellationToken)
    {
        // Opened first, outside the creation lock, which the ledger's reads of the system database may take.
        var inPlace = await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) as TursoDatabaseStore;
        var record = await Ledger.GetAsync(database, cancellationToken).ConfigureAwait(false);
        if (record is { Phase: RebuildPhase.Pending, NewIncarnation: not null })
        {
            // A second uncertain copy is in place beside one already kept: it stays, and so does everything else.
            return null;
        }

        var additional = record is { Phase: RebuildPhase.Pending } ? record.AdditionalIncarnation : null;
        var (key, path) = Locate(database);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ThrowIfPurgeFencedAsync(key.Database, cancellationToken).ConfigureAwait(false);
            // Under the creation lock, which a deletion holds while its files go: a deleted database (its side copy gone with
            // it) or one a lifecycle operation owns is never published again.
            if (_pendingIntents.Contains(key) || !File.Exists(sidePath))
            {
                return null;
            }
            if (additional is { } retained && inPlace is not null && (inPlace.Incarnation == retained || !File.Exists(SidePath(database, retained))))
            {
                // A copy under rebuild faulted: uncertified, it may still hold verified states its side copy lacks, so it is
                // kept beside it, unwritten, for the salvage. Once it is aside, a file in place is the fresh one published
                // after it, which nothing wrote (a pending record refuses every write); before, any other file refuses.
                if (inPlace.Incarnation != retained || !_stores.TryGetValue(key, out var store) || store != inPlace
                    || !await DrainAsync(key, store, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var retainedPath = SidePath(database, retained);
                await Io.RunAsync(() =>
                {
                    RejectDatabaseLinks(retainedPath);
                    MoveAside(path, retainedPath);
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }

            if (!File.Exists(path))
            {
                // A move aside stopped between the main file and its log: the log follows its file first, or the fresh file
                // would open beside another file's log. The retained copy moves only after the swap finished, so a log left
                // behind while it is aside is its own.
                var target = additional is { } moved && File.Exists(SidePath(database, moved)) ? SidePath(database, moved) : sidePath;
                _ = await Io.RunAsync(() =>
                {
                    foreach (var sidecar in Sidecars.Where(sidecar => File.Exists(path + sidecar)))
                    {
                        RejectLink(path + sidecar);
                        File.Move(path + sidecar, target + sidecar, overwrite: false);
                    }

                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }

            _ = await CreateFileAsync(key.Database, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitCreation();
        }

        return await IncarnationAsync(database, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> CopyForSalvageAsync(string sidePath, CancellationToken cancellationToken)
    {
        var working = sidePath + ".salvage";
        return await RunSalvageAsync(sidePath, operation => Io.RunAsync(() =>
        {
            _checkpoint("rebuild.salvage_copy", operation.Database);
            // The retained side copy is only read by copies, never opened in place.
            DeleteFiles(working);
            RejectLink(sidePath);
            File.Copy(sidePath, working, overwrite: false);
            foreach (var sidecar in Sidecars.Where(sidecar => File.Exists(sidePath + sidecar)))
            {
                RejectLink(sidePath + sidecar);
                File.Copy(sidePath + sidecar, working + sidecar, overwrite: false);
            }

            return working;
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SideRow>> MeasureSideAsync(
        string workingPath, string afterCollection, string afterId, int maxRows, long maxBytes, CancellationToken cancellationToken) =>
        await ReadSalvageAsync(workingPath,
            connection => MeasureSide(connection, afterCollection, afterId, maxRows, maxBytes, cancellationToken), cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<RepairState>> ReadSideAsync(string workingPath, IReadOnlyList<SideRow> rows, CancellationToken cancellationToken)
    {
        var (states, skipped) = await ReadSalvageAsync(workingPath,
            connection => ReadSide(connection, rows, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (skipped > 0)
        {
            _logger.ZLogWarning($"A rebuild's salvage skipped {skipped:@Count} side-copy states that do not verify against their stored digests");
        }

        return states;
    }

    public async ValueTask DiscardWorkingCopyAsync(string workingPath, CancellationToken cancellationToken)
    {
        Io.Admit();
        try
        {
            _ = await Io.RunAsync(() =>
            {
                DeleteFiles(workingPath);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }
    }

    public async ValueTask<bool> FinalizeAsync(TenantContext database, Ulid rebuiltIncarnation, string sidePath, CancellationToken cancellationToken)
    {
        if (await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not TursoDatabaseStore)
        {
            return false;
        }

        var (key, _) = Locate(database);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Under the creation lock nothing replaces or removes the rebuilt file between these checks and the deletion: it
            // is the certified incarnation, and it holds the barrier the ledger's record names.
            if (!_stores.TryGetValue(key, out var store) || store.Incarnation != rebuiltIncarnation)
            {
                return false;
            }

            return await store.WithWritersHeldAsync(() => Ledger.FinalizeCertifiedAsync(database, store, rebuiltIncarnation,
                () => Io.RunAsync(() =>
                {
                    DeleteFiles(sidePath + ".salvage");
                    DeleteFiles(sidePath);
                    return true;
                }, CancellationToken.None), cancellationToken), cancellationToken, "a rebuild deleting its certified side copy").ConfigureAwait(false);
        }
        finally
        {
            ExitCreation();
        }
    }

    // Retires `store` and closes it once no write transaction of this process is open on it, under the creation lock;
    // false, the store open and in place, when it does not drain in time.
    private async Task<bool> DrainAsync((string Organization, string Project, string Database) key, TursoDatabaseStore store, CancellationToken cancellationToken)
    {
        // Retired before the drain, under the creation lock: nothing opens the path again until the caller ends.
        _ = _stores.TryRemove(key, out _);
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(DrainTimeout);
        try
        {
            // Holding the writer: no write transaction of this process is open on the copy. Disposing it closes its
            // sessions, and its writer connection closes as the writer is released, so a cached store reference can no
            // longer write the file.
            _ = await store.WithWritersHeldAsync(() =>
            {
                store.Dispose();
                return Task.FromResult(true);
            }, drain.Token, "a rebuild draining a faulted copy").ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Not drained in time: the copy stays open, in place, untouched.
            _stores[key] = store;
            return false;
        }
    }

    // The main file first, then its log: a failure leaves the main file in place, or moves back what had moved.
    private static void MoveAside(string path, string sidePath)
    {
        RejectLink(path);
        File.Move(path, sidePath, overwrite: false);
        List<string> moved = [];
        try
        {
            foreach (var sidecar in Sidecars.Where(sidecar => File.Exists(path + sidecar)))
            {
                RejectLink(path + sidecar);
                File.Move(path + sidecar, sidePath + sidecar, overwrite: false);
                moved.Add(sidecar);
            }
        }
        catch
        {
            foreach (var sidecar in moved)
            {
                File.Move(sidePath + sidecar, path + sidecar, overwrite: false);
            }

            File.Move(sidePath, path, overwrite: false);
            throw;
        }
    }

    // Worker-only: the next rows of a private working copy with the bytes of their stored states and documents, read
    // without reading either.
    private static List<SideRow> MeasureSide(SqliteConnection connection, string afterCollection, string afterId, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        if (afterCollection.Length == 0 && afterId.Length == 0)
        {
            VerifySideCoverage(connection, cancellationToken);
        }

        List<(string Collection, string Id, long State)> keys = [];
        using (var page = connection.CreateCommand())
        {
            page.CommandText = """
                SELECT collection, id, octet_length(state),
                    typeof(collection) = 'text' AND typeof(id) = 'text'
                    AND octet_length(collection) BETWEEN 1 AND 4096 AND octet_length(id) BETWEEN 1 AND 4096
                    AND length(collection) <= 128 AND length(id) <= 1024
                FROM _tb_state WHERE collection > $collection OR (collection = $collection AND id > $id)
                ORDER BY collection, id LIMIT $limit;
                """;
            page.Parameters.AddWithValue("$collection", afterCollection);
            page.Parameters.AddWithValue("$id", afterId);
            page.Parameters.AddWithValue("$limit", (long)maxRows);
            using var reader = page.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetInt64(3) == 0)
                {
                    throw new InvalidDataException("a side-copy key exceeds its bounded representation");
                }

                keys.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        List<SideRow> rows = [];
        var bytes = 0L;
        var documents = DocumentBytes(connection, keys.Select(key => (key.Collection, key.Id)), cancellationToken);
        foreach (var (collection, id, state) in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = Encoding.UTF8.GetByteCount(collection) + Encoding.UTF8.GetByteCount(id) + state + documents[(collection, id)];
            if (rows.Count > 0 && bytes + size > maxBytes)
            {
                break;
            }

            rows.Add(new SideRow(collection, id, size));
            bytes += size;
        }

        return rows;
    }

    // Missing state cannot be reconstructed from its body; retain the side copy rather than certify its omission.
    private static void VerifySideCoverage(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var after = string.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string collection;
            using (var next = connection.CreateCommand())
            {
                next.CommandText = """
                    WITH names(name) AS (
                        SELECT name FROM (SELECT name FROM collections WHERE name > $after ORDER BY name LIMIT 1)
                        UNION SELECT '_collections' UNION SELECT '_indexes' UNION SELECT '_unique'
                    )
                    SELECT name, typeof(name) = 'text' AND octet_length(name) BETWEEN 1 AND 4096
                    FROM names WHERE name > $after ORDER BY name LIMIT 1;
                    """;
                next.Parameters.AddWithValue("$after", after);
                using var reader = next.ExecuteReader();
                if (!reader.Read())
                {
                    break;
                }

                if (reader.GetInt64(1) != 1)
                {
                    throw new InvalidDataException("a side-copy collection has an invalid name");
                }

                collection = reader.GetString(0);
                if (!DatabaseName.TryParse(collection, out _) && !TursoStorageTransaction.IsMetadata(collection))
                {
                    throw new InvalidDataException("a side-copy collection has an invalid name");
                }
            }

            using var orphan = connection.CreateCommand();
            orphan.CommandText = $"""
                SELECT 1 FROM {TursoStorageTransaction.Table(collection)} AS document
                WHERE NOT EXISTS (
                    SELECT 1 FROM _tb_state AS state WHERE state.collection = $collection AND state.id = document.id
                ) LIMIT 1;
                """;
            orphan.Parameters.AddWithValue("$collection", collection);
            if (orphan.ExecuteScalar() is not null)
            {
                throw new InvalidDataException("a side-copy document has no replicated state");
            }

            after = collection;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var conflict = connection.CreateCommand();
        conflict.CommandText = """
            SELECT 1 FROM _tb_unique_conflicts AS document
            WHERE NOT EXISTS (
                SELECT 1 FROM _tb_state AS state WHERE state.collection = document.collection AND state.id = document.id
            ) LIMIT 1;
            """;
        if (conflict.ExecuteScalar() is not null)
        {
            throw new InvalidDataException("a side-copy uniqueness loser has no replicated state");
        }
    }

    // Worker-only: the measured rows' states that verify against their stored digests, and how many did not (the class doc
    // of ReplicaRebuild: only verified states join the rebuild).
    private static (List<RepairState> States, int Skipped) ReadSide(SqliteConnection connection, IReadOnlyList<SideRow> rows, CancellationToken cancellationToken)
    {
        using var snapshot = connection.BeginTransaction(deferred: true);
        List<RepairState> states = [];
        var skipped = 0;
        var documents = DocumentBytes(connection, rows.Select(row => (row.Collection, row.Id)), cancellationToken);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyBytes = Encoding.UTF8.GetByteCount(row.Collection) + Encoding.UTF8.GetByteCount(row.Id);
            var documentBytes = documents[(row.Collection, row.Id)];
            // Hash affinity permits BLOBs: reject them before any getter can allocate their unmeasured payload.
            using (var preflight = connection.CreateCommand())
            {
                preflight.CommandText = """
                    SELECT octet_length(state), typeof(state) = 'text' AND typeof(h1) = 'integer' AND typeof(h2) = 'integer'
                    FROM _tb_state WHERE collection = $collection AND id = $id;
                    """;
                preflight.Parameters.AddWithValue("$collection", row.Collection);
                preflight.Parameters.AddWithValue("$id", row.Id);
                using var measured = preflight.ExecuteReader();
                if (!measured.Read() || measured.IsDBNull(0) || measured.GetInt64(0) <= 0
                    || checked(keyBytes + documentBytes + measured.GetInt64(0)) != row.Bytes)
                {
                    throw new InvalidDataException("a side-copy state no longer matches its measured size");
                }

                if (measured.GetInt64(1) != 1)
                {
                    skipped++;
                    continue;
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state, h1, h2 FROM _tb_state WHERE collection = $collection AND id = $id;";
            command.Parameters.AddWithValue("$collection", row.Collection);
            command.Parameters.AddWithValue("$id", row.Id);
            string stored;
            long h1, h2;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    throw new InvalidDataException("a measured state of the side copy is gone");
                }

                (stored, h1, h2) = (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
            }

            DocumentState state;
            try
            {
                state = DocumentStateEncoding.DecodeStored(Base64Url.DecodeFromChars(stored), Document(connection, row.Collection, row.Id));
            }
            catch (Exception error) when (error is FormatException or InvalidDataException or JsonException)
            {
                skipped++;
                continue;
            }

            var digest = DocumentStateEncoding.Digest(row.Collection, row.Id, state);
            if (unchecked((long)(ulong)(digest >> 64)) != h1 || unchecked((long)(ulong)digest) != h2)
            {
                skipped++;
                continue;
            }

            states.Add(new RepairState(row.Collection, row.Id, state));
        }

        return (states, skipped);
    }

    // An unpooled connection on a private working copy (never on the retained side copy).
    private static SqliteConnection Working(string workingPath, string connectionString, SalvageOperation operation)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            TursoSession.OpenExisting(connection, workingPath);
            return connection;
        }
        catch
        {
            try
            {
                connection.Dispose();
            }
            catch
            {
                operation.CloseFailed = true;
                throw;
            }
            throw;
        }
    }

    // Measure a bounded page without reading bodies or preparing two queries for every key.
    private static Dictionary<(string Collection, string Id), long> DocumentBytes(
        SqliteConnection connection, IEnumerable<(string Collection, string Id)> keys, CancellationToken cancellationToken)
    {
        Dictionary<(string Collection, string Id), long> lengths = [];
        foreach (var collection in keys.GroupBy(key => key.Collection, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = $table;";
            command.Parameters.AddWithValue("$table", collection.Key);
            var table = command.ExecuteScalar() is not null;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$collection", collection.Key);
            var values = string.Join(",", collection.Select((key, index) =>
            {
                var parameter = "$id" + index;
                command.Parameters.AddWithValue(parameter, key.Id);
                return "(" + parameter + ")";
            }));
            var loser = "(SELECT octet_length(body) FROM _tb_unique_conflicts WHERE collection = $collection AND id = ids.id)";
            var length = table
                ? $"coalesce({loser}, (SELECT octet_length(doc) FROM {TursoStorageTransaction.Table(collection.Key)} WHERE id = ids.id))"
                : loser;
            command.CommandText = $"WITH ids(id) AS (VALUES {values}) SELECT ids.id, {length} FROM ids;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                lengths[(collection.Key, reader.GetString(0))] = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            }
        }

        return lengths;
    }

    // The document a stored state was written with: a hidden uniqueness loser's body, else its collection row.
    private static byte[] Document(SqliteConnection connection, string collection, string id) =>
        DocumentQuery(connection, collection, id, "body", "doc") is string document ? Encoding.UTF8.GetBytes(document) : [];

    private static object? DocumentQuery(SqliteConnection connection, string collection, string id, string loser, string row)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = $table;";
        command.Parameters.AddWithValue("$table", collection);
        var table = command.ExecuteScalar() is not null;
        command.Parameters.Clear();
        command.CommandText = table
            ? $"SELECT coalesce((SELECT {loser} FROM _tb_unique_conflicts WHERE collection = $collection AND id = $id), (SELECT {row} FROM {TursoStorageTransaction.Table(collection)} WHERE id = $id));"
            : $"SELECT {loser} FROM _tb_unique_conflicts WHERE collection = $collection AND id = $id;";
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar();
    }
}
