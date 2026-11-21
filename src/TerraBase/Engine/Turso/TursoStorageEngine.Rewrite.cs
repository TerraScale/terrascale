using ZLogger;
using TerraBase.Engine.Revisions;
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

    public async ValueTask<HealthyRewriteOutcome> RewriteAsync(TenantContext? database, long minimumOffset, CancellationToken cancellationToken)
    {

        using var openingDeadline = new CancellationTokenSource(DrainTimeout, TimeProvider.System);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token, openingDeadline.Token);
        TursoDatabaseStore? opened;
        try
        {
            opened = (TursoDatabaseStore?)(database is null
                ? await OpenSystemDatabaseAsync(opening.Token).ConfigureAwait(false)
                : await OpenDatabaseAsync(database, opening.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (openingDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_stopping.IsCancellationRequested)
        {
            return new RewriteRefused("the rewrite could not open the database in time", new Deadline());
        }
        if (opened is null)
        {
            return new RewriteRefused("the database does not exist", new Safety());
        }

        var store = opened;
        var (key, path) = database is null ? ((Organization: SystemName, Project: SystemName, Database: SystemName), LocateSystem()) : Locate(database);
        var step = database ?? SystemCheckpoint;
        Io.Admit();
        HealthyRewriteOutcome outcome;
        try
        {
            outcome = await Io.WithAdmissionAsync<HealthyRewriteOutcome>(async () =>
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token, store.Refused, Ledger.Refused);
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
                    switch (store.TryBeginRewrite())
                    {
                        case TursoDatabaseStore.RewriteHeld:
                            return BudgetRefused(budget, cancellationToken) ?? new RewriteRefused("another healthy rewrite owns this database", new Safety());
                        case RewriteRefused refused:
                            return BudgetRefused(budget, cancellationToken) ?? refused;
                        case TursoDatabaseStore.RewriteLease:
                            break;
                    }

                    ownsRewrite = true;
                    using var disk = Disk.Reserve(DiskBudget.WorkspaceBytes(DiskBudget.FileBytes(path)));
                    budget.Token.ThrowIfCancellationRequested();
                    RejectDatabaseLinks(scratch);
                    var source = store.Incarnation;
                    var lineage = store.Lineage;

                    var uncaptured = await store.WithWriterAsync(_ => store.UncapturedCommits, "a rewrite's capture setup", budget.Token).ConfigureAwait(false);
                    TursoChangeTap? replay = null;
                    Ulid target = default;
                    long offset = 0;
                    using (StorageWork.Bulk())
                    {
                        switch (await store.WriteRewriteAsync(scratch, minimumOffset, null, default, finalPass: false, budget.Token).ConfigureAwait(false))
                        {
                            case TursoDatabaseStore.RewriteWrite initial:
                                (offset, replay, target) = (initial.Offset, initial.Replay, initial.Incarnation);
                                break;
                            case RewriteRefused refused:
                                return BudgetRefused(budget, cancellationToken) ?? refused;
                        }
                    }
                    if (store.UncapturedCommits != uncaptured)
                    {
                        return BudgetRefused(budget, cancellationToken) ?? new RewriteRefused($"an uncaptured transaction ({store.UncapturedBy}) committed during the rewrite", new Safety());
                    }

                    using var lifecycle = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    lifecycle.CancelAfter(DrainTimeout);
                    try
                    {
                        await EnterCreationAsync(lifecycle.Token, admitted: true).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return BudgetRefused(budget, cancellationToken) ?? new RewriteRefused("the rewrite could not acquire the lifecycle lock in time", new Safety());
                    }
                    ownsCreation = true;
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    RejectDatabaseLinks(path);
                    if (store.IsClosed || store.Incarnation != source || store.Lineage != lineage ||
                        (database is null ? !ReferenceEquals(_system, store)
                        : !_stores.TryGetValue(key, out var open) || !ReferenceEquals(open, store) || _initializing.ContainsKey(key) || _pendingIntents.Contains(key)))
                    {
                        return BudgetRefused(budget, cancellationToken) ?? new RewriteRefused("the copied database or its lineage changed", new Safety());
                    }

                    if (database is not null)
                    {
                        await ThrowIfPurgeFencedAsync(key.Database, budget.Token).ConfigureAwait(false);
                    }

                    TursoDatabaseStore.Quiesced? held = null;
                    try
                    {
                        switch (await store.QuiesceAsync(async token =>
                        {
                            return await store.WriteRewriteAsync(scratch, minimumOffset, replay, target, finalPass: false, token).ConfigureAwait(false) switch
                            {
                                TursoDatabaseStore.RewriteWrite => null,
                                RewriteRefused refused => BudgetRefused(budget, cancellationToken) ?? refused,
                            };
                        }, async token =>
                        {
                            switch (await store.WriteRewriteAsync(scratch, minimumOffset, replay, target, finalPass: false, token).ConfigureAwait(false))
                            {
                                case TursoDatabaseStore.RewriteWrite:
                                    break;
                                case RewriteRefused refused:
                                    return BudgetRefused(budget, cancellationToken) ?? refused;
                            }

                            if (store.UncapturedCommits != uncaptured)
                            {
                                return BudgetRefused(budget, cancellationToken) ?? new RewriteRefused($"an uncaptured transaction ({store.UncapturedBy}) committed during the rewrite", new Safety());
                            }

                            return null;
                        }, () =>
                        {
                            budget.StopPressure();
                            heldBudget.CancelAfter(RewriteFinalBudget);
                        }, final.Token, budget.Token).ConfigureAwait(false))
                        {
                            case TursoDatabaseStore.Quiesced quiesced:
                                held = quiesced;
                                break;
                            case RewriteRefused refused:
                                return BudgetRefused(budget, cancellationToken) ?? refused;
                        }

                        if (store.UncapturedCommits != uncaptured)
                        {
                            return BudgetRefused(budget, cancellationToken) ?? new RewriteRefused($"an uncaptured transaction ({store.UncapturedBy}) committed during the rewrite", new Safety());
                        }
                        switch (await store.WriteRewriteAsync(scratch, minimumOffset, replay, target, finalPass: true, final.Token).ConfigureAwait(false))
                        {
                            case TursoDatabaseStore.RewriteWrite finale:
                                offset = finale.Offset;
                                break;
                            case RewriteRefused finalRefused:
                                return BudgetRefused(budget, cancellationToken) ?? finalRefused;
                        }

                        RewriteRefused? replaced = null;
                        try
                        {
                            await store.WriteLane.RunAsync(() =>
                            {
                                replaced = Replace(path, scratch, step, store, final.Token, ref renamed);
                                return true;
                            }, final.Token).ConfigureAwait(false);
                        }
                        catch (Exception error) when (renamed)
                        {
                            store.Rewritten(target);
                            store.NoteCopyFault("a healthy rewrite's rename may not be durable");
                            await store.RecordCopyFaultAsync(store.CopyFault!).ConfigureAwait(false);
                            return new RewriteUncertain(error);
                        }

                        if (replaced is { } replaceRefused)
                        {
                            return BudgetRefused(budget, cancellationToken) ?? replaceRefused;
                        }

                        store.RewroteHealthily(source, target);
                        store.Rewritten(target);
                        return new HealthyRewrite(source, target, offset);
                    }
                    finally
                    {
                        if (held is not null)
                        {
                            await held.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && heldBudget.IsCancellationRequested)
                {
                    return new RewriteRefused("the final rewrite delta exceeded its time budget", new Budget());
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && budget.Expired && error is OperationCanceledException)
                {
                    return new RewriteRefused(budget.Reason is Pressure
                        ? "the off-writer rewrite stopped under storage or memory pressure"
                        : "the off-writer rewrite exceeded its total deadline", budget.Reason);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return new RewriteRefused("the rewrite failed; the old file stays in place", new Safety());
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
        finally
        {
            Io.Release();
        }

        if (outcome is RewriteRefused refused)
        {
            _rewriteRefused.Add(1, new KeyValuePair<string, object?>("reason", refused.Reason.Label));
            _logger.ZLogInformation($"Healthy rewrite refused: {refused.Reason.Label} ({refused.Message})");
        }
        else if (outcome is RewriteUncertain uncertain)
        {
            _rewriteRefused.Add(1, new KeyValuePair<string, object?>("reason", "Uncertain"));
            _logger.ZLogError(uncertain.Cause, $"the rewritten file is in place but its rename may not be durable; storage failed closed");
        }

        return outcome;
    }

    private static RewriteRefused? BudgetRefused(RewriteBudget budget, CancellationToken cancellationToken) =>
        !budget.Expired || cancellationToken.IsCancellationRequested ? null : new RewriteRefused(budget.Reason is Pressure
            ? "the off-writer rewrite stopped under storage or memory pressure"
            : "the off-writer rewrite exceeded its total deadline", budget.Reason);

    private RewriteRefused? Replace(string path, string scratch, TenantContext step, TursoDatabaseStore store, CancellationToken token, ref bool renamed)
    {
        var directory = Path.GetDirectoryName(path)!;
        RejectDatabaseLinks(path);
        RejectDatabaseLinks(scratch);
        foreach (var file in new[] { path, scratch })
        {
            foreach (var sidecar in Sidecars.Where(sidecar => sidecar is not (DatabaseKeys.Suffix or RevisionManifestFile.Suffix or RevisionManifestFile.StagedSuffix)))
            {

                if (new FileInfo(file + sidecar) is { Exists: true } log && log.Length != 0 && sidecar != "-shm")
                {
                    return new RewriteRefused("a database log still holds frames", new Safety());
                }
            }
        }

        token.ThrowIfCancellationRequested();
        using var mutation = store.EnterMutation();
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
            store.ThrowIfUnavailable();
            File.Move(scratch, path, overwrite: true);
        }
        renamed = true;
        _checkpoint("rewrite.replaced", step);

        using (store.MeasureRewrite("directory_fsync_after"))
        {
            DirectorySync(directory);
        }

        return null;
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

    private static void SyncDirectory(string directory) => TerraBase.Primitives.DirectorySync.Sync(directory);
}
