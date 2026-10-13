using System.Buffers.Text;
using System.Text;
using Turso.Data.Sqlite;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// The storage half of rebuilding a faulted copy into a new incarnation (docs/terrabase/decisions/0013a, R7): the swap
/// that drains the open copy and moves its file and log aside unwritten, the fresh file in its place, and the verified
/// read of the side copy's states for the salvage. <see cref="ReplicaRebuild"/> decides the order.
/// </summary>
internal sealed partial class SqliteStorageEngine : IRebuildStorage
{
    // How long a swap waits for the open copy's writer before it refuses and leaves the copy in place.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    public string SidePath(TenantContext database, Ulid incarnation)
    {
        var (key, path) = Locate(database);
        return Path.Combine(Path.GetDirectoryName(path)!, $".{key.Database}.{incarnation}.rebuild");
    }

    public bool SideExists(string sidePath) => File.Exists(sidePath);

    public async ValueTask<Ulid?> IncarnationAsync(TenantContext database, CancellationToken cancellationToken) =>
        await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is SqliteDatabaseStore store ? store.Incarnation : null;

    public async ValueTask<Ulid?> SwapAsync(TenantContext database, Ulid faultedIncarnation, string sidePath, CancellationToken cancellationToken)
    {
        // Opened first, so the check below reads the file's own identity.
        if (await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not SqliteDatabaseStore)
        {
            return null;
        }

        var (key, path) = Locate(database);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RejectDatabaseLinks(sidePath);
            if (!_stores.TryGetValue(key, out var store) || store.Incarnation != faultedIncarnation || _initializing.ContainsKey(key)
                || _pendingIntents.Contains(key) || File.Exists(sidePath))
            {
                return null;
            }

            // Retired before the drain, under the creation lock: nothing opens the old path again until the swap ends.
            _ = _stores.TryRemove(key, out _);
            using (var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                drain.CancelAfter(DrainTimeout);
                try
                {
                    // Holding the writer: no write transaction of this process is open on the copy. Disposing it closes its
                    // sessions, and its writer connection closes as the writer is released, so a cached store reference can
                    // no longer write the old file.
                    _ = await store.WithWritersHeldAsync(() =>
                    {
                        store.Dispose();
                        return Task.FromResult(true);
                    }, drain.Token, "a rebuild draining a faulted copy").ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Not drained in time: the copy stays open, in place, untouched.
                    _stores[key] = store;
                    return null;
                }
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

            await PublishAsync(key.Database, path, temporary => SqliteDatabaseStore.InitializeAsync(key.Database, temporary, Io, _pragmas, cancellationToken))
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
        var (key, path) = Locate(database);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!File.Exists(path))
            {
                // A swap stopped between the main file and its log: the log follows its file first, or the fresh file would
                // open beside another file's log.
                _ = await Io.RunAsync(() =>
                {
                    foreach (var sidecar in Sidecars.Where(sidecar => File.Exists(path + sidecar)))
                    {
                        RejectLink(path + sidecar);
                        File.Move(path + sidecar, sidePath + sidecar, overwrite: false);
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
        Io.Admit();
        try
        {
            return await Io.RunAsync(() =>
            {
                // The retained side copy is only ever read by these copies, never opened: an open could recover or index
                // its log in place.
                DeleteFiles(working);
                RejectLink(sidePath);
                File.Copy(sidePath, working, overwrite: false);
                foreach (var sidecar in Sidecars.Where(sidecar => File.Exists(sidePath + sidecar)))
                {
                    RejectLink(sidePath + sidecar);
                    File.Copy(sidePath + sidecar, working + sidecar, overwrite: false);
                }

                return working;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }
    }

    public async ValueTask<IReadOnlyList<SideRow>> MeasureSideAsync(
        string workingPath, string afterCollection, string afterId, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        Io.Admit();
        try
        {
            return await Io.RunAsync(() => MeasureSide(workingPath, afterCollection, afterId, maxRows, maxBytes, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }
    }

    public async ValueTask<IReadOnlyList<RepairState>> ReadSideAsync(string workingPath, IReadOnlyList<SideRow> rows, CancellationToken cancellationToken)
    {
        Io.Admit();
        try
        {
            return await Io.RunAsync(() => ReadSide(workingPath, rows, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }
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
        if (await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not SqliteDatabaseStore)
        {
            return false;
        }

        var (key, _) = Locate(database);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Under the creation lock nothing replaces or removes the rebuilt file between this check and the deletion.
            if (!_stores.TryGetValue(key, out var store) || store.Incarnation != rebuiltIncarnation)
            {
                return false;
            }

            _ = await Io.RunAsync(() =>
            {
                DeleteFiles(sidePath + ".salvage");
                DeleteFiles(sidePath);
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        finally
        {
            ExitCreation();
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
    private static List<SideRow> MeasureSide(string workingPath, string afterCollection, string afterId, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        using var connection = Working(workingPath);
        List<(string Collection, string Id, long State)> keys = [];
        using (var page = connection.CreateCommand())
        {
            page.CommandText = """
                SELECT collection, id, octet_length(state) FROM _tb_state WHERE collection > $collection OR (collection = $collection AND id > $id)
                ORDER BY collection, id LIMIT $limit;
                """;
            page.Parameters.AddWithValue("$collection", afterCollection);
            page.Parameters.AddWithValue("$id", afterId);
            page.Parameters.AddWithValue("$limit", (long)maxRows);
            using var reader = page.ExecuteReader();
            while (reader.Read())
            {
                keys.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        List<SideRow> rows = [];
        var bytes = 0L;
        foreach (var (collection, id, state) in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = Encoding.UTF8.GetByteCount(collection) + Encoding.UTF8.GetByteCount(id) + state + DocumentBytes(connection, collection, id);
            if (rows.Count > 0 && bytes + size > maxBytes)
            {
                break;
            }

            rows.Add(new SideRow(collection, id, size));
            bytes += size;
        }

        return rows;
    }

    // Worker-only: the measured rows' states, each checked against its stored digest.
    private static List<RepairState> ReadSide(string workingPath, IReadOnlyList<SideRow> rows, CancellationToken cancellationToken)
    {
        using var connection = Working(workingPath);
        List<RepairState> states = [];
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            catch (FormatException error)
            {
                throw new InvalidDataException("a state of the side copy does not decode", error);
            }

            var digest = DocumentStateEncoding.Digest(row.Collection, row.Id, state);
            if (unchecked((long)(ulong)(digest >> 64)) != h1 || unchecked((long)(ulong)digest) != h2)
            {
                throw new InvalidDataException("a state of the side copy does not match its stored digest");
            }

            states.Add(new RepairState(row.Collection, row.Id, state));
        }

        return states;
    }

    // An unpooled connection on a private working copy (never on the retained side copy).
    private static SqliteConnection Working(string workingPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = workingPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // The bytes of the document a stored state was written with, without reading it.
    private static long DocumentBytes(SqliteConnection connection, string collection, string id) =>
        DocumentQuery(connection, collection, id, "octet_length(body)", "octet_length(doc)") is long bytes ? bytes : 0;

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
            ? $"SELECT coalesce((SELECT {loser} FROM _tb_unique_conflicts WHERE collection = $collection AND id = $id), (SELECT {row} FROM {SqliteStorageTransaction.Table(collection)} WHERE id = $id));"
            : $"SELECT {loser} FROM _tb_unique_conflicts WHERE collection = $collection AND id = $id;";
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar();
    }
}
