using System.Collections.Concurrent;
using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.FileStorage;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Hosting;
using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private const int ForwardedAtOnce = 8;

    private static readonly TimeSpan ForwardPassBudget = TimeSpan.FromSeconds(10);

    private readonly HashSet<string> _platformClosuresReported = [with(StringComparer.Ordinal)];

    private string? _closuresAfter;

    private string? _closureRegionsAfter;

    private long _closuresPending;

    private long _closuresOldestTicks;

    private long _closuresPassPending;

    private long _closuresPassOldestTicks;

    private void RegisterClosureMetrics()
    {
        _recoveryMeter.CreateObservableGauge("terrabase.project_closure.pending", () => Interlocked.Read(ref _closuresPending),
            description: "Project closures sent to other regions that this machine or one of those regions has not confirmed.");
        _recoveryMeter.CreateObservableGauge("terrabase.project_closure.oldest_pending_age_seconds", () =>
        {
            var ticks = Interlocked.Read(ref _closuresOldestTicks);
            return ticks == 0 ? 0 : Math.Max(0, (time.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero)).TotalSeconds);
        }, description: "Seconds since the oldest of those unconfirmed project closures was requested.");
    }

    public async ValueTask<CloseProjectResult> CloseProjectAsync(
        string projectId, string closureId, string authorityOrganizationId, int version, bool forwarded, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatformProject(projectId))
        {
            return ManagementErrors.PlatformProjectProtected;
        }

        var regions = await closureFanout.OtherRegionsAsync(cancellationToken).ConfigureAwait(false);
        if (version == 1 && !forwarded && regions.Count > 0
            && await RefusedBeforeFenceAsync(projectId, new(version, closureId, authorityOrganizationId), regions, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        var result = await FenceProjectAsync(projectId, closureId, authorityOrganizationId, version, regions, forwarded, cancellationToken).ConfigureAwait(false);
        if (result is not ProjectClosureStatus { Status: ProjectClosure.Pending })
        {
            return result;
        }

        var until = time.GetUtcNow() + (forwarded ? ForwardedErasureBudget : ErasureBudget);
        await Task.WhenAll(EraseClosingAsync(projectId, until, cancellationToken),
            forwarded ? Task.CompletedTask : AcknowledgeRegionsAsync(projectId, [], new(StringComparer.Ordinal), cancellationToken)).ConfigureAwait(false);
        string? scanned = null;
        while (true)
        {
            var step = await WriteAsync<ClosureStep>(transaction => RefreshClosureAsync(transaction, projectId, forwarded, cancellationToken),
                static error => new(error, null), cancellationToken).ConfigureAwait(false);
            if (step.Result is not ProjectClosureStatus { Status: ProjectClosure.Pending } || step.Scan is null or "complete" || step.Scan == scanned
                || time.GetUtcNow() >= until)
            {
                return step.Result;
            }

            scanned = step.Scan;
        }
    }

    public async ValueTask<CloseProjectResult> InspectClosureAsync(
        string projectId, string closureId, string authorityOrganizationId, int version, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatformProject(projectId))
        {
            return ManagementErrors.PlatformProjectProtected;
        }

        return await ReadAsync<CloseProjectResult>(async transaction =>
        {
            var existing = await GetAsync(transaction, ProjectClosure.Kind, projectId, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false);
            if (existing is not null && ClosureConflicts(existing, projectId, closureId, authorityOrganizationId, version))
            {
                return ProjectClosure.ClosureConflict;
            }

            if (!await ClosureOrganizationMatchesAsync(transaction, projectId, authorityOrganizationId, cancellationToken).ConfigureAwait(false))
            {
                return ProjectClosure.BindingConflict;
            }

            if (existing is not null)
            {
                return await ClosureStatusAsync(transaction, existing, forwarded: true, cancellationToken).ConfigureAwait(false);
            }

            return version == 1 && !await IsProjectEmptyAsync(transaction, projectId, cancellationToken).ConfigureAwait(false)
                ? ManagementErrors.NotEmpty
                : new ProjectClosureStatus(ProjectClosure.Empty, closureId, 0);
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CloseProjectResult?> RefusedBeforeFenceAsync(
        string projectId, ProjectClosureRequest closure, IReadOnlyList<string> regions, CancellationToken cancellationToken)
    {
        var local = await InspectClosureAsync(projectId, closure.ClosureId!, closure.AuthorityOrganizationId!, closure.Version, cancellationToken).ConfigureAwait(false);
        if (local is not ProjectClosureStatus status)
        {
            return local;
        }

        if (status.Status != ProjectClosure.Empty)
        {
            return null;
        }

        var answers = await Task.WhenAll(regions.Select(region => closureFanout.CheckAsync(region, projectId, closure, cancellationToken))).ConfigureAwait(false);
        if (answers.Contains(RegionAnswer.PlatformProjectProtected))
        {
            return ManagementErrors.PlatformProjectProtected;
        }

        if (answers.Contains(RegionAnswer.NotEmpty))
        {
            return ManagementErrors.NotEmpty;
        }

        if (answers.Contains(RegionAnswer.ClosureConflict))
        {
            return ProjectClosure.ClosureConflict;
        }

        if (answers.Contains(RegionAnswer.BindingConflict))
        {
            return ProjectClosure.BindingConflict;
        }

        if (answers.All(answer => answer is RegionAnswer.Empty or RegionAnswer.Accepted or RegionAnswer.Pending))
        {
            return null;
        }

        return ProjectClosure.RegionUnanswered;
    }

    private static bool ClosureConflicts(ProjectClosureRecord existing, string projectId, string closureId, string authorityOrganizationId, int version) =>
        existing.FormatVersion is not (1 or 2) || version < existing.FormatVersion || existing.ProjectId != projectId || existing.ClosureId != closureId
        || existing.AuthorityOrganizationId != authorityOrganizationId;

    private async Task EraseClosingAsync(string projectId, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await EraseClosingDatabasesAsync(projectId, until, cancellationToken).ConfigureAwait(false);
        await EraseClosingFilesAsync(projectId, until, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ClosureStep> RefreshClosureAsync(IStorageTransaction transaction, string projectId, bool forwarded, CancellationToken cancellationToken)
    {
        var record = await GetAsync(transaction, ProjectClosure.Kind, projectId, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("a project closure fence is missing");
        await RefreshClosureReceiptAsync(transaction, record, cancellationToken).ConfigureAwait(false);
        return new(await ClosureStatusAsync(transaction, record, forwarded, cancellationToken).ConfigureAwait(false),
            await GetAsync(transaction, ProjectClosure.Scans(projectId), options.MachineId, Id, cancellationToken).ConfigureAwait(false));
    }

    private sealed record ClosureStep(CloseProjectResult Result, string? Scan);

    private async ValueTask<CloseProjectResult> FenceProjectAsync(
        string projectId, string closureId, string authorityOrganizationId, int version, IReadOnlyList<string> regions, bool forwarded, CancellationToken cancellationToken) =>
        await WriteAsync<CloseProjectResult>(async transaction =>
        {
            if (await GetAsync(transaction, ProjectClosure.Kind, projectId, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                if (ClosureConflicts(existing, projectId, closureId, authorityOrganizationId, version))
                {
                    return ProjectClosure.ClosureConflict;
                }

                if (!await ClosureOrganizationMatchesAsync(transaction, projectId, authorityOrganizationId, cancellationToken).ConfigureAwait(false))
                {
                    return ProjectClosure.BindingConflict;
                }

                if (version > existing.FormatVersion)
                {
                    existing = existing with { FormatVersion = version };
                    await PutAsync(transaction, ProjectClosure.Kind, projectId, existing, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false);
                }

                await RefreshClosureReceiptAsync(transaction, existing, cancellationToken).ConfigureAwait(false);
                return await ClosureStatusAsync(transaction, existing, forwarded, cancellationToken).ConfigureAwait(false);
            }

            if (!await ClosureOrganizationMatchesAsync(transaction, projectId, authorityOrganizationId, cancellationToken).ConfigureAwait(false))
            {
                return ProjectClosure.BindingConflict;
            }

            if (version == 1 && !await IsProjectEmptyAsync(transaction, projectId, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotEmpty;
            }

            var record = new ProjectClosureRecord(projectId, closureId, authorityOrganizationId, time.GetUtcNow(),
                [.. CatalogHolders(options.MachineId).Concat(regions.Select(ProjectClosure.RegionHolder)).Order(StringComparer.Ordinal)], version);
            await PutAsync(transaction, ProjectClosure.Kind, projectId, record, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false);
            await RefreshClosureReceiptAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            logger.ZLogInformation($"Project {projectId:@Project} is fenced for closure {closureId:@Closure}");
            return await ClosureStatusAsync(transaction, record, forwarded, cancellationToken).ConfigureAwait(false);
        }, static error => error, cancellationToken).ConfigureAwait(false);

    private async ValueTask<bool> ClosureOrganizationMatchesAsync(IStorageTransaction transaction, string project, string authorityOrganization, CancellationToken token)
    {
        if (await CatalogTables.ProjectOrganizationAsync(transaction, project, token).ConfigureAwait(false) is not { } namespaceId)
        {
            return true;
        }

        return namespaceId == authorityOrganization
            && await IsProjectRegisteredAsync(transaction, project, namespaceId, token).ConfigureAwait(false);
    }

    private static ValueTask<bool> IsProjectClosedAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken) =>
        ProjectClosure.IsClosedAsync(transaction, projectId, cancellationToken);

    private static async ValueTask<bool> IsProjectEmptyAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken) =>
        !await CatalogTables.HasDatabasesAsync(transaction, projectId, cancellationToken).ConfigureAwait(false)
        && !await AnyAsync(transaction, EngineApiKeyStore.ProjectKeys(projectId), cancellationToken).ConfigureAwait(false)
        && !await AnyAsync(transaction, ProjectBackupDestinations.Kind(projectId), cancellationToken).ConfigureAwait(false)
        && !await AnyAsync(transaction, FileStorageUsage.Buckets(projectId), cancellationToken).ConfigureAwait(false);

    internal static HashSet<string> CatalogHolders(string machineId) =>
        new([machineId], StringComparer.Ordinal);

    private async ValueTask RefreshClosureReceiptAsync(IStorageTransaction transaction, ProjectClosureRecord record, CancellationToken cancellationToken)
    {
        record = await GetAsync(transaction, ProjectClosure.Kind, record.ProjectId, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("a project closure fence is missing");
        var current = await OwnClosureReceiptAsync(transaction, record, cancellationToken).ConfigureAwait(false);
        var empty = record.FormatVersion == 2
            ? await EraseProjectBatchAsync(transaction, record, cancellationToken).ConfigureAwait(false)
            : await IsProjectEmptyAsync(transaction, record.ProjectId, cancellationToken).ConfigureAwait(false)
                && await EraseRetainedDatabasesAsync(transaction, record.ProjectId, cancellationToken).ConfigureAwait(false)
                && await VerifyProjectPurgesAsync(transaction, record.ProjectId, cancellationToken).ConfigureAwait(false);
        if (current is not null && current.EvidenceVersion == 2 && current.Empty == empty && current.ClosureId == record.ClosureId && current.FormatVersion == record.FormatVersion)
        {
            return;
        }

        await PutAsync(transaction, ProjectClosure.Receipts(record.ProjectId), options.MachineId,
            new ProjectClosureReceipt(record.ProjectId, record.ClosureId, options.MachineId, empty, time.GetUtcNow(), record.FormatVersion, EvidenceVersion: 2), ManagementJsonContext.Default.ProjectClosureReceipt, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<bool> IsClosureSettledAsync(IStorageTransaction transaction, ProjectClosureRecord record, CancellationToken cancellationToken)
    {
        var project = record.ProjectId;
        return await GetAsync(transaction, ProjectClosure.Kind, project, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken)
                .ConfigureAwait(false) is { } current
            && await OwnClosureReceiptAsync(transaction, current, cancellationToken).ConfigureAwait(false) is { EvidenceVersion: 2, Empty: true } receipt
            && receipt.ClosureId == current.ClosureId && receipt.FormatVersion == current.FormatVersion
            && await ClosureOrganizationMatchesAsync(transaction, project, current.AuthorityOrganizationId, cancellationToken).ConfigureAwait(false)
            && await GetAsync(transaction, ProjectClosure.Scans(project), options.MachineId, Id, cancellationToken).ConfigureAwait(false) == "complete"
            && !await AnyAsync(transaction, ProjectClosure.Purges(project), cancellationToken).ConfigureAwait(false)
            && await IsProjectEmptyAsync(transaction, project, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ProjectClosureReceipt?> OwnClosureReceiptAsync(IStorageTransaction transaction, ProjectClosureRecord record, CancellationToken cancellationToken) =>
        GetAsync(transaction, ProjectClosure.Receipts(record.ProjectId), options.MachineId, ManagementJsonContext.Default.ProjectClosureReceipt, cancellationToken);

    private async ValueTask<ProjectClosureStatus> ClosureStatusAsync(IStorageTransaction transaction, ProjectClosureRecord record, bool forwarded, CancellationToken cancellationToken)
    {
        var holders = CatalogHolders(options.MachineId);
        if (!forwarded)
        {
            holders.UnionWith(record.ExpectedHolders);
        }

        var pending = 0;
        List<string> notEmpty = [];
        foreach (var holder in holders)
        {
            var receipt = await GetAsync(transaction, ProjectClosure.Receipts(record.ProjectId), holder, ManagementJsonContext.Default.ProjectClosureReceipt, cancellationToken)
                .ConfigureAwait(false);
            if (receipt is not null && receipt.Confirms(record, holder))
            {
                continue;
            }

            pending++;
            if (receipt is not null && ProjectClosure.IsRegionHolder(holder) && receipt.Refuses(record, holder))
            {
                notEmpty.Add(ProjectClosure.HolderRegion(holder));
            }
        }

        return new ProjectClosureStatus(pending == 0 ? ProjectClosure.Accepted : ProjectClosure.Pending, record.ClosureId, pending)
        {
            NotEmptyRegions = [.. notEmpty.Order(StringComparer.Ordinal)],
        };
    }

    private async Task<ClosureSeen> AcknowledgeRegionsAsync(
        string projectId, IReadOnlyList<string> serving, ConcurrentDictionary<string, bool> silent, CancellationToken cancellationToken)
    {
        var waiting = await ReadAsync<WaitingClosure?>(transaction => WaitingClosureAsync(transaction, projectId, cancellationToken), static _ => null, cancellationToken)
            .ConfigureAwait(false);
        if (waiting is null)
        {
            return new(Pending: false, null);
        }

        if (waiting.Pending && waiting.Closure.ExpectedHolders.Any(ProjectClosure.IsRegionHolder)
            && serving.Any(region => !waiting.Closure.ExpectedHolders.Contains(ProjectClosure.RegionHolder(region))))
        {
            waiting = await WriteAsync<WaitingClosure?>(transaction => NameServingRegionsAsync(transaction, waiting.Closure, serving, cancellationToken),
                static _ => null, cancellationToken).ConfigureAwait(false) ?? waiting;
        }

        var closure = waiting.Closure;
        string[] asked = [.. waiting.Regions.Where(region => !silent.ContainsKey(region))];
        var answers = await Task.WhenAll(asked.Select(region => closureFanout.SendAsync(region, closure, cancellationToken))).ConfigureAwait(false);
        for (var index = 0; index < asked.Length; index++)
        {
            var region = asked[index];
            switch (answers[index])
            {
                case RegionAnswer.Accepted:
                    _ = await WriteAsync<bool>(transaction => RecordRegionReceiptAsync(transaction, closure, region, empty: true, cancellationToken),
                        static _ => false, cancellationToken).ConfigureAwait(false);
                    break;
                case RegionAnswer.NotEmpty:
                    if (await WriteAsync<bool>(transaction => RecordRegionReceiptAsync(transaction, closure, region, empty: false, cancellationToken),
                        static _ => false, cancellationToken).ConfigureAwait(false))
                    {
                        closureFanout.Refused(projectId, region);
                    }

                    break;
                case RegionAnswer.NotListed or RegionAnswer.TimedOut or RegionAnswer.Unreachable:
                    _ = silent.TryAdd(region, true);
                    break;
                case RegionAnswer.Pending or RegionAnswer.Empty or RegionAnswer.ClosureConflict or RegionAnswer.BindingConflict or RegionAnswer.PlatformProjectProtected
                    or RegionAnswer.Refused or RegionAnswer.Invalid:
                    break;
                default:
                    throw new UnreachableException();
            }
        }

        return new(waiting.Pending, closure.RequestedAt);
    }

    private async ValueTask<WaitingClosure?> WaitingClosureAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken)
    {
        if (await GetAsync(transaction, ProjectClosure.Kind, projectId, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false)
            is not { FormatVersion: 1 or 2 } closure || closure.ProjectId != projectId)
        {
            return null;
        }

        List<string> regions = [];
        foreach (var holder in closure.ExpectedHolders.Where(ProjectClosure.IsRegionHolder))
        {
            var receipt = await GetAsync(transaction, ProjectClosure.Receipts(projectId), holder, ManagementJsonContext.Default.ProjectClosureReceipt, cancellationToken)
                .ConfigureAwait(false);
            if (receipt is null || !receipt.Confirms(closure, holder))
            {
                regions.Add(ProjectClosure.HolderRegion(holder));
            }
        }

        var own = await OwnClosureReceiptAsync(transaction, closure, cancellationToken).ConfigureAwait(false);
        return new(closure, regions, regions.Count > 0 || own is null || !own.Confirms(closure, options.MachineId));
    }

    private async ValueTask<WaitingClosure?> NameServingRegionsAsync(
        IStorageTransaction transaction, ProjectClosureRecord seen, IReadOnlyList<string> serving, CancellationToken cancellationToken)
    {
        if (await WaitingClosureAsync(transaction, seen.ProjectId, cancellationToken).ConfigureAwait(false) is not { Pending: true } waiting
            || waiting.Closure.ClosureId != seen.ClosureId || !waiting.Closure.ExpectedHolders.Any(ProjectClosure.IsRegionHolder))
        {
            return null;
        }

        string[] holders = [.. waiting.Closure.ExpectedHolders.Union(serving.Select(ProjectClosure.RegionHolder), StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (holders.Length == waiting.Closure.ExpectedHolders.Length)
        {
            return waiting;
        }

        await PutAsync(transaction, ProjectClosure.Kind, seen.ProjectId, waiting.Closure with { ExpectedHolders = holders }, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken)
            .ConfigureAwait(false);
        logger.ZLogInformation($"The pending closure of project {seen.ProjectId:@Project} now names every serving region");
        return await WaitingClosureAsync(transaction, seen.ProjectId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> RecordRegionReceiptAsync(IStorageTransaction transaction, ProjectClosureRecord sent, string region, bool empty, CancellationToken cancellationToken)
    {
        var holder = ProjectClosure.RegionHolder(region);
        if (await GetAsync(transaction, ProjectClosure.Kind, sent.ProjectId, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false) is not { } current
            || current.ProjectId != sent.ProjectId || current.ClosureId != sent.ClosureId || current.AuthorityOrganizationId != sent.AuthorityOrganizationId
            || current.FormatVersion != sent.FormatVersion || !current.ExpectedHolders.Contains(holder))
        {
            return false;
        }

        if (await GetAsync(transaction, ProjectClosure.Receipts(sent.ProjectId), holder, ManagementJsonContext.Default.ProjectClosureReceipt, cancellationToken).ConfigureAwait(false) is { } known
            && (empty ? known.Confirms(current, holder) : known.Refuses(current, holder)))
        {
            return false;
        }

        await PutAsync(transaction, ProjectClosure.Receipts(sent.ProjectId), holder,
            new ProjectClosureReceipt(sent.ProjectId, sent.ClosureId, holder, empty, time.GetUtcNow(), sent.FormatVersion, EvidenceVersion: 2),
            ManagementJsonContext.Default.ProjectClosureReceipt, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private sealed record WaitingClosure(ProjectClosureRecord Closure, IReadOnlyList<string> Regions, bool Pending);

    private sealed record ClosureSeen(bool Pending, DateTimeOffset? RequestedAt);

    internal async ValueTask ForwardClosuresAsync(CancellationToken cancellationToken)
    {
        if (!catalogGate.Value.IsOpen)
        {
            return;
        }

        try
        {
            var page = await ReadAsync<RecordPage?>(async transaction => await transaction.ListAsync(ProjectClosure.Kind, _closureRegionsAfter,
                CatalogPage, MaxPageBytes, cancellationToken).ConfigureAwait(false), static _ => null, cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                return;
            }

            var serving = await closureFanout.OtherRegionsAsync(cancellationToken).ConfigureAwait(false);
            var seen = new ClosureSeen?[page.Records.Count];
            var handled = await ForwardPageAsync(page.Records, serving, seen, cancellationToken).ConfigureAwait(false);
            foreach (var closure in seen.Take(handled))
            {
                if (closure is { Pending: true })
                {
                    _closuresPassPending++;
                    if (closure.RequestedAt is { UtcTicks: var requested })
                    {
                        _closuresPassOldestTicks = _closuresPassOldestTicks == 0 ? requested : Math.Min(_closuresPassOldestTicks, requested);
                    }
                }
            }

            if (handled < page.Records.Count)
            {
                if (handled > 0)
                {
                    _closureRegionsAfter = page.Records[handled - 1].Id;
                }

                return;
            }

            if (page.HasMore)
            {
                _closureRegionsAfter = page.Records[^1].Id;
                return;
            }

            Interlocked.Exchange(ref _closuresPending, _closuresPassPending);
            Interlocked.Exchange(ref _closuresOldestTicks, _closuresPassOldestTicks);
            _closureRegionsAfter = null;
            _closuresPassPending = _closuresPassOldestTicks = 0;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            closureFanout.Failed(null, error);
        }
    }

    private async ValueTask<int> ForwardPageAsync(
        IReadOnlyList<StoredRecord> closures, IReadOnlyList<string> serving, ClosureSeen?[] seen, CancellationToken cancellationToken)
    {
        ConcurrentDictionary<string, bool> silent = new(StringComparer.Ordinal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ForwardPassBudget);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, closures.Count), new ParallelOptions { MaxDegreeOfParallelism = ForwardedAtOnce, CancellationToken = deadline.Token },
                async (index, token) =>
                {
                    try
                    {
                        seen[index] = !PlatformDatabases.IsPlatformProject(closures[index].Id) && NamesRegion(closures[index])
                            ? await AcknowledgeRegionsAsync(closures[index].Id, serving, silent, token).ConfigureAwait(false)
                            : new(Pending: false, null);
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        closureFanout.Failed(closures[index].Id, error);
                        seen[index] = new(Pending: true, null);
                    }
                }).ConfigureAwait(false);
            return closures.Count;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return seen.TakeWhile(closure => closure is not null).Count();
        }
    }

    private static bool NamesRegion(StoredRecord stored) =>
        EntityPayload.Read(stored, ManagementJsonContext.Default.ProjectClosureRecord) is { } closure && closure.ExpectedHolders.Any(ProjectClosure.IsRegionHolder);

    private async ValueTask ConvergeClosuresAsync(CancellationToken cancellationToken)
    {
        var page = await ReadAsync<RecordPage?>(async transaction => await transaction.ListAsync(ProjectClosure.Kind, _closuresAfter,
            CatalogPage, MaxPageBytes, cancellationToken).ConfigureAwait(false), static _ => null, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return;
        }

        foreach (var stored in page.Records)
        {
            if (PlatformDatabases.IsPlatformProject(stored.Id))
            {
                if (_platformClosuresReported.Add(stored.Id))
                {
                    logger.ZLogError($"The closure record of platform project {stored.Id:@Project} is never converged; nothing of that project is erased: {ManagementErrors.PlatformProjectProtected.Code:@Code}");
                }

                continue;
            }

            try
            {
                var record = EntityPayload.Read(stored, ManagementJsonContext.Default.ProjectClosureRecord);
                if (record is not { FormatVersion: 1 or 2 } || record.ProjectId != stored.Id)
                {
                    throw new InvalidOperationException("a project closure record has unsupported identity or format");
                }

                if (await ReadAsync(transaction => IsClosureSettledAsync(transaction, record, cancellationToken), static _ => false, cancellationToken)
                    .ConfigureAwait(false))
                {
                    continue;
                }

                await EraseClosingFilesAsync(record.ProjectId, time.GetUtcNow() + ErasureBudget, cancellationToken).ConfigureAwait(false);
                _ = await WriteAsync<Done>(async transaction =>
                {
                    await RefreshClosureReceiptAsync(transaction, record, cancellationToken).ConfigureAwait(false);
                    return new Done();
                }, static _ => new Done(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Confirming project closure {stored.Id:@Project} failed; the next pass tries again");
            }
        }

        _closuresAfter = page.HasMore ? page.Records[^1].Id : null;
    }
}
