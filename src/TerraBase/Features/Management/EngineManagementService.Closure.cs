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
    private string? _closuresAfter;

    public async ValueTask<CloseProjectResult> CloseProjectAsync(string projectId, string closureId, string authorityOrganizationId, int version, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatformProject(projectId))
        {
            return ManagementErrors.PlatformProjectProtected;
        }

        var result = await FenceProjectAsync(projectId, closureId, authorityOrganizationId, version, cancellationToken).ConfigureAwait(false);
        if (result is not ProjectClosureStatus { Status: ProjectClosure.Pending })
        {
            return result;
        }

        var until = time.GetUtcNow() + ErasureBudget;
        await EraseClosingDatabasesAsync(projectId, until, cancellationToken).ConfigureAwait(false);
        await EraseClosingFilesAsync(projectId, until, cancellationToken).ConfigureAwait(false);
        string? scanned = null;
        while (true)
        {
            var step = await WriteAsync<ClosureStep>(transaction => RefreshClosureAsync(transaction, projectId, cancellationToken),
                static error => new(error, null), cancellationToken).ConfigureAwait(false);
            if (step.Result is not ProjectClosureStatus { Status: ProjectClosure.Pending } || step.Scan is null or "complete" || step.Scan == scanned)
            {
                return step.Result;
            }

            scanned = step.Scan;
        }
    }

    private async ValueTask<ClosureStep> RefreshClosureAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken)
    {
        var record = await GetAsync(transaction, ProjectClosure.Kind, projectId, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("a project closure fence is missing");
        await RefreshClosureReceiptAsync(transaction, record, cancellationToken).ConfigureAwait(false);
        return new(await ClosureStatusAsync(transaction, record, cancellationToken).ConfigureAwait(false),
            await GetAsync(transaction, ProjectClosure.Scans(projectId), options.MachineId, Id, cancellationToken).ConfigureAwait(false));
    }

    private sealed record ClosureStep(CloseProjectResult Result, string? Scan);

    private async ValueTask<CloseProjectResult> FenceProjectAsync(string projectId, string closureId, string authorityOrganizationId, int version, CancellationToken cancellationToken) =>
        await WriteAsync<CloseProjectResult>(async transaction =>
        {
            if (await GetAsync(transaction, ProjectClosure.Kind, projectId, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                if (existing.FormatVersion is not (1 or 2) || version < existing.FormatVersion || existing.ProjectId != projectId || existing.ClosureId != closureId || existing.AuthorityOrganizationId != authorityOrganizationId)
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
                return await ClosureStatusAsync(transaction, existing, cancellationToken).ConfigureAwait(false);
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
                [.. CatalogHolders(options.MachineId).Order(StringComparer.Ordinal)], version);
            await PutAsync(transaction, ProjectClosure.Kind, projectId, record, ManagementJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false);
            await RefreshClosureReceiptAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            logger.ZLogInformation($"Project {projectId:@Project} is fenced for closure {closureId:@Closure}");
            return await ClosureStatusAsync(transaction, record, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<ProjectClosureStatus> ClosureStatusAsync(IStorageTransaction transaction, ProjectClosureRecord record, CancellationToken cancellationToken)
    {
        var holders = CatalogHolders(options.MachineId);
        holders.UnionWith(record.ExpectedHolders);
        var pending = 0;
        foreach (var holder in holders)
        {
            var receipt = await GetAsync(transaction, ProjectClosure.Receipts(record.ProjectId), holder, ManagementJsonContext.Default.ProjectClosureReceipt, cancellationToken)
                .ConfigureAwait(false);
            if (receipt is not { Empty: true, EvidenceVersion: 2 } || receipt.FormatVersion != record.FormatVersion || receipt.MachineId != holder || receipt.ProjectId != record.ProjectId
                || receipt.ClosureId != record.ClosureId)
            {
                pending++;
            }
        }

        return new ProjectClosureStatus(pending == 0 ? ProjectClosure.Accepted : ProjectClosure.Pending, record.ClosureId, pending);
    }

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
