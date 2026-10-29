using System.Runtime.InteropServices;
using ZLogger;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IHealthyRewrite
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);
    private const string RewriteSuffix = "rewriting";
    internal static readonly TimeSpan RewriteFinalBudget = TimeSpan.FromMilliseconds(750);

    private static readonly TenantContext SystemCheckpoint = new(SystemName, PlanTier.Hobby);

    internal Action<string> DirectorySync { get; set; } = SyncDirectory;

    public async ValueTask<HealthyRewrite> RewriteAsync(TenantContext? database, long minimumOffset, CancellationToken cancellationToken)
    {

        using var openingDeadline = new CancellationTokenSource(DrainTimeout, TimeProvider.System);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token, openingDeadline.Token);
        TursoDatabaseStore store;
        try
        {
            store = (TursoDatabaseStore?)(database is null
                ? await OpenSystemDatabaseAsync(opening.Token).ConfigureAwait(false)
                : await OpenDatabaseAsync(database, opening.Token).ConfigureAwait(false))
                ?? throw new RewriteRefusedException("the database does not exist");
        }
        catch (OperationCanceledException error) when (openingDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_stopping.IsCancellationRequested)
        {
            throw new RewriteRefusedException("the rewrite could not open the database in time", error, RewriteRefusal.Deadline);
        }
        var (key, path) = database is null ? ((Organization: SystemName, Project: SystemName, Database: SystemName), LocateSystem()) : Locate(database);
        var step = database ?? SystemCheckpoint;
        Io.Admit();
        try
        {
            return await Io.WithAdmissionAsync<HealthyRewrite>(async () =>
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
                cancellationToken = lifetime.Token;
                using var budget = new RewriteBudget(this, DiskBudget.FileBytes(path), cancellationToken);
                using var heldBudget = new CancellationTokenSource(Timeout.InfiniteTimeSpan, TimeProvider.System);
                using var final = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, heldBudget.Token);
                var ownsRewrite = false;
                var ownsCreation = false;
                var scratch = Path.Combine(Path.GetDirectoryName(path)!, $".{key.Database}.{EntityIds.New()}.{RewriteSuffix}");
                var renamed = false;
                try
                {
                    if (!store.TryBeginRewrite())
                    {
                        throw new RewriteRefusedException("another healthy rewrite owns this database");
                    }

                    ownsRewrite = true;
                    using var disk = Disk.Reserve(DiskBudget.WorkspaceBytes(DiskBudget.FileBytes(path)));
                    budget.Token.ThrowIfCancellationRequested();
                    RejectDatabaseLinks(scratch);
                    var source = store.Incarnation;
                    var lineage = store.Lineage;

                    var uncaptured = await store.WithWriterAsync(_ => store.UncapturedCommits, "a rewrite's capture setup", budget.Token).ConfigureAwait(false);
                    long offset;
                    TursoChangeTap replay;
                    Ulid target;
                    using (StorageWork.Bulk())
                    {
                        (offset, replay, target) = await store.WriteRewriteAsync(scratch, minimumOffset, null, default, finalPass: false, budget.Token).ConfigureAwait(false);
                    }
                    if (store.UncapturedCommits != uncaptured)
                    {
                        throw new RewriteRefusedException($"an uncaptured transaction ({store.UncapturedBy}) committed during the rewrite");
                    }

                    using var lifecycle = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    lifecycle.CancelAfter(DrainTimeout);
                    try
                    {
                        await EnterCreationAsync(lifecycle.Token, admitted: true).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new RewriteRefusedException("the rewrite could not acquire the lifecycle lock in time", error);
                    }
                    ownsCreation = true;
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    RejectDatabaseLinks(path);
                    if (store.IsClosed || store.Incarnation != source || store.Lineage != lineage ||
                        (database is null ? !ReferenceEquals(_system, store)
                        : !_stores.TryGetValue(key, out var open) || !ReferenceEquals(open, store) || _initializing.ContainsKey(key) || _pendingIntents.Contains(key)))
                    {
                        throw new RewriteRefusedException("the copied database or its lineage changed");
                    }

                    if (database is not null)
                    {
                        await ThrowIfPurgeFencedAsync(key.Database, budget.Token).ConfigureAwait(false);
                    }

                    await using var quiesced = (await store.QuiesceAsync(async token =>
                    {
                        await store.WriteRewriteAsync(scratch, minimumOffset, replay, target, finalPass: false, token).ConfigureAwait(false);
                    }, async token =>
                    {
                        await store.WriteRewriteAsync(scratch, minimumOffset, replay, target, finalPass: false, token).ConfigureAwait(false);
                        if (store.UncapturedCommits != uncaptured)
                        {
                            throw new RewriteRefusedException($"an uncaptured transaction ({store.UncapturedBy}) committed during the rewrite");
                        }
                    }, () =>
                    {
                        budget.StopPressure();
                        heldBudget.CancelAfter(RewriteFinalBudget);
                    }, final.Token, budget.Token).ConfigureAwait(false)).ConfigureAwait(false);

                    if (store.UncapturedCommits != uncaptured)
                    {
                        throw new RewriteRefusedException($"an uncaptured transaction ({store.UncapturedBy}) committed during the rewrite");
                    }
                    (offset, _, _) = await store.WriteRewriteAsync(scratch, minimumOffset, replay, target, finalPass: true, final.Token).ConfigureAwait(false);
                    try
                    {
                        await store.WriteLane.RunAsync(() =>
                        {
                            Replace(path, scratch, step, store, final.Token, ref renamed);
                            return true;
                        }, final.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (renamed)
                    {
                        store.Rewritten(target);
                        Ledger.FailClosed("a healthy rewrite's rename may not be durable");
                        throw new RewriteUncertainException(error);
                    }

                    store.RewroteHealthily(source, target);
                    store.Rewritten(target);
                    return new HealthyRewrite(source, target, offset);
                }
                catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && heldBudget.IsCancellationRequested)
                {
                    throw new RewriteRefusedException("the final rewrite delta exceeded its time budget", error, RewriteRefusal.Budget);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && budget.Expired && error is OperationCanceledException or RewriteRefusedException)
                {
                    throw new RewriteRefusedException(budget.Reason == RewriteRefusal.Pressure
                        ? "the off-writer rewrite stopped under storage or memory pressure"
                        : "the off-writer rewrite exceeded its total deadline", error, budget.Reason);
                }
                catch (Exception error) when (error is not (RewriteRefusedException or RewriteUncertainException or OperationCanceledException))
                {
                    throw new RewriteRefusedException("the rewrite failed; the old file stays in place", error);
                }
                finally
                {
                    try
                    {
                        if (ownsCreation)
                        {
                            ExitCreation(admitted: true);
                        }
                        if (!renamed)
                        {
                            await Io.RunAsync(() =>
                            {
                                DeleteFiles(scratch);
                                return true;
                            }, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        if (ownsRewrite)
                        {
                            store.EndRewrite();
                        }
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (RewriteRefusedException error)
        {
            _rewriteRefused.Add(1, new KeyValuePair<string, object?>("reason", error.Reason.ToString()));
            _logger.ZLogInformation($"Healthy rewrite refused: {error.Reason} ({error.Message})");
            throw;
        }
        finally
        {
            Io.Release();
        }
    }

    private void Replace(string path, string scratch, TenantContext step, TursoDatabaseStore store, CancellationToken token, ref bool renamed)
    {
        var directory = Path.GetDirectoryName(path)!;
        RejectDatabaseLinks(path);
        RejectDatabaseLinks(scratch);
        foreach (var file in new[] { path, scratch })
        {
            foreach (var sidecar in Sidecars.Where(sidecar => sidecar != DatabaseKeys.Suffix))
            {

                if (new FileInfo(file + sidecar) is { Exists: true } log && log.Length != 0 && sidecar != "-shm")
                {
                    throw new RewriteRefusedException("a database log still holds frames");
                }
            }
        }

        token.ThrowIfCancellationRequested();
        DeleteSidecars(path, keepKey: true);
        _checkpoint("rewrite.old_log_removed", step);
        using (store.MeasureRewrite("directory_fsync_before"))
        {
            DirectorySync(directory);
        }
        DeleteSidecars(scratch, keepKey: true);
        _checkpoint("rewrite.replacing", step);
        token.ThrowIfCancellationRequested();
        using (store.MeasureRewrite("rename"))
        {
            File.Move(scratch, path, overwrite: true);
        }
        renamed = true;
        _checkpoint("rewrite.replaced", step);

        using (store.MeasureRewrite("directory_fsync_after"))
        {
            DirectorySync(directory);
        }
    }

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

    private static void SyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {

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
