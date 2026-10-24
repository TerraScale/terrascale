using System.Runtime.InteropServices;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Healthy rewrites (TS-873, <see cref="IHealthyRewrite"/>). The file transition keeps one complete lineage on disk at every
/// step: the old file's log is checkpointed into it, synced and removed (directory synced) before the synced private copy
/// is renamed over it (directory synced again), so a crash finds the whole old file or the whole new one, never the old log
/// beside the new file. Private copies (<c>.&lt;id&gt;.&lt;nonce&gt;.rewriting</c>) left by a crash are deleted at start.
/// </summary>
internal sealed partial class TursoStorageEngine : IHealthyRewrite
{
    private const string RewriteSuffix = "rewriting";

    private static readonly TenantContext SystemCheckpoint = new(SystemName, PlanTier.Hobby);

    /// <summary>Durably records a directory's entries; integration tests replace it to inject sync failures.</summary>
    internal Action<string> DirectorySync { get; set; } = SyncDirectory;

    public async ValueTask<HealthyRewrite> RewriteAsync(TenantContext? database, long minimumOffset, CancellationToken cancellationToken)
    {
        // Opened outside the creation lock, which opening takes.
        var store = (TursoDatabaseStore?)(database is null
            ? await OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)
            : await OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false))
            ?? throw new RewriteRefusedException("the database does not exist");
        var (key, path) = database is null ? ((Organization: SystemName, Project: SystemName, Database: SystemName), LocateSystem()) : Locate(database);
        var step = database ?? SystemCheckpoint;
        // Lifecycle operations (create, copy, delete, rebuild swaps, salvage) and uncached opens wait for the whole rewrite.
        // shortcut: one engine-wide lock; reserve the database alone if rewrite latency stalls other lifecycle work.
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RejectDatabaseLinks(path);
            if (database is null ? !ReferenceEquals(_system, store)
                : !_stores.TryGetValue(key, out var open) || !ReferenceEquals(open, store) || _initializing.ContainsKey(key) || _pendingIntents.Contains(key))
            {
                throw new RewriteRefusedException("the database is not open, or a lifecycle operation owns it");
            }

            if (database is not null)
            {
                await ThrowIfPurgeFencedAsync(key.Database, cancellationToken).ConfigureAwait(false);
            }

            lock (_salvageGate)
            {
                if (_salvageOperations.ContainsKey(path))
                {
                    throw new RewriteRefusedException("a salvage operation reads beside the database");
                }
            }

            using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drain.CancelAfter(DrainTimeout);
            await using var quiesced = (await store.QuiesceAsync(drain.Token).ConfigureAwait(false)).ConfigureAwait(false);
            var source = store.Incarnation;
            var scratch = Path.Combine(Path.GetDirectoryName(path)!, $".{key.Database}.{EntityIds.New()}.{RewriteSuffix}");
            RejectDatabaseLinks(scratch);
            var renamed = false;
            try
            {
                var offset = await store.WriteRewriteAsync(scratch, cancellationToken).ConfigureAwait(false);
                if (offset < minimumOffset)
                {
                    throw new RewriteRefusedException("the copied cut precedes the requested commit offset");
                }

                // Retain the key in place: a crash's old or new file must open with the same durable sidecar.
                // Unlike a separate database copy, a healthy rewrite is not a data-key rotation.
                var connectionString = new global::Turso.Data.Sqlite.SqliteConnectionStringBuilder(
                    await store.ConnectionStringAsync(Io, cancellationToken).ConfigureAwait(false)) { DataSource = scratch }.ToString();
                var target = await TursoDatabaseStore.InitializeAsync(key.Database, scratch, Io, _pragmas, connectionString, cancellationToken).ConfigureAwait(false);
                try
                {
                    await Io.RunAsync(() =>
                    {
                        Replace(path, scratch, step, ref renamed);
                        return true;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (renamed)
                {
                    // A power loss may still bring the old file back: nothing may write or be acknowledged on the new one
                    // until a restart reads what is durable. Before the quiesced store resumes.
                    store.Rewritten(target);
                    Ledger.FailClosed("a healthy rewrite's rename may not be durable");
                    throw new RewriteUncertainException(error);
                }

                // Only a durable healthy rewrite, and before the new incarnation is published: a request that pinned the source
                // never sees the target in place without the chain back to its pin.
                store.RewroteHealthily(source, target);
                store.Rewritten(target);
                return new HealthyRewrite(source, target, offset);
            }
            catch (Exception error) when (error is not (RewriteRefusedException or RewriteUncertainException or OperationCanceledException))
            {
                // A copy, sync or file step failed before the rename: the old file is whole and serves again.
                throw new RewriteRefusedException("the rewrite failed; the old file stays in place", error);
            }
            finally
            {
                if (!renamed)
                {
                    await Io.RunAsync(() =>
                    {
                        DeleteFiles(scratch);
                        return true;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ExitCreation();
        }
    }

    // Worker-only, with the store quiesced and its log checkpointed. Each step leaves one complete lineage.
    private void Replace(string path, string scratch, TenantContext step, ref bool renamed)
    {
        var directory = Path.GetDirectoryName(path)!;
        RejectDatabaseLinks(path);
        RejectDatabaseLinks(scratch);
        foreach (var file in new[] { path, scratch })
        {
            foreach (var sidecar in Sidecars.Where(sidecar => sidecar != DatabaseKeys.Suffix))
            {
                // A log or journal still holding frames belongs to its file: never removed.
                if (new FileInfo(file + sidecar) is { Exists: true } log && log.Length != 0 && sidecar != "-shm")
                {
                    throw new RewriteRefusedException("a database log still holds frames");
                }
            }
        }

        DeleteSidecars(path, keepKey: true);
        _checkpoint("rewrite.old_log_removed", step);
        DirectorySync(directory);
        DeleteSidecars(scratch, keepKey: true);
        _checkpoint("rewrite.replacing", step);
        File.Move(scratch, path, overwrite: true);
        renamed = true;
        _checkpoint("rewrite.replaced", step);
        // Never retried: after a failed sync Linux may report a later one clean without the lost entries (fsyncgate).
        DirectorySync(directory);
    }

    // Recognized private rewrite copies of SYSTEM and of every tenant database a crash left; the files in place are whole.
    private void DeleteRewriteScratch()
    {
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var file in Directory.EnumerateFiles(_dataDirectory, $".{SystemName}.*.{RewriteSuffix}*", options))
        {
            if (RewriteScratchOf(Path.GetFileName(file)) == SystemName)
            {
                DeleteFileIfPresent(file);
            }
        }

        foreach (var organization in Directory.EnumerateDirectories(_dataDirectory, "*", options).Where(directory => IsId(Path.GetFileName(directory))))
        {
            foreach (var project in Directory.EnumerateDirectories(organization, "*", options).Where(directory => IsId(Path.GetFileName(directory))))
            {
                foreach (var file in Directory.EnumerateFiles(project, $".*.*.{RewriteSuffix}*", options))
                {
                    if (RewriteScratchOf(Path.GetFileName(file)) is { } id && IsId(id))
                    {
                        DeleteFileIfPresent(file);
                    }
                }
            }
        }
    }

    // The database id of a private rewrite copy or one of its sidecars, or null.
    private static string? RewriteScratchOf(string name)
    {
        foreach (var sidecar in Sidecars)
        {
            if (name.EndsWith(sidecar, StringComparison.Ordinal))
            {
                name = name[..^sidecar.Length];
                break;
            }
        }

        return name.Split('.') is ["", var id, var nonce, RewriteSuffix] && EntityIds.IsValid(nonce) ? id : null;
    }

    // Durably records the directory's entries (a removal or a rename): .NET opens no directory handle.
    private static void SyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            // shortcut: Linux only, where TerraBase runs; add the platform's directory flush before serving elsewhere.
            throw new PlatformNotSupportedException("a healthy rewrite syncs directories on Linux only");
        }

        var descriptor = Native.Open(directory, 0);
        if (descriptor < 0)
        {
            throw new IOException($"opening a database directory to sync it failed (errno {Marshal.GetLastPInvokeError()})");
        }

        try
        {
            if (Native.Fsync(descriptor) != 0)
            {
                throw new IOException($"syncing a database directory failed (errno {Marshal.GetLastPInvokeError()})");
            }
        }
        finally
        {
            _ = Native.Close(descriptor);
        }
    }

    private static class Native
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int Fsync(int descriptor);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int Close(int descriptor);
    }
}
